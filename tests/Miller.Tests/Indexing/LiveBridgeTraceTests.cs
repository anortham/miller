using Microsoft.Data.Sqlite;
using Miller.Core.Graph;
using Miller.Core.Resolver;
using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// M4 Phase D SHIPPABLE GATE (design §7 layers 5+6, §9 final two checkboxes). Drives the REAL
/// <c>julie-extract</c> over throwaway polyglot fixtures, builds the index + cross-language bridge
/// graph through the single production path (<see cref="RepositoryIndexLoader.Load"/>), and asserts against
/// the actual <see cref="BridgeGraph"/> — not a mock.
///
/// <para>Two probes:</para>
/// <list type="number">
///   <item><b>Disciplined fixture</b> (<see cref="DisciplinedFixture_AllThreeLegs_KnownBridgeSet"/>): a
///   by-construction-known bridge set across all three buildable legs (entity→table via DbSet, DTO↔entity via
///   CreateMap, route via axios verb + [controller] expansion). Asserts WHICH entity→table, WHICH CreateMap
///   pair, WHICH route, with the expected score bands from the real scorer.</item>
///   <item><b>Honesty probe</b> (<see cref="HonestyProbe_UndisciplinedFixture_GuardsHold_PrecisionAndRecall"/>):
///   a deliberately undisciplined fixture. Computes precision, proves corroborator-only + ambiguous-name-never-High
///   from the scored payload, and measures recall PER LEG against the buildable ground truth (Dapper-FROM excluded
///   from the denominator — it is not buildable on the lean contract; see <see cref="BridgeGraphBuilder"/>).</item>
/// </list>
///
/// <para>Subprocess + extraction will not fit the &lt;10s default budget, so this is
/// <c>[Trait("Category","Scale")]</c> and EXCLUDED by the default fast suite. The single launch signal is
/// <see cref="ScaleTestSupport.RequireJulieServer"/>: if <c>.tools/julie-extract</c> is absent the test SKIPS
/// (never fails) with an actionable message. No private locator — the
/// <see cref="Conventions.ScaleTraitConventionTests"/> drift guard keys on this one signal.</para>
/// </summary>
[Trait("Category", "Scale")]
public sealed class LiveBridgeTraceTests
{
    private readonly ITestOutputHelper _output;

    public LiveBridgeTraceTests(ITestOutputHelper output) => _output = output;

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Probe 1: disciplined polyglot fixture — a known, enumerable bridge set across all three legs.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DisciplinedFixture_AllThreeLegs_KnownBridgeSet()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteDisciplinedFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var graph = index.BridgeGraph;

        // ── Leg 3 (entity→table): EF DbSet<AppSetting> property "AppSettings" ⇒ AppSetting StoredIn AppSettings.
        // The DbSet always supplies a DbSetProperty structural breadcrumb ⇒ High band (BridgeScorer §5).
        string appSettingId = FindSymbolId(index, "AppSetting", "class");
        var storedIn = SingleEdgeOfKind(graph, appSettingId, BridgeKind.StoredIn);
        Assert.Equal("AppSetting", EndpointDisplayOf(graph, storedIn, EndpointSide.Source));
        Assert.Equal("AppSettings", EndpointDisplayOf(graph, storedIn, EndpointSide.Target));
        Assert.Equal(ConfidenceBand.High, storedIn.Band);
        // NEGATIVE (design §7): the table side is the DbSet PROPERTY name, NOT the DbContext class AppDbContext.
        string appSettingsTableId = BridgeGraph.SynthesizeId(BridgeNodeKind.DbTable, "AppSettings");
        Assert.True(graph.Contains(appSettingsTableId), "entity must bridge to the table node");
        Assert.DoesNotContain(
            graph.Walk(appSettingId, 2),
            e => string.Equals(EndpointDisplayOf(graph, e, EndpointSide.Target), "AppDbContext", StringComparison.Ordinal));

        // ── Leg 2 (DTO↔entity): CreateMap<AppSetting, AppSettingDto>() ⇒ MapsTo edge between the two C# shapes.
        // CreateMap alone = a structural breadcrumb ⇒ High band. Both sides resolve (unique names) so not ambiguous.
        string dtoId = FindSymbolId(index, "AppSettingDto", "class");
        var mapsTo = SingleEdgeOfKind(graph, dtoId, BridgeKind.MapsTo);
        Assert.Equal(ConfidenceBand.High, mapsTo.Band);
        Assert.False(mapsTo.HasAmbiguousName, "AppSetting/AppSettingDto are unique names; the map must not be ambiguous");
        var mapNames = new[]
        {
            EndpointDisplayOf(graph, mapsTo, EndpointSide.Source),
            EndpointDisplayOf(graph, mapsTo, EndpointSide.Target),
        };
        Assert.Contains("AppSetting", mapNames);
        Assert.Contains("AppSettingDto", mapNames);

        // ── Leg 1 (route): TS axios.get<AppSettingDto>('/api/appsettings/{id}') ⇒ Hits the [controller]-expanded GET endpoint.
        // axios.get is a verb-KNOWN carrier ⇒ RouteVerbMatch structural breadcrumb ⇒ High band, NOT verb-unknown.
        string handlerId = FindSymbolId(index, "GetById", "method");
        var hits = SingleEdgeOfKind(graph, handlerId, BridgeKind.Hits);
        Assert.Equal("GetById", EndpointDisplayOf(graph, hits, EndpointSide.Target));
        Assert.False(hits.IsVerbUnknown, "axios.get supplies a known verb; this route edge must not be verb-unknown");
        Assert.False(hits.HasAmbiguousName);
        Assert.Equal(ConfidenceBand.High, hits.Band);
        // The client side is the containing TS function, not the synthetic route node, so agents start from useful code.
        string clientDisplay = EndpointDisplayOf(graph, hits, EndpointSide.Source);
        Assert.Equal("fetchAppSetting", clientDisplay);
        Assert.Equal(2, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientCalls"]);

        // ── End-to-end render proof: the entity→table bridge must render through the real TraceTool.Run with its band.
        var resolver = new SmartTargetResolver(index);
        string rendered = TraceTool.Run(
            index, resolver, target: "AppSetting", mode: "bridge", to: null, depth: 3, limit: 20,
            fullFormat: false, out int emitted, out _);
        Assert.True(emitted > 0, "trace bridge over AppSetting must emit at least the StoredIn link");
        Assert.Contains("AppSetting  --DbSet-->  AppSettings", rendered);
        Assert.Contains("(High)", rendered);

        _output.WriteLine("DISCIPLINED FIXTURE — known bridge set verified:");
        _output.WriteLine($"  Leg3 StoredIn: AppSetting -> AppSettings   band={storedIn.Band} score={Fmt(storedIn.Score)}");
        _output.WriteLine($"  Leg2 MapsTo:   {mapNames[0]} <-> {mapNames[1]}   band={mapsTo.Band} score={Fmt(mapsTo.Score)}");
        _output.WriteLine($"  Leg1 Hits:     {clientDisplay} -> GetById   band={hits.Band} score={Fmt(hits.Score)} verbUnknown={hits.IsVerbUnknown}");
        _output.WriteLine("  Rendered (trace bridge AppSetting):");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void NextFixture_RouteReferenceToFileRoute_TraceBridgeResolves()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteNextFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var patternIds = StructuralPatternIds(work.Db, "nextjs.%");
        Assert.Contains("nextjs.route_reference.v1", patternIds);
        Assert.Contains("nextjs.file_route.v1", patternIds);

        var graph = index.BridgeGraph;
        Assert.Contains("nextjs", graph.CapabilityReport.ActiveProviders);
        var edge = Assert.Single(graph.Edges, e =>
            e.Edge.Kind == BridgeKind.NavigatesTo
            && string.Equals(e.Edge.SourceRef.Display, "/settings", StringComparison.Ordinal)
            && string.Equals(e.Edge.TargetRef.Display, "/settings", StringComparison.Ordinal));
        Assert.Equal(ConfidenceBand.High, edge.Band);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "/settings", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("Nav  --navigates_to-->  /settings", rendered);
        Assert.DoesNotContain("--route-->", rendered);

        _output.WriteLine("NEXT.JS FIXTURE — route reference to file route verified:");
        _output.WriteLine($"  Patterns: {string.Join(", ", patternIds.Order(StringComparer.Ordinal))}");
        _output.WriteLine($"  NavigatesTo: {edge.Edge.SourceRef.Display} -> {edge.Edge.TargetRef.Display} band={edge.Band} score={Fmt(edge.Score)}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void NextFixture_DynamicRouteReferenceToFileRoute_TraceBridgeResolves()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteNextFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var graph = index.BridgeGraph;

        var edge = Assert.Single(graph.Edges, e =>
            e.Edge.Kind == BridgeKind.NavigatesTo
            && string.Equals(e.Edge.SourceRef.Display, "/users/42", StringComparison.Ordinal)
            && string.Equals(e.Edge.TargetRef.FilePath, "app/users/[id]/page.tsx", StringComparison.Ordinal));
        Assert.Equal(ConfidenceBand.High, edge.Band);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "/users/42", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("--navigates_to-->", rendered);

        _output.WriteLine("NEXT.JS FIXTURE - dynamic route reference to file route verified:");
        _output.WriteLine($"  NavigatesTo: {edge.Edge.SourceRef.Display} -> {edge.Edge.TargetRef.Display} file={edge.Edge.TargetRef.FilePath}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void NuxtFixture_RouteReferenceToFileRoute_TraceBridgeResolves()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteNuxtFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var patternIds = StructuralPatternIds(work.Db, "nuxt.%");
        Assert.Contains("nuxt.route_reference.v1", patternIds);
        Assert.Contains("nuxt.file_route.v1", patternIds);

        var graph = index.BridgeGraph;
        Assert.Contains("nuxt", graph.CapabilityReport.ActiveProviders);
        var edge = Assert.Single(graph.Edges, e =>
            e.Edge.Kind == BridgeKind.NavigatesTo
            && string.Equals(e.Edge.SourceRef.Display, "/about", StringComparison.Ordinal)
            && string.Equals(e.Edge.TargetRef.Display, "/about", StringComparison.Ordinal));
        Assert.Equal(ConfidenceBand.High, edge.Band);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "/about", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("--navigates_to-->  /about", rendered);
        Assert.DoesNotContain("--route-->", rendered);

        _output.WriteLine("NUXT FIXTURE — route reference to file route verified:");
        _output.WriteLine($"  Patterns: {string.Join(", ", patternIds.Order(StringComparer.Ordinal))}");
        _output.WriteLine($"  NavigatesTo: {edge.Edge.SourceRef.Display} -> {edge.Edge.TargetRef.Display} band={edge.Band} score={Fmt(edge.Score)}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void NuxtFixture_DynamicRouteReferenceToFileRoute_TraceBridgeResolves()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteNuxtFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var graph = index.BridgeGraph;

        var edge = Assert.Single(graph.Edges, e =>
            e.Edge.Kind == BridgeKind.NavigatesTo
            && string.Equals(e.Edge.SourceRef.Display, "/blog/hello-world", StringComparison.Ordinal)
            && string.Equals(e.Edge.TargetRef.FilePath, "app/pages/blog/[slug].vue", StringComparison.Ordinal));
        Assert.Equal(ConfidenceBand.High, edge.Band);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "/blog/hello-world", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("--navigates_to-->", rendered);

        _output.WriteLine("NUXT FIXTURE - dynamic route reference to file route verified:");
        _output.WriteLine($"  NavigatesTo: {edge.Edge.SourceRef.Display} -> {edge.Edge.TargetRef.Display} file={edge.Edge.TargetRef.FilePath}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void HtmxFixture_DataHxPostAttribute_TraceBridgeResolves()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteHtmxDataPrefixFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var patternIds = StructuralPatternIds(work.Db, "htmx.%");
        Assert.Contains("htmx.attribute.v1", patternIds);

        var graph = index.BridgeGraph;
        string handlerId = FindSymbolId(index, "CreateTodo", "method");
        var hit = Assert.Single(graph.Incident(handlerId), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "CreateTodo", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);

        Assert.True(emitted > 0);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("CreateTodo", rendered);

        _output.WriteLine("HTMX FIXTURE - data-hx-post attribute to POST endpoint verified:");
        _output.WriteLine($"  Hits: {EndpointDisplayOf(graph, hit, EndpointSide.Source)} -> {EndpointDisplayOf(graph, hit, EndpointSide.Target)}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void VueFixture_RouteReferenceToRouteDefinition_TraceBridgeResolves()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteVueRouteDefinitionFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var patternIds = StructuralPatternIds(work.Db, "vue.%");
        Assert.Contains("vue.route_reference.v1", patternIds);
        Assert.Contains("vue.route_definition.v1", patternIds);

        var graph = index.BridgeGraph;
        Assert.Contains("vue", graph.CapabilityReport.ActiveProviders);
        var edge = Assert.Single(graph.Edges, e =>
            e.Edge.Kind == BridgeKind.NavigatesTo
            && string.Equals(e.Edge.SourceRef.Display, "/users/42", StringComparison.Ordinal)
            && string.Equals(e.Edge.TargetRef.Display, "/users/:id", StringComparison.Ordinal));
        Assert.Equal(ConfidenceBand.High, edge.Band);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "/users/42", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);

        Assert.Equal(1, emitted);
        Assert.Contains("--navigates_to-->", rendered);
        Assert.Contains("/users/:id", rendered);

        _output.WriteLine("VUE FIXTURE - route reference to route definition verified:");
        _output.WriteLine($"  NavigatesTo: {edge.Edge.SourceRef.Display} -> {edge.Edge.TargetRef.Display} file={edge.Edge.TargetRef.FilePath}");
        _output.WriteLine(rendered);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // 2.6.0 HTTP boundary facts, live: http.client_request.v1 → nextjs.route_handler.v1 / nuxt.server_route.v1
    // through the verb-aware nextjs-api / nuxt-api providers, plus htmx-from-TSX → aspnet.attribute_route.v1
    // with live annotation+structural dedupe (plan Task 6).
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NextApiFixture_AttestedPostFetch_HitsPostRouteHandlerSymbol()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteNextApiFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var handlerPatterns = StructuralPatternIds(work.Db, "nextjs.%");
        Assert.Contains("nextjs.route_handler.v1", handlerPatterns);
        var clientPatterns = StructuralPatternIds(work.Db, "http.%");
        Assert.Contains("http.client_request.v1", clientPatterns);

        var graph = index.BridgeGraph;
        Assert.Contains("nextjs-api", graph.CapabilityReport.ActiveProviders);
        // Pure Next.js repo: client requests alone must never activate dotnet-web (backend-evidence gate).
        Assert.DoesNotContain("dotnet-web", graph.CapabilityReport.ActiveProviders);

        // BOTH handler export shapes (function declaration + const arrow) emit route-handler facts live and,
        // with julie-extract 2.6.1+, both carry the exported handler symbol as containing_symbol_id.
        Assert.Equal(2, StructuralFactCount(work.Db, "nextjs.route_handler.v1", "app/api/messages/route.ts"));
        var boundHandlerIds = StructuralFactContainingSymbolIds(
            work.Db, "nextjs.route_handler.v1", "app/api/messages/route.ts");
        Assert.Equal(2, boundHandlerIds.Count);
        Assert.DoesNotContain(boundHandlerIds, string.IsNullOrEmpty);
        Assert.Contains(SymbolIdsInFile(index, "GET", "app/api/messages/route.ts"), boundHandlerIds.Contains);
        Assert.Contains(SymbolIdsInFile(index, "POST", "app/api/messages/route.ts"), boundHandlerIds.Contains);

        // fetch("/api/messages", { method: "POST" }) is verb-attested POST ⇒ exactly ONE High edge into the
        // route file, bound to the POST handler-export SYMBOL (the navigation payoff), NOT a synthetic
        // endpoint node — and the GET export stays unbridged (verb discrimination).
        var hit = Assert.Single(graph.Edges, e =>
            e.Edge.Kind == BridgeKind.Hits
            && string.Equals(e.Edge.TargetRef.FilePath, "app/api/messages/route.ts", StringComparison.Ordinal));
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.False(string.IsNullOrEmpty(hit.Edge.TargetRef.SymbolId));
        Assert.Contains(hit.Edge.TargetRef.SymbolId!, SymbolIdsInFile(index, "POST", "app/api/messages/route.ts"));
        Assert.Equal("POST", EndpointDisplayOf(graph, hit, EndpointSide.Target));
        Assert.Equal("sendMessage", EndpointDisplayOf(graph, hit, EndpointSide.Source));

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "sendMessage", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);

        Assert.True(emitted > 0);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("(High)", rendered);

        _output.WriteLine("NEXT.JS API FIXTURE - attested POST fetch to route-handler export verified:");
        _output.WriteLine($"  Patterns: {string.Join(", ", handlerPatterns.Concat(clientPatterns).Order(StringComparer.Ordinal))}");
        _output.WriteLine($"  Hits: {EndpointDisplayOf(graph, hit, EndpointSide.Source)} -> {EndpointDisplayOf(graph, hit, EndpointSide.Target)} band={hit.Band} score={Fmt(hit.Score)} verbUnknown={hit.IsVerbUnknown}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void NextApiFixture_DynamicRouteClientRequest_HitsBracketRouteHandler()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteNextApiFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var graph = index.BridgeGraph;

        // fetch("/api/users/42") (bare fetch = spec-default GET, verb-known) segment-matches the bracket
        // route route_path=/api/users/[id] and binds to its GET export symbol.
        var hit = Assert.Single(graph.Edges, e =>
            e.Edge.Kind == BridgeKind.Hits
            && string.Equals(e.Edge.TargetRef.FilePath, "app/api/users/[id]/route.ts", StringComparison.Ordinal));
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.False(string.IsNullOrEmpty(hit.Edge.TargetRef.SymbolId));
        Assert.Contains(hit.Edge.TargetRef.SymbolId!, SymbolIdsInFile(index, "GET", "app/api/users/[id]/route.ts"));
        Assert.Equal("GET", EndpointDisplayOf(graph, hit, EndpointSide.Target));
        Assert.Equal("loadUser", EndpointDisplayOf(graph, hit, EndpointSide.Source));

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "loadUser", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);

        Assert.True(emitted > 0);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("(High)", rendered);

        _output.WriteLine("NEXT.JS API FIXTURE - dynamic client request to bracket route handler verified:");
        _output.WriteLine($"  Hits: {EndpointDisplayOf(graph, hit, EndpointSide.Source)} -> {EndpointDisplayOf(graph, hit, EndpointSide.Target)} band={hit.Band} score={Fmt(hit.Score)} verbUnknown={hit.IsVerbUnknown}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void NuxtServerFixture_AxiosAndSuffixlessRoutes_HighAndHonestMedium()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteNuxtServerFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var patternIds = StructuralPatternIds(work.Db, "nuxt.%");
        Assert.Contains("nuxt.server_route.v1", patternIds);
        Assert.Contains("http.client_request.v1", StructuralPatternIds(work.Db, "http.%"));

        var graph = index.BridgeGraph;
        Assert.Contains("nuxt-api", graph.CapabilityReport.ActiveProviders);

        // server/api/messages.get.ts: the filename suffix attests GET; axios.get is verb-known ⇒ High, no flag.
        var messagesHit = Assert.Single(graph.Edges, e =>
            e.Edge.Kind == BridgeKind.Hits
            && string.Equals(e.Edge.TargetRef.FilePath, "server/api/messages.get.ts", StringComparison.Ordinal));
        Assert.Equal(ConfidenceBand.High, messagesHit.Band);
        Assert.False(messagesHit.IsVerbUnknown);

        // server/api/notes.ts: suffix-less handler answers every method — its accepted verb set is not
        // source-attested, so the route-only match stays honest Medium with the verb-unknown flag.
        var notesHit = Assert.Single(graph.Edges, e =>
            e.Edge.Kind == BridgeKind.Hits
            && string.Equals(e.Edge.TargetRef.FilePath, "server/api/notes.ts", StringComparison.Ordinal));
        Assert.Equal(ConfidenceBand.Medium, notesHit.Band);
        Assert.True(notesHit.IsVerbUnknown);

        string renderedHigh = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "fetchMessages", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emittedHigh, out _);
        Assert.True(emittedHigh > 0);
        Assert.Contains("--route-->", renderedHigh);
        Assert.Contains("(High)", renderedHigh);

        string renderedMedium = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "loadNotes", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emittedMedium, out _);
        Assert.True(emittedMedium > 0);
        Assert.Contains("--route-->", renderedMedium);
        Assert.Contains("[verb-unknown]", renderedMedium);
        Assert.Contains("(Medium)", renderedMedium);

        _output.WriteLine("NUXT SERVER FIXTURE - axios GET + suffix-less server route verified:");
        _output.WriteLine($"  Patterns: {string.Join(", ", patternIds.Order(StringComparer.Ordinal))}");
        _output.WriteLine($"  Hits(GET): {EndpointDisplayOf(graph, messagesHit, EndpointSide.Source)} -> {EndpointDisplayOf(graph, messagesHit, EndpointSide.Target)} band={messagesHit.Band} verbUnknown={messagesHit.IsVerbUnknown}");
        _output.WriteLine($"  Hits(suffix-less): {EndpointDisplayOf(graph, notesHit, EndpointSide.Source)} -> {EndpointDisplayOf(graph, notesHit, EndpointSide.Target)} band={notesHit.Band} verbUnknown={notesHit.IsVerbUnknown}");
        _output.WriteLine(renderedHigh);
        _output.WriteLine(renderedMedium);
    }

    [Fact]
    public void HtmxTsxFixture_AttributeRouteController_BridgesAndDedupes()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteHtmxTsxAttributeRouteFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        Assert.Contains("htmx.attribute.v1", StructuralPatternIds(work.Db, "htmx.%"));
        Assert.Contains("aspnet.attribute_route.v1", StructuralPatternIds(work.Db, "aspnet.%"));
        Assert.Contains("http.client_request.v1", StructuralPatternIds(work.Db, "http.%"));
        // 2.6.0 emits the htmx fact from TSX (not just plain HTML/Vue) — pin the emitting file.
        Assert.Contains("web/TodoPanel.tsx", StructuralFactPaths(work.Db, "htmx.attribute.v1"));

        var graph = index.BridgeGraph;
        Assert.True(graph.CapabilityReport.EvidenceCounts["dotnet-web.attributeRoutes"] > 0,
            "the [HttpPost] action must be counted as aspnet.attribute_route.v1 endpoint evidence");
        Assert.True(graph.CapabilityReport.EvidenceCounts["dotnet-web.clientRequests"] >= 1,
            "the axios.post call must be counted as http.client_request.v1 evidence");

        string handlerId = FindSymbolId(index, "CreateTodo", "method");
        var hits = graph.Incident(handlerId).Where(e => e.Edge.Kind == BridgeKind.Hits).ToList();

        // Live dedupe proof: the axios site emits BOTH a legacy url literal and a structural client-request
        // fact, and the [HttpPost] action emits BOTH an annotation endpoint and an aspnet.attribute_route.v1
        // fact — the graph must still hold exactly ONE Hits edge per (client, endpoint) pair: the htmx TSX
        // button and the axios TS helper, nothing duplicated.
        Assert.Equal(2, hits.Count);
        foreach (var group in hits.GroupBy(
                     e => BridgeGraph.NodeIdOf(e.Edge.SourceRef, e.Edge.Kind, EndpointSide.Source) ?? string.Empty,
                     StringComparer.Ordinal))
        {
            Assert.Single(group);
        }

        foreach (var hit in hits)
        {
            Assert.Equal(ConfidenceBand.High, hit.Band);
            Assert.False(hit.IsVerbUnknown);
        }

        var sources = hits
            .Select(e => EndpointDisplayOf(graph, e, EndpointSide.Source))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Contains("createTodo", sources);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "CreateTodo", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);

        Assert.True(emitted >= 2);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("(High)", rendered);
        Assert.DoesNotContain("[verb-unknown]", rendered);

        _output.WriteLine("HTMX TSX FIXTURE - hx-post from TSX + axios to attribute-routed action verified:");
        _output.WriteLine($"  Clients: {string.Join(", ", sources)} -> CreateTodo (one edge each, bands {string.Join("/", hits.Select(h => h.Band))})");
        _output.WriteLine(rendered);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // TASK 7 — backend-http boundary language-parity gate (julie-extract 2.7.0 + 2.8.0). One grouped polyglot
    // fixture per scenario, each extracted LIVE through the real binary, proving Miller CONSUMES every new
    // route/mount family and every new client language end-to-end (fact emission → backend-http provider → Hits
    // edge → band → rendered trace). Seven behavioral group tests plus one aggregation test that unions per-family
    // emission and per-client-language emission across ALL group workspaces.
    //
    // SCOPE OF THE PARITY CLAIM (state it, don't imply): these tests live-verify Miller's per-family CONSUMPTION
    // and per-client-language CONSUMPTION on REPRESENTATIVE fixtures — one idiomatic shape per family, one client
    // per language. The full per-language×per-family matrix (express in js AND jsx AND ts AND tsx, spring across
    // annotation shapes, etc.) is owned UPSTREAM by julie-extractors' capability-matrix and golden gates; Miller
    // does not re-prove extractor coverage. It proves that each of the 28 families and each of the 12 client
    // languages, once emitted, actually bridges through the backend-http/dotnet-web providers Miller ships.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BackendHttpJsTsGroup_ExpressMountFastifyVueClient_LiveBridges()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteJsTsBackendGroup(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var expressPatterns = StructuralPatternIds(work.Db, "express.%");
        Assert.Contains("express.route.v1", expressPatterns);
        Assert.Contains("express.router_mount.v1", expressPatterns);
        Assert.Contains("fastify.route.v1", StructuralPatternIds(work.Db, "fastify.%"));
        Assert.Contains("http.client_request.v1", StructuralPatternIds(work.Db, "http.%"));

        var graph = index.BridgeGraph;
        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);

        // Direct express route (server/direct.ts, app.get) — axios.get + fetch clients are verb-known GET ⇒ High.
        var directHits = HitsInto(graph, "server/direct.ts");
        Assert.NotEmpty(directHits);
        Assert.All(directHits, h => Assert.Equal(ConfidenceBand.High, h.Band));
        Assert.All(directHits, h => Assert.False(h.IsVerbUnknown));

        // Same-file mounted express route (julie-extract 2.8.1 contract): app.use("/users", usersRouter) with the
        // router created in the SAME file attests the mount target as an ExpressRouter, so express.router_mount.v1
        // emits AND the extractor folds the mount prefix into the route fact's effective_route_template (/users/:id).
        // 2.8.1 narrowed router_mount.v1 to same-file attested routers — v2.8.0 over-emitted for any app.use target
        // (middleware, cross-file imports); cross-file composition is still proven for axum/actix/phoenix in
        // BackendHttpV280Group_*. The client /users/1 (axios GET) joins the already-effective route DIRECTLY at High
        // (composedRoutes==0 — an effective route is never double-composed; see
        // BackendHttp_route_with_effective_template_is_never_double_composed).
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        var mountedHits = HitsInto(graph, "server/app.ts");
        Assert.NotEmpty(mountedHits);
        Assert.All(mountedHits, h => Assert.Equal(ConfidenceBand.High, h.Band));

        // Fastify shorthand route (server/fast.ts) — client /things/1 (axios GET) ⇒ High.
        var fastifyHits = HitsInto(graph, "server/fast.ts");
        Assert.NotEmpty(fastifyHits);
        Assert.All(fastifyHits, h => Assert.Equal(ConfidenceBand.High, h.Band));

        // The Vue SFC's <script> client request emits http.client_request.v1 with language=vue and bridges the
        // same effective /users/:id route — proving vue is a first-class backend-http client language.
        Assert.Contains("vue", StructuralFactLanguages(work.Db, "http.client_request.v1"));
        Assert.Contains(mountedHits, h => string.Equals(h.Edge.SourceRef.FilePath, "web/Panel.vue", StringComparison.Ordinal));

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "loadUser", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);
        Assert.True(emitted > 0);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("(High)", rendered);

        _output.WriteLine("BACKEND-HTTP JS/TS GROUP — express direct + same-file mount + fastify + vue client verified:");
        _output.WriteLine($"  Patterns: {string.Join(", ", expressPatterns.Order(StringComparer.Ordinal))}");
        _output.WriteLine($"  composedRoutes={graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]} directHits={directHits.Count} mountedHits={mountedHits.Count} fastifyHits={fastifyHits.Count}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void BackendHttpPythonGroup_FastApiFlaskDjango_LiveBridges()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WritePythonBackendGroup(work.Repo);

        var index = ExtractAndLoad(binary, work);
        Assert.Contains("fastapi.route.v1", StructuralPatternIds(work.Db, "fastapi.%"));
        Assert.Contains("fastapi.include_router.v1", StructuralPatternIds(work.Db, "fastapi.%"));
        Assert.Contains("flask.route.v1", StructuralPatternIds(work.Db, "flask.%"));
        Assert.Contains("flask.blueprint_registration.v1", StructuralPatternIds(work.Db, "flask.%"));
        Assert.Contains("django.url_pattern.v1", StructuralPatternIds(work.Db, "django.%"));
        Assert.Contains("django.url_include.v1", StructuralPatternIds(work.Db, "django.%"));
        Assert.Contains("http.client_request.v1", StructuralPatternIds(work.Db, "http.%"));

        var graph = index.BridgeGraph;
        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);

        // FastAPI: @router.get with APIRouter(prefix="/api") ⇒ effective /api/users/{user_id}; client /api/users/1
        // (requests.get GET) bridges High into the decorator's file.
        var fastapiHits = HitsInto(graph, "app/main.py");
        Assert.NotEmpty(fastapiHits);
        Assert.All(fastapiHits, h => Assert.Equal(ConfidenceBand.High, h.Band));
        Assert.All(fastapiHits, h => Assert.False(h.IsVerbUnknown));

        // Flask blueprint composed cross-file: register_blueprint(bp, url_prefix="/shop") composes /shop onto
        // users_bp.py's @bp.get("/accounts/<int:account_id>") ⇒ /shop/accounts/:account_id; client /shop/accounts/1
        // (httpx GET) bridges composed-High into the blueprint route's file.
        Assert.True(graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"] >= 1,
            "the flask register_blueprint mount must compose at least one /shop/accounts route onto users_bp.py");
        var flaskHits = HitsInto(graph, "app/users_bp.py");
        Assert.NotEmpty(flaskHits);
        Assert.All(flaskHits, h => Assert.Equal(ConfidenceBand.High, h.Band));

        // Django URLconf path() has no verb at the URLconf level ⇒ every django route-only match is honest Medium
        // verb_unknown. Client /users/1 matches path("users/<int:pk>/") in app/urls.py.
        var djangoHits = HitsInto(graph, "app/urls.py");
        Assert.NotEmpty(djangoHits);
        Assert.All(djangoHits, h => Assert.Equal(ConfidenceBand.Medium, h.Band));
        Assert.All(djangoHits, h => Assert.True(h.IsVerbUnknown));

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "call_clients", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);
        Assert.True(emitted > 0);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("(High)", rendered);
        Assert.Contains("(Medium)", rendered);
        Assert.Contains("[verb-unknown]", rendered);

        _output.WriteLine("BACKEND-HTTP PYTHON GROUP — fastapi High + flask composed High + django Medium verb_unknown verified:");
        _output.WriteLine($"  fastapiHits={fastapiHits.Count} flaskHits(composed)={flaskHits.Count} djangoHits(medium)={djangoHits.Count}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void BackendHttpGoGroup_NetHttpGinEcho_LiveBridges()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteGoBackendGroup(work.Repo);

        var index = ExtractAndLoad(binary, work);
        Assert.Contains("go.net_http.route.v1", StructuralPatternIds(work.Db, "go.%"));
        Assert.Contains("gin.route.v1", StructuralPatternIds(work.Db, "gin.%"));
        Assert.Contains("echo.route.v1", StructuralPatternIds(work.Db, "echo.%"));
        Assert.Contains("http.client_request.v1", StructuralPatternIds(work.Db, "http.%"));

        var graph = index.BridgeGraph;
        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);

        // Everything lives in main.go, so discriminate the two client edges by band, not file:
        //   net/http Go-1.22 pattern "GET /api/items/{id}" attests GET ⇒ http.Get("/api/items/1") is verb-matched High.
        //   gin r.Any("/ping") is verbless ⇒ http.Get("/ping") is a route-only Medium verb_unknown.
        var hits = HitsInto(graph, "main.go");
        Assert.Contains(hits, h => h.Band == ConfidenceBand.High && !h.IsVerbUnknown);
        Assert.Contains(hits, h => h.Band == ConfidenceBand.Medium && h.IsVerbUnknown);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "clients", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);
        Assert.True(emitted > 0);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("(High)", rendered);
        Assert.Contains("(Medium)", rendered);
        Assert.Contains("[verb-unknown]", rendered);

        _output.WriteLine("BACKEND-HTTP GO GROUP — net/http verb-attested High + gin Any Medium verb_unknown (echo emits) verified:");
        _output.WriteLine($"  hits={hits.Count} bands={string.Join("/", hits.Select(h => $"{h.Band}{(h.IsVerbUnknown ? "!" : string.Empty)}"))}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void BackendHttpJavaGroup_SpringRequestMapping_LiveBridges()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteJavaBackendGroup(work.Repo);

        var index = ExtractAndLoad(binary, work);
        Assert.Contains("spring.request_mapping.v1", StructuralPatternIds(work.Db, "spring.%"));
        Assert.Contains("http.client_request.v1", StructuralPatternIds(work.Db, "http.%"));

        var graph = index.BridgeGraph;
        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);

        // Both controllers live in UserController.java; discriminate by band:
        //   @RequestMapping("/api") class + @GetMapping("/users/{id}") ⇒ effective GET /api/users/:id; the
        //   HttpRequest builder client to /api/users/1 is verb-matched High.
        //   method-less @RequestMapping("/legacy") is verbless ⇒ client /legacy is route-only Medium verb_unknown.
        var hits = HitsInto(graph, "src/UserController.java");
        Assert.Contains(hits, h => h.Band == ConfidenceBand.High && !h.IsVerbUnknown);
        Assert.Contains(hits, h => h.Band == ConfidenceBand.Medium && h.IsVerbUnknown);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "callClient", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);
        Assert.True(emitted > 0);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("(High)", rendered);
        Assert.Contains("(Medium)", rendered);
        Assert.Contains("[verb-unknown]", rendered);

        _output.WriteLine("BACKEND-HTTP JAVA GROUP — spring @GetMapping High + method-less @RequestMapping Medium verb_unknown verified:");
        _output.WriteLine($"  hits={hits.Count} bands={string.Join("/", hits.Select(h => $"{h.Band}{(h.IsVerbUnknown ? "!" : string.Empty)}"))}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void BackendHttpRubyGroup_RailsDslResourceMount_LiveBridges()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteRubyBackendGroup(work.Repo);

        var index = ExtractAndLoad(binary, work);
        Assert.Contains("rails.route.v1", StructuralPatternIds(work.Db, "rails.%"));
        Assert.Contains("rails.resource_route.v1", StructuralPatternIds(work.Db, "rails.%"));
        Assert.Contains("rails.mount.v1", StructuralPatternIds(work.Db, "rails.%"));
        Assert.Contains("http.client_request.v1", StructuralPatternIds(work.Db, "http.%"));

        var graph = index.BridgeGraph;
        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);

        // rails.mount.v1 (mount Sidekiq::Web => "/jobs") mounts a Rack app whose internal routes never reach the
        // fact stream — it is evidence-only and must be counted in backend-http.mounts, never composed/bridged.
        Assert.True(graph.CapabilityReport.EvidenceCounts["backend-http.mounts"] >= 1,
            "the rails mount must be counted in backend-http.mounts evidence");
        // resources :users must expand to concrete Rails routes on Miller's side (the julie handoff's job).
        Assert.True(graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"] >= 1,
            "resources :users must expand into concrete verb-known route handlers");

        var hits = HitsInto(graph, "config/routes.rb");
        Assert.NotEmpty(hits);

        // DSL verb route: get "/health", to: "health#show" ⇒ Net::HTTP GET /health is verb-matched High.
        Assert.Contains(hits, h => h.Band == ConfidenceBand.High && !h.IsVerbUnknown);

        // Expanded-resource route bound to the controller method SYMBOL: resources :users expands show ⇒
        // GET /users/:id, and Miller rebinds the endpoint to UsersController#show (unambiguous method match).
        // Client GET /users/1 therefore lands on the show method symbol, not a synthesized endpoint node.
        var showMethodIds = SymbolIdsInFile(index, "show", "app/controllers/users_controller.rb");
        var showHit = Assert.Single(hits, h =>
            !string.IsNullOrEmpty(h.Edge.TargetRef.SymbolId) && showMethodIds.Contains(h.Edge.TargetRef.SymbolId!));
        Assert.Equal(ConfidenceBand.High, showHit.Band);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "call_clients", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);
        Assert.True(emitted > 0);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("(High)", rendered);

        _output.WriteLine("BACKEND-HTTP RUBY GROUP — rails DSL High + expanded-resource High bound to UsersController#show + mount counted verified:");
        _output.WriteLine($"  mounts={graph.CapabilityReport.EvidenceCounts["backend-http.mounts"]} expandedResourceRoutes={graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]} showBound={showHit.Edge.TargetRef.SymbolId}");
        _output.WriteLine(rendered);
    }

    [Fact]
    public void BackendHttpCsharpGroup_HttpClientToAttributeRoute_LiveBridges()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteCsharpBackendGroup(work.Repo);

        var index = ExtractAndLoad(binary, work);
        Assert.Contains("aspnet.attribute_route.v1", StructuralPatternIds(work.Db, "aspnet.%"));
        Assert.Contains("http.client_request.v1", StructuralPatternIds(work.Db, "http.%"));
        Assert.Contains("csharp", StructuralFactLanguages(work.Db, "http.client_request.v1"));

        var graph = index.BridgeGraph;
        // Task 5 live proof: a non-test csharp HttpClient structural request is first-class client evidence into
        // dotnet-web (service-to-service). client.GetFromJsonAsync("/api/users/{id}") ⇒ verb-known GET matches the
        // [HttpGet("{id}")] action under [Route("api/users")] ⇒ High edge into the controller, through dotnet-web.
        Assert.Contains("dotnet-web", graph.CapabilityReport.ActiveProviders);
        var hits = HitsInto(graph, "server/UsersController.cs");
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Band == ConfidenceBand.High && !h.IsVerbUnknown);

        string rendered = TraceTool.Run(
            index, new SmartTargetResolver(index), target: "Load", mode: "bridge", to: null, depth: 2, limit: 20,
            fullFormat: false, out int emitted, out _);
        Assert.True(emitted > 0);
        Assert.Contains("--route-->", rendered);
        Assert.Contains("(High)", rendered);

        _output.WriteLine("BACKEND-HTTP C# GROUP — HttpClient service-to-service to attribute-routed action verified:");
        _output.WriteLine($"  hits={hits.Count} bands={string.Join("/", hits.Select(h => h.Band))}");
        _output.WriteLine(rendered);
    }

    // ── julie-extract 2.8.0 wave 2: six more backend stacks + four more client languages, extracted LIVE. ──────
    [Fact]
    public void BackendHttpV280Group_NestjsLaravelPhoenixAxumActix_LiveBridges()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteV280BackendGroup(work.Repo);

        var index = ExtractAndLoad(binary, work);

        // Emission: every 2.8.0 route / resource / mount family appears in the real extract.
        Assert.Contains("nestjs.route.v1", StructuralPatternIds(work.Db, "nestjs.%"));
        var laravel = StructuralPatternIds(work.Db, "laravel.%");
        Assert.Contains("laravel.route.v1", laravel);
        Assert.Contains("laravel.resource_route.v1", laravel);
        Assert.Contains("laravel.route_prefix.v1", laravel);
        var phoenix = StructuralPatternIds(work.Db, "phoenix.%");
        Assert.Contains("phoenix.route.v1", phoenix);
        Assert.Contains("phoenix.resource_route.v1", phoenix);
        Assert.Contains("phoenix.forward.v1", phoenix);
        var axum = StructuralPatternIds(work.Db, "axum.%");
        Assert.Contains("axum.route.v1", axum);
        Assert.Contains("axum.nest.v1", axum);
        var actix = StructuralPatternIds(work.Db, "actix.%");
        Assert.Contains("actix.attribute_route.v1", actix);
        Assert.Contains("actix.scope_route.v1", actix);
        Assert.Contains("actix.mount.v1", actix);

        // The four new http.client_request.v1 languages emit.
        var clientLangs = StructuralFactLanguages(work.Db, "http.client_request.v1");
        Assert.Contains("kotlin", clientLangs);
        Assert.Contains("php", clientLangs);
        Assert.Contains("elixir", clientLangs);
        Assert.Contains("rust", clientLangs);

        var graph = index.BridgeGraph;
        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);

        // Consumption end-to-end: laravel Route::resource (8) + phoenix scoped resources (8) expand to concrete routes.
        Assert.True(graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"] >= 8,
            "laravel Route::resource + phoenix resources must expand to concrete verb-known routes");

        // Representative cross-language DIRECT join: a Kotlin Ktor client GET /cats/1 bridges to the NestJS controller's
        // @Get(':id') under @Controller('cats') (effective /cats/:id), verb-known ⇒ High.
        var nestHits = HitsInto(graph, "nest/cats.controller.ts");
        Assert.NotEmpty(nestHits);
        Assert.Contains(nestHits, h => h.Band == ConfidenceBand.High && !h.IsVerbUnknown);

        // The four review fixes, each proven on REAL v2.8.0 data by a composed/expanded/optional join landing on an
        // ISOLABLE handler file — every one of these would be EMPTY under the corresponding bug:
        //   #2 namespaced phoenix forward "/admin", MyAppWeb.AdminRouter → composed /admin/dashboard.
        Assert.NotEmpty(HitsInto(graph, "phoenix/admin_router.ex"));
        //   #1 scoped phoenix resources "/api"+"/posts" prefixed ONCE → /api/posts (a doubled /api/api/posts misses).
        Assert.NotEmpty(HitsInto(graph, "phoenix/api_router.ex"));
        //   #3 axum .nest("/api", api::routes()) — '::'-qualified target anchors api.rs → composed /api/orders/1.
        Assert.NotEmpty(HitsInto(graph, "axum/api.rs"));
        //   #3 actix web::scope("/admin").configure(admin::configure) — '::'-qualified → composed /admin/settings.
        Assert.NotEmpty(HitsInto(graph, "actix/admin.rs"));
        //   #4 laravel optional {id?} inside a prefix group → /admin/reports/99 (brace-optional must stay dynamic).
        Assert.NotEmpty(HitsInto(graph, "laravel/admin.php"));

        _output.WriteLine("BACKEND-HTTP 2.8.0 GROUP — nestjs/laravel/phoenix/axum/actix families + kt/php/ex/rs clients:");
        _output.WriteLine($"  families: nestjs={StructuralPatternIds(work.Db, "nestjs.%").Count} laravel={laravel.Count} "
            + $"phoenix={phoenix.Count} axum={axum.Count} actix={actix.Count}");
        _output.WriteLine($"  expandedResourceRoutes={graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]} "
            + $"composedRoutes={graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]} "
            + $"candidates={graph.CapabilityReport.EvidenceCounts["backend-http.candidates"]}");
        _output.WriteLine($"  client languages: {string.Join(", ", clientLangs.Order(StringComparer.Ordinal))}");
    }

    private static void WriteV280BackendGroup(string repo)
    {
        string nest = Path.Combine(repo, "nest");
        string laravel = Path.Combine(repo, "laravel");
        string phoenix = Path.Combine(repo, "phoenix");
        string axum = Path.Combine(repo, "axum");
        string actix = Path.Combine(repo, "actix");
        string client = Path.Combine(repo, "client");
        foreach (var dir in new[] { nest, laravel, phoenix, axum, actix, client })
            Directory.CreateDirectory(dir);

        // NestJS — @Controller('cats') prefix folds into effective /cats/:id and /cats.
        File.WriteAllText(Path.Combine(nest, "cats.controller.ts"), """
            import { Controller, Get, Post } from '@nestjs/common';

            @Controller('cats')
            export class CatsController {
              @Get(':id')
              findOne() {
                return null;
              }

              @Post()
              create() {
                return null;
              }
            }
            """);

        // Laravel — web.php: a call route + a resource. admin.php (own file, so the join is isolable): a prefix group
        // with an OPTIONAL param, whose effective_route_template "/admin/reports/{id?}" must still match a client
        // GET /admin/reports/99 (regression: the brace-optional marker must not be treated as a query suffix).
        File.WriteAllText(Path.Combine(laravel, "web.php"), """
            <?php
            use Illuminate\Support\Facades\Route;

            Route::get('/books/{id}', [BookController::class, 'show']);
            Route::resource('photos', PhotoController::class);
            """);
        File.WriteAllText(Path.Combine(laravel, "admin.php"), """
            <?php
            use Illuminate\Support\Facades\Route;

            Route::prefix('admin')->group(function () {
                Route::get('/reports/{id?}', [ReportController::class, 'show']);
            });
            """);

        // Phoenix — router.ex: a call route + a forward to a NAMESPACED router module (real Phoenix; the forwarded
        // module symbol is named by its full dotted alias). admin_router.ex + api_router.ex are separate files so the
        // composed /admin/dashboard and the scoped-resource /api/posts joins land on isolable targets.
        File.WriteAllText(Path.Combine(phoenix, "router.ex"), """
            defmodule MyAppWeb.Router do
              use Phoenix.Router

              get "/articles/:id", ArticleController, :show
              forward "/admin", MyAppWeb.AdminRouter
            end
            """);
        File.WriteAllText(Path.Combine(phoenix, "admin_router.ex"), """
            defmodule MyAppWeb.AdminRouter do
              use Phoenix.Router

              get "/dashboard", DashboardController, :index
            end
            """);
        // Scoped resource: `scope "/api" do resources "/posts" end` — the resource's normalized_resource_path ALREADY
        // folds in "/api", so the expander must prefix ONCE (regression: no /api/api/posts double-prefix).
        File.WriteAllText(Path.Combine(phoenix, "api_router.ex"), """
            defmodule MyAppWeb.ApiRouter do
              use Phoenix.Router

              scope "/api" do
                resources "/posts", PostController
              end
            end
            """);

        // axum — a direct route plus a cross-file .nest with a PATH-QUALIFIED target `api::routes()` (idiomatic Rust;
        // regression: the '::' must split so the leaf fn name "routes" anchors api.rs).
        File.WriteAllText(Path.Combine(axum, "api.rs"), """
            use axum::{routing::get, Router};

            pub fn routes() -> Router {
                Router::new().route("/orders/{id}", get(order))
            }

            async fn order() {}
            """);
        File.WriteAllText(Path.Combine(axum, "main.rs"), """
            use axum::{routing::get, Router};

            mod api;

            fn app() -> Router {
                Router::new()
                    .route("/widgets/{id}", get(show))
                    .nest("/api", api::routes())
            }

            async fn show() {}
            """);

        // actix — an attribute route, a scope route, and a web::scope(...).configure(...) mount with a PATH-QUALIFIED
        // configure target `admin::configure` (regression: '::' split anchors the leaf fn "configure" in admin.rs).
        File.WriteAllText(Path.Combine(actix, "admin.rs"), """
            use actix_web::{get, web, HttpResponse, Responder};

            #[get("/settings")]
            async fn settings() -> impl Responder {
                HttpResponse::Ok().finish()
            }

            pub fn configure(cfg: &mut web::ServiceConfig) {
                cfg.service(settings);
            }
            """);
        File.WriteAllText(Path.Combine(actix, "main.rs"), """
            use actix_web::{get, web, App, HttpResponse, Responder};

            mod admin;

            #[get("/gadgets/{id}")]
            async fn gadget() -> impl Responder {
                HttpResponse::Ok().finish()
            }

            async fn items() -> impl Responder {
                HttpResponse::Ok().finish()
            }

            fn make_app() -> App<()> {
                App::new()
                    .service(web::scope("/store").route("/items/{id}", web::get().to(items)))
                    .service(web::scope("/admin").configure(admin::configure))
            }
            """);

        // Clients in the four new http.client_request.v1 languages, each a path-kind call to a distinct DIRECT route.
        File.WriteAllText(Path.Combine(client, "Client.kt"), """
            import io.ktor.client.HttpClient
            import io.ktor.client.request.get

            suspend fun loadCat(client: HttpClient) {
                client.get("/cats/1")
            }
            """);
        File.WriteAllText(Path.Combine(client, "client.php"), """
            <?php
            use Illuminate\Support\Facades\Http;

            function loadBook() {
                Http::get('/books/1');
            }
            """);
        File.WriteAllText(Path.Combine(client, "client.ex"), """
            defmodule MyApp.Client do
              def load_article do
                Req.get("/articles/1")
              end
            end
            """);
        File.WriteAllText(Path.Combine(client, "client.rs"), """
            use reqwest;

            async fn load_widget() {
                reqwest::get("/widgets/1").await;
            }
            """);

        // A TypeScript client that calls the COMPOSED / EXPANDED / OPTIONAL paths — the ones that only exist if the
        // four review fixes hold. Each targets an isolable handler file so its join proves one fix on real data.
        File.WriteAllText(Path.Combine(client, "composed.ts"), """
            export async function callComposed() {
              await fetch('/admin/dashboard');   // phoenix forward → admin_router.ex (finding #2: namespaced module)
              await fetch('/api/posts');          // phoenix scoped resource → api_router.ex (finding #1: prefix once)
              await fetch('/api/orders/1');       // axum nest api::routes() → api.rs (finding #3: '::' split)
              await fetch('/admin/settings');     // actix mount admin::configure → admin.rs (finding #3: '::' split)
              await fetch('/admin/reports/99');   // laravel optional {id?} in prefix group → admin.php (finding #4)
            }
            """);
    }

    // ── The parity gate: aggregate per-family + per-client-language emission across ALL group workspaces ──────
    [Fact]
    public void BackendHttpParityGate_AllTwentyEightFamiliesAndTwelveClientLanguagesEmitLive()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        // Extract every group into its own workspace and union the emitted structural-fact pattern ids + the
        // http.client_request.v1 languages across all of them. A family or client language that silently emits
        // ZERO across every representative fixture FAILS this test — it never softens to a subset.
        var writers = new (string Label, Action<string> Write)[]
        {
            ("js/ts", WriteJsTsBackendGroup),
            ("python", WritePythonBackendGroup),
            ("go", WriteGoBackendGroup),
            ("java", WriteJavaBackendGroup),
            ("ruby", WriteRubyBackendGroup),
            ("csharp", WriteCsharpBackendGroup),
            ("v2.8.0", WriteV280BackendGroup),
        };

        var emittedPatternIds = new HashSet<string>(StringComparer.Ordinal);
        var emittedClientLanguages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (label, write) in writers)
        {
            using var work = new TempWorkspace();
            write(work.Repo);
            _ = ExtractAndLoad(binary, work);
            foreach (var id in StructuralPatternIds(work.Db, "%"))
                emittedPatternIds.Add(id);
            foreach (var lang in StructuralFactLanguages(work.Db, "http.client_request.v1"))
                emittedClientLanguages.Add(lang);
            _output.WriteLine($"  group {label}: {StructuralFactLanguages(work.Db, "http.client_request.v1").Count} client languages, "
                + $"{StructuralPatternIds(work.Db, "%").Count(id => RequiredFamilies.Contains(id))} target families present");
        }

        // The 28 route/mount families the backend-http lane spans (julie-extract 2.7.0's 16 + 2.8.0's 12; release
        // notes §New Structural-Fact Families). Every one must emit at least once, or Miller's consumer is a subset.
        foreach (var family in RequiredFamilies)
            Assert.Contains(family, emittedPatternIds);

        // http.client_request.v1 emits from twelve client languages: js/ts (javascript + typescript), plus vue,
        // python, go, java, ruby, csharp (2.7.0) and kotlin, php, elixir, rust (2.8.0). A client language that
        // silently emits zero fails the test.
        foreach (var lang in RequiredClientLanguages)
            Assert.Contains(lang, emittedClientLanguages);

        _output.WriteLine("BACKEND-HTTP PARITY GATE — all 28 families + all client languages emit live:");
        _output.WriteLine($"  families ({RequiredFamilies.Length}/28): {string.Join(", ", RequiredFamilies.Order(StringComparer.Ordinal))}");
        _output.WriteLine($"  client languages: {string.Join(", ", emittedClientLanguages.Order(StringComparer.Ordinal))}");
    }

    // The 28 backend route/mount fact families (julie-extract 2.7.0's 16 + 2.8.0's 12). Kept as one explicit list
    // so a dropped or renamed family FAILS loudly rather than silently shrinking the parity claim.
    private static readonly string[] RequiredFamilies =
    {
        // 2.7.0 wave 1 (16).
        "express.route.v1", "express.router_mount.v1", "fastify.route.v1",
        "fastapi.route.v1", "fastapi.include_router.v1", "flask.route.v1", "flask.blueprint_registration.v1",
        "django.url_pattern.v1", "django.url_include.v1", "spring.request_mapping.v1",
        "go.net_http.route.v1", "gin.route.v1", "echo.route.v1",
        "rails.route.v1", "rails.resource_route.v1", "rails.mount.v1",
        // 2.8.0 wave 2 (12).
        "nestjs.route.v1",
        "laravel.route.v1", "laravel.resource_route.v1", "laravel.route_prefix.v1",
        "phoenix.route.v1", "phoenix.resource_route.v1", "phoenix.forward.v1",
        "axum.route.v1", "axum.nest.v1",
        "actix.attribute_route.v1", "actix.scope_route.v1", "actix.mount.v1",
    };

    // The distinct client languages http.client_request.v1 must cover live (js/ts split into javascript +
    // typescript; plus vue, python, go, java, ruby, csharp from 2.7.0, and kotlin/php/elixir/rust from 2.8.0).
    private static readonly string[] RequiredClientLanguages =
    {
        "javascript", "typescript", "vue", "python", "go", "java", "ruby", "csharp",
        "kotlin", "php", "elixir", "rust",
    };

    private static List<ScoredEdge> HitsInto(BridgeGraph graph, string filePath) =>
        graph.Edges
            .Where(e => e.Edge.Kind == BridgeKind.Hits
                && string.Equals(e.Edge.TargetRef.FilePath, filePath, StringComparison.Ordinal))
            .ToList();

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Probe 2: honesty probe — undisciplined fixture. Precision + per-leg recall + guard proofs.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HonestyProbe_UndisciplinedFixture_GuardsHold_PrecisionAndRecall()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var work = new TempWorkspace();
        WriteUndisciplinedFixture(work.Repo);

        var index = ExtractAndLoad(binary, work);
        var graph = index.BridgeGraph;

        // Enumerate every distinct surviving bridge edge (undirected; one entry per edge signature).
        var allEdges = EnumerateDistinctEdges(index, graph);

        // ── GROUND TRUTH (by construction). The buildable-correct bridges in the undisciplined fixture are:
        //   Leg 3 (entity→table): Customer DbSet "Customers" ⇒ Customer StoredIn Customers.            [detectable]
        //   Leg 2 (DTO↔entity):   CreateMap<Customer, CustomerDto>() ⇒ Customer MapsTo CustomerDto.     [detectable]
        //   Leg 1 (route):        axios.post('/api/orders') ⇒ Hits CreateOrder (POST, verb-known).      [detectable]
        // Dapper-FROM is NOT buildable on the lean contract (BridgeGraphBuilder passes DapperFromCandidates:[]),
        // so it is EXCLUDED from every recall denominator below — stated explicitly per the honesty requirement.
        //
        // Traps that must NOT produce a confident-wrong edge:
        //   (a) Ambiguous duplicate type name "Account" (Billing.Account + Crm.Account) + CreateMap<Billing.Account,
        //       AccountDto>(): the entity side resolves to >1 symbol ⇒ NEVER High (capped/flagged or dropped).
        //   (b) A 1-field wrapper pair (IdHolder vs IdHolderDto, share only "Id", no CreateMap) ⇒ NEVER an edge.
        //   (c) A verb-less fetch carrier fetch('/api/reports'): any route edge it forms must be verb-unknown.
        //   (d) A C# test HttpClient url literal ("/api/secret" in a [Fact]) ⇒ EXCLUDED (language + test_role filter).
        //   (e) An inbound CreateMap<UpdateCustomerRequest, Customer>(): a real structural edge; the Request must NOT
        //       be mislabeled as the entity (ordinal = copy direction, never reordered to entity-vs-DTO).

        // ── PRECISION. Classify every emitted edge correct/incorrect against the ground truth + trap rules.
        int truePositives = 0;
        int falsePositives = 0;
        var verdicts = new List<string>();
        foreach (var edge in allEdges)
        {
            var (ok, why) = ClassifyHonesty(graph, edge);
            if (ok) truePositives++; else falsePositives++;
            verdicts.Add($"  [{(ok ? "TP" : "FP")}] {DescribeEdge(graph, edge)}  band={edge.Band} score={Fmt(edge.Score)} ambiguous={edge.HasAmbiguousName} verbUnknown={edge.IsVerbUnknown}  ({why})");
        }
        int detected = truePositives + falsePositives;
        double precision = detected == 0 ? 1.0 : (double)truePositives / detected;

        // ── GUARD 1: corroborator-only / 1-field wrapper never anchors. IdHolder<->IdHolderDto share exactly one
        // field ("Id") and have no CreateMap; the field-set-only branch is never even built by the production builder
        // (it passes Projections:[]), and the scorer's MinAnchoringFieldCount=2 would refuse it anyway. No edge.
        string? idHolderId = FindSymbolIdOrNull(index, "IdHolder", "class");
        if (idHolderId is not null)
        {
            Assert.DoesNotContain(graph.Walk(idHolderId, 3),
                e => EdgeTouchesDisplay(graph, e, "IdHolderDto"));
        }
        // And no surviving edge anywhere may be field-set-anchored with <2 shared fields (proven from the payload).
        foreach (var edge in allEdges)
        {
            bool structurallyAnchored = edge.Edge.Signals.Any(s =>
                s is StructuralSignal { Present: true } st && st.Rule != SignalRule.RouteOnlyMatch);
            var fs = edge.Edge.Signals.OfType<FieldSetSignal>().FirstOrDefault();
            // A field-set may only RIDE ALONG on a structurally-anchored edge, and only when it carries >=2 fields.
            if (fs is not null)
                Assert.True(structurallyAnchored && fs.FieldCount >= 2,
                    $"a FieldSetSignal must ride on a structural anchor with >=2 fields, saw count={fs.FieldCount}, anchored={structurallyAnchored}");
        }

        // ── GUARD 2: ambiguous-name-never-High. Any edge flagged ambiguous (HasAmbiguousName) OR carrying a
        // NameResolutionSignal with Status=Ambiguous must NOT be High (proven from the payload, not a rule name).
        foreach (var edge in allEdges)
        {
            bool ambiguousByPayload =
                edge.HasAmbiguousName
                || edge.Edge.Signals.OfType<NameResolutionSignal>().Any(n => n.Status == ResolutionStatus.Ambiguous);
            if (ambiguousByPayload)
                Assert.NotEqual(ConfidenceBand.High, edge.Band);
        }
        // The ambiguous "Account" CreateMap specifically: Billing.Account/Crm.Account collide, so the entity side
        // resolves ambiguously. Whatever Account-touching edge survives must never be a High edge.
        foreach (var edge in allEdges.Where(e =>
                     EdgeTouchesDisplay(graph, e, "Account") || EdgeTouchesDisplay(graph, e, "AccountDto")))
        {
            if (edge.HasAmbiguousName)
                Assert.NotEqual(ConfidenceBand.High, edge.Band);
        }
        // ANTI-VACUITY: the guard above is only meaningful if the ambiguity actually fired this run. If a future
        // julie-extract build stopped resolving "Account" to >1 symbol (e.g. emitted fully-qualified names), every
        // edge would be unambiguous and the guard would pass while testing nothing. Pin that at least one surviving
        // edge really carries an ambiguous signal, so the guard cannot silently lose its teeth.
        int ambiguousEdgeCount = allEdges.Count(e =>
            e.HasAmbiguousName
            || e.Edge.Signals.OfType<NameResolutionSignal>().Any(n => n.Status == ResolutionStatus.Ambiguous));
        Assert.True(ambiguousEdgeCount >= 1,
            "the ambiguous-name guard is vacuous: no surviving edge carried an ambiguous signal. The Account "
            + "collision (Billing.Account/Crm.Account) must produce at least one ambiguous-flagged edge for Guard 2 "
            + "to mean anything — if julie-extract stopped flagging it, this fixture no longer exercises the guard.");

        // ── GUARD 3: the C# test HttpClient literal "/api/secret" must NOT form a route bridge (language + test filter).
        Assert.DoesNotContain(allEdges, e =>
            EdgeTouchesDisplay(graph, e, "/api/secret") || EdgeTouchesDisplay(graph, e, "api/secret"));

        // ── GUARD 4: the verb-less fetch carrier must never be assumed GET. Any /api/reports route edge that exists
        // must be verb-unknown. (It may also simply not match an endpoint and produce no edge — both are honest.)
        foreach (var edge in allEdges.Where(e =>
                     EdgeTouchesDisplay(graph, e, "/api/reports") || EdgeTouchesDisplay(graph, e, "api/reports")))
            Assert.True(edge.IsVerbUnknown, "a verb-less fetch carrier route edge must be verb-unknown, never assumed GET");

        // ── RECALL PER LEG (detected / detectable; Dapper-FROM excluded from the denominator).
        bool leg3Detected = allEdges.Any(e =>
            e.Edge.Kind == BridgeKind.StoredIn
            && EdgeTouchesDisplay(graph, e, "Customer")
            && EdgeTouchesDisplay(graph, e, "Customers"));
        bool leg2Detected = allEdges.Any(e =>
            e.Edge.Kind == BridgeKind.MapsTo
            && EdgeTouchesDisplay(graph, e, "Customer")
            && EdgeTouchesDisplay(graph, e, "CustomerDto"));
        bool leg1Detected = allEdges.Any(e =>
            e.Edge.Kind == BridgeKind.Hits
            && EdgeTouchesDisplay(graph, e, "CreateOrder"));

        double recallLeg1 = leg1Detected ? 1.0 : 0.0; // denominator 1 (POST /api/orders -> CreateOrder)
        double recallLeg2 = leg2Detected ? 1.0 : 0.0; // denominator 1 (CreateMap<Customer,CustomerDto>)
        double recallLeg3 = leg3Detected ? 1.0 : 0.0; // denominator 1 (Customer DbSet "Customers")

        // ── Acceptance assertions. Guards above are hard; here we pin the measured numbers don't regress below bar.
        Assert.True(precision >= 0.75,
            $"honesty-probe precision {Fmt(precision)} below the 0.75 acceptance floor. Verdicts:\n{string.Join("\n", verdicts)}");
        Assert.True(leg3Detected, "Leg 3 ground-truth bridge (Customer -> Customers) must be detected (recall > 0)");
        Assert.True(leg2Detected, "Leg 2 ground-truth bridge (Customer <-> CustomerDto) must be detected (recall > 0)");
        Assert.True(leg1Detected, "Leg 1 ground-truth bridge (POST /api/orders -> CreateOrder) must be detected (recall > 0)");

        // ── Emit the MEASURED numbers (copied verbatim into the design-doc appendix).
        _output.WriteLine("=== M4 PHASE D HONESTY PROBE (measured) ===");
        _output.WriteLine($"edges emitted (distinct): {detected}   TP={truePositives}  FP={falsePositives}");
        _output.WriteLine($"PRECISION (all legs): {Fmt(precision)}");
        _output.WriteLine($"RECALL Leg1 (route):          {Fmt(recallLeg1)}  (detected {(leg1Detected ? 1 : 0)}/1)");
        _output.WriteLine($"RECALL Leg2 (DTO<->entity):   {Fmt(recallLeg2)}  (detected {(leg2Detected ? 1 : 0)}/1)");
        _output.WriteLine($"RECALL Leg3 (entity<->table): {Fmt(recallLeg3)}  (detected {(leg3Detected ? 1 : 0)}/1)");
        _output.WriteLine("Dapper-FROM leg: EXCLUDED from denominators (not buildable on the lean contract).");
        _output.WriteLine("--- per-edge verdicts ---");
        foreach (var v in verdicts)
            _output.WriteLine(v);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Disciplined fixture writer.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriteDisciplinedFixture(string repo)
    {
        string cs = Path.Combine(repo, "server");
        string ts = Path.Combine(repo, "web");
        Directory.CreateDirectory(cs);
        Directory.CreateDirectory(ts);

        File.WriteAllText(Path.Combine(cs, "AppSetting.cs"), """
            namespace Demo.Domain;

            public sealed class AppSetting
            {
                public int Id { get; set; }
                public string Key { get; set; } = string.Empty;
                public string Value { get; set; } = string.Empty;
            }
            """);
        File.WriteAllText(Path.Combine(cs, "AppSettingDto.cs"), """
            namespace Demo.Contracts;

            public sealed class AppSettingDto
            {
                public int Id { get; set; }
                public string Key { get; set; } = string.Empty;
                public string Value { get; set; } = string.Empty;
            }
            """);

        // EF DbContext with a DbSet<AppSetting> property named AppSettings (Leg 3 anchor).
        File.WriteAllText(Path.Combine(cs, "AppDbContext.cs"), """
            using Microsoft.EntityFrameworkCore;
            using Demo.Domain;

            namespace Demo.Data;

            public sealed class AppDbContext : DbContext
            {
                public DbSet<AppSetting> AppSettings { get; set; } = null!;
            }
            """);

        // AutoMapper profile: CreateMap<AppSetting, AppSettingDto>().ReverseMap() (Leg 2 anchor).
        File.WriteAllText(Path.Combine(cs, "MappingProfile.cs"), """
            using AutoMapper;
            using Demo.Domain;
            using Demo.Contracts;

            namespace Demo.Mapping;

            public sealed class MappingProfile : Profile
            {
                public MappingProfile()
                {
                    CreateMap<AppSetting, AppSettingDto>().ReverseMap();
                }
            }
            """);

        // Controller: [Route("api/[controller]")] + [HttpGet("{id}")] handler GetById (Leg 1 endpoint).
        File.WriteAllText(Path.Combine(cs, "AppSettingsController.cs"), """
            using Microsoft.AspNetCore.Mvc;
            using Demo.Contracts;

            namespace Demo.Api;

            [ApiController]
            [Route("api/[controller]")]
            public sealed class AppSettingsController : ControllerBase
            {
                [HttpGet("{id}")]
                public ActionResult<AppSettingDto> GetById(int id) => new AppSettingDto();

                [HttpPost]
                public ActionResult<AppSettingDto> Create([FromBody] AppSettingDto body) => body;
            }
            """);

        // TS client: typed axios.get<T> / axios.post to the same routes (Leg 1 frontend side; verb-known carriers).
        // The generic GET shape pins the julie-extract 2.1.2 TypeScript URL-literal persistence fix.
        File.WriteAllText(Path.Combine(ts, "appSettings.api.ts"), """
            import axios from "axios";

            export interface AppSettingDto {
              id: number;
              key: string;
              value: string;
            }

            export async function fetchAppSetting(id: number): Promise<AppSettingDto> {
              const res = await axios.get<AppSettingDto>(`/api/appsettings/${id}`);
              return res.data;
            }

            export async function createAppSetting(body: AppSettingDto): Promise<AppSettingDto> {
              const res = await axios.post("/api/appsettings", body);
              return res.data;
            }
            """);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Pure Next.js fixture writer.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriteNextFixture(string repo)
    {
        string settingsRoute = Path.Combine(repo, "app", "settings");
        string userRoute = Path.Combine(repo, "app", "users", "[id]");
        string components = Path.Combine(repo, "components");
        Directory.CreateDirectory(settingsRoute);
        Directory.CreateDirectory(userRoute);
        Directory.CreateDirectory(components);

        File.WriteAllText(Path.Combine(settingsRoute, "page.tsx"), """
            export default function SettingsPage() {
              return <main>Settings</main>;
            }
            """);
        File.WriteAllText(Path.Combine(userRoute, "page.tsx"), """
            export default function UserPage() {
              return <main>User</main>;
            }
            """);
        File.WriteAllText(Path.Combine(components, "Nav.tsx"), """
            import Link from "next/link";

            export function Nav() {
              return (
                <nav>
                  <Link href="/settings">Settings</Link>
                  <Link href="/users/42">User 42</Link>
                </nav>
              );
            }
            """);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Pure Nuxt fixture writer.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriteNuxtFixture(string repo)
    {
        string pageRoute = Path.Combine(repo, "app", "pages");
        string components = Path.Combine(repo, "app", "components");
        string composables = Path.Combine(repo, "app", "composables");
        Directory.CreateDirectory(pageRoute);
        Directory.CreateDirectory(components);
        Directory.CreateDirectory(composables);

        File.WriteAllText(Path.Combine(pageRoute, "about.vue"), """
            <template>
              <main>About</main>
            </template>

            <script setup lang="ts">
            const pageTitle = "About";
            </script>
            """);
        File.WriteAllText(Path.Combine(components, "Nav.vue"), """
            <template>
              <nav>
                <NuxtLink to="/about">About</NuxtLink>
                <NuxtLink to="/blog/hello-world">Hello World</NuxtLink>
              </nav>
            </template>
            """);
        string blogRoute = Path.Combine(pageRoute, "blog");
        Directory.CreateDirectory(blogRoute);
        File.WriteAllText(Path.Combine(blogRoute, "[slug].vue"), """
            <template>
              <article>Blog post</article>
            </template>

            <script setup lang="ts">
            const slug = "hello-world";
            </script>
            """);
        File.WriteAllText(Path.Combine(composables, "useMarker.ts"), """
            export function useMarker() {
              return "marker";
            }
            """);
    }

    private static void WriteHtmxDataPrefixFixture(string repo)
    {
        string server = Path.Combine(repo, "server");
        string web = Path.Combine(repo, "web");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(web);

        File.WriteAllText(Path.Combine(server, "TodosController.cs"), """
            using Microsoft.AspNetCore.Mvc;

            namespace Demo.Api;

            [ApiController]
            [Route("todos")]
            public sealed class TodosController : ControllerBase
            {
                [HttpPost]
                public IActionResult CreateTodo() => Ok();
            }
            """);
        File.WriteAllText(Path.Combine(web, "index.html"), """
            <!doctype html>
            <html>
              <body>
                <form data-hx-post="/todos">
                  <button type="submit">Create</button>
                </form>
              </body>
            </html>
            """);
    }

    private static void WriteVueRouteDefinitionFixture(string repo)
    {
        string web = Path.Combine(repo, "web");
        Directory.CreateDirectory(web);

        File.WriteAllText(Path.Combine(web, "router.ts"), """
            import { createRouter, createWebHistory } from "vue-router";
            import UserPage from "./UserPage.vue";

            export const router = createRouter({
              history: createWebHistory(),
              routes: [
                {
                  path: "/users/:id",
                  name: "user",
                  component: UserPage,
                },
              ],
            });
            """);
        File.WriteAllText(Path.Combine(web, "UserPage.vue"), """
            <template>
              <main>User page</main>
            </template>
            """);
        File.WriteAllText(Path.Combine(web, "Nav.vue"), """
            <template>
              <section>
                <template #default>
                  <router-link to="/users/42">User 42</router-link>
                </template>
              </section>
            </template>
            """);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Next.js API fixture writer: App Router route handlers + fetch clients. Client URLs are plain static
    // strings so the extractor emits concrete path facts. julie-extract 2.6.1+ binds both function-declaration
    // and const-arrow route handlers to their exported handler symbols; the test above asserts both bindings.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriteNextApiFixture(string repo)
    {
        string messagesRoute = Path.Combine(repo, "app", "api", "messages");
        string userRoute = Path.Combine(repo, "app", "api", "users", "[id]");
        string lib = Path.Combine(repo, "lib");
        Directory.CreateDirectory(messagesRoute);
        Directory.CreateDirectory(userRoute);
        Directory.CreateDirectory(lib);

        // Both handler shapes: an exported const arrow (GET) and an exported async function (POST).
        File.WriteAllText(Path.Combine(messagesRoute, "route.ts"), """
            export const GET = async (request: Request): Promise<Response> => {
              return Response.json({ messages: [] });
            };

            export async function POST(request: Request): Promise<Response> {
              const body = await request.json();
              return Response.json(body, { status: 201 });
            }
            """);
        File.WriteAllText(Path.Combine(userRoute, "route.ts"), """
            export async function GET(request: Request): Promise<Response> {
              return Response.json({ id: "42" });
            }
            """);
        File.WriteAllText(Path.Combine(lib, "messages.api.ts"), """
            export async function sendMessage(payload: unknown): Promise<void> {
              await fetch("/api/messages", { method: "POST" });
            }

            export async function loadUser(): Promise<void> {
              await fetch("/api/users/42");
            }
            """);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Nuxt server-route fixture writer: Nitro routes live at server/api/** relative to the project root
    // (the /api prefix comes from the directory). messages.get.ts attests GET via the filename suffix;
    // notes.ts is suffix-less (answers every method ⇒ verb-less fact). axios calls REQUIRE the axios import.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriteNuxtServerFixture(string repo)
    {
        string serverApi = Path.Combine(repo, "server", "api");
        string composables = Path.Combine(repo, "app", "composables");
        Directory.CreateDirectory(serverApi);
        Directory.CreateDirectory(composables);

        File.WriteAllText(Path.Combine(serverApi, "messages.get.ts"), """
            export default defineEventHandler(() => {
              return { messages: [] };
            });
            """);
        File.WriteAllText(Path.Combine(serverApi, "notes.ts"), """
            export default defineEventHandler(() => {
              return { notes: [] };
            });
            """);
        // Bare awaits keep this fixture compact; the assigned-response client shape is covered by the
        // disciplined and honesty-probe fixtures, where 2.6.1+ binds the fact to the containing function.
        File.WriteAllText(Path.Combine(composables, "useApi.ts"), """
            import axios from "axios";

            export async function fetchMessages(): Promise<void> {
              await axios.get("/api/messages");
            }

            export async function loadNotes(): Promise<void> {
              await fetch("/api/notes");
            }
            """);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // htmx-in-TSX + ASP.NET attribute-route fixture writer: [Route("todos")] + bare [HttpPost] compose the
    // effective route /todos; the TSX hx-post and an axios.post both target it. The axios site additionally
    // emits a legacy url literal and the action an annotation endpoint — the live dedupe surface.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriteHtmxTsxAttributeRouteFixture(string repo)
    {
        string server = Path.Combine(repo, "server");
        string web = Path.Combine(repo, "web");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(web);

        File.WriteAllText(Path.Combine(server, "TodosController.cs"), """
            using Microsoft.AspNetCore.Mvc;

            namespace Demo.Api;

            [ApiController]
            [Route("todos")]
            public sealed class TodosController : ControllerBase
            {
                [HttpPost]
                public IActionResult CreateTodo() => Ok();
            }
            """);
        File.WriteAllText(Path.Combine(web, "TodoPanel.tsx"), """
            export function TodoPanel() {
              return (
                <section>
                  <button hx-post="/todos">Add</button>
                </section>
              );
            }
            """);
        File.WriteAllText(Path.Combine(web, "todos.api.ts"), """
            import axios from "axios";

            export async function createTodo(body: unknown): Promise<void> {
              await axios.post("/todos", body);
            }
            """);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // TASK 7 grouped polyglot fixture writers. Each idiomatic shape is grounded in julie-extract 2.7.0's own
    // golden fixtures (fixtures/extraction/<lang>/backend_http_boundaries) and validated against the real binary
    // so every target family emits. Cross-file layouts (mount composition, blueprint registration, Rails
    // controllers) are split across files exactly as the enrichment passes need.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    // JS/TS group: a direct express route, a SAME-FILE app.use("/users", usersRouter) mount (router created in the
    // same file — julie-extract 2.8.1's router_mount.v1 contract attests the mount target only when it is an in-file
    // express.Router() receiver; v2.8.0 over-emitted for any app.use target, including middleware and cross-file
    // imports), a fastify shorthand route, and fetch/axios clients — including a Vue SFC client. The extractor folds
    // the mount prefix into the route fact's effective_route_template (/users/:id), so the client joins the
    // already-effective route directly (composedRoutes==0). Cross-file mount composition is still proven for
    // axum/actix/phoenix in the v2.8.0 group.
    private static void WriteJsTsBackendGroup(string repo)
    {
        string server = Path.Combine(repo, "server");
        string web = Path.Combine(repo, "web");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(web);

        File.WriteAllText(Path.Combine(server, "app.ts"), """
            import express from "express";

            export function buildApp(): void {
              const app = express();
              const usersRouter = express.Router();

              usersRouter.get("/:id", (_req, res) => res.send("user"));

              app.use("/users", usersRouter);
            }
            """);
        File.WriteAllText(Path.Combine(server, "direct.ts"), """
            import express from "express";

            export function buildDirect(): void {
              const app = express();
              app.get("/direct/:id", (_req, res) => res.send("direct"));
            }
            """);
        File.WriteAllText(Path.Combine(server, "fast.ts"), """
            import fastify from "fastify";

            export function buildFast(): void {
              const server = fastify();
              server.get("/things/:id", async () => ({ ok: true }));
            }
            """);
        File.WriteAllText(Path.Combine(web, "client.ts"), """
            import axios from "axios";

            export async function loadUser(): Promise<void> {
              await axios.get("/users/1");
            }

            export async function loadDirect(): Promise<void> {
              await axios.get("/direct/1");
            }

            export async function loadThing(): Promise<void> {
              await axios.get("/things/1");
            }
            """);
        File.WriteAllText(Path.Combine(web, "legacy.js"), """
            export async function pingDirect() {
              await fetch("/direct/1");
            }
            """);
        File.WriteAllText(Path.Combine(web, "Panel.vue"), """
            <template>
              <button @click="load">load</button>
            </template>

            <script setup lang="ts">
            import axios from "axios";

            async function load() {
              await axios.get("/users/1");
            }
            </script>
            """);
    }

    // Python group: FastAPI APIRouter(prefix=...) + include_router, a cross-file Flask blueprint composed by
    // register_blueprint(url_prefix=...), Django path() + include(), and requests/httpx clients. Distinct path
    // prefixes per framework keep the join keys from colliding (a cross-family verb-exact tie would drop as
    // ambiguous). views.* is undefined on purpose — structural facts are syntactic, exactly like the golden.
    private static void WritePythonBackendGroup(string repo)
    {
        string app = Path.Combine(repo, "app");
        string shop = Path.Combine(repo, "shop");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(shop);

        File.WriteAllText(Path.Combine(app, "main.py"), """
            from fastapi import FastAPI, APIRouter
            from flask import Flask
            from users_bp import bp

            app = FastAPI()
            router = APIRouter(prefix="/api")


            @router.get("/users/{user_id}")
            def fastapi_user(user_id: str):
                pass


            app.include_router(router, prefix="/v1")

            flask_app = Flask(__name__)
            flask_app.register_blueprint(bp, url_prefix="/shop")
            """);
        File.WriteAllText(Path.Combine(app, "users_bp.py"), """
            from flask import Blueprint

            bp = Blueprint("accounts", __name__)


            @bp.get("/accounts/<int:account_id>")
            def flask_account(account_id):
                pass
            """);
        File.WriteAllText(Path.Combine(app, "urls.py"), """
            from django.urls import path, include

            urlpatterns = [
                path("users/<int:pk>/", views.detail, name="user-detail"),
                path("api/", include("shop.urls"), namespace="api"),
            ]
            """);
        File.WriteAllText(Path.Combine(shop, "urls.py"), """
            from django.urls import path

            urlpatterns = [
                path("products/<int:pk>/", views.product, name="product"),
            ]
            """);
        File.WriteAllText(Path.Combine(app, "clients.py"), """
            import requests
            import httpx


            def call_clients():
                requests.get("/api/users/1")
                httpx.get("/shop/accounts/1")
                requests.get("/users/1")
            """);
    }

    // Go group: a net/http Go-1.22 "GET /api/items/{id}" mux route (verb-attested), a gin group route plus a
    // verbless r.Any("/ping"), an echo group route (emission only), and http.Get clients. Each framework's routes
    // sit in their OWN function so the net/http and gin route facts carry DISTINCT containing symbols — otherwise
    // both client edges collapse to the same (clients -> routes) signature and the graph dedupes the Medium away.
    // The behavioral test then discriminates the net/http High edge and the gin-Any Medium edge by band.
    private static void WriteGoBackendGroup(string repo)
    {
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "main.go"), """
            package main

            import (
                "net/http"

                "github.com/gin-gonic/gin"
                "github.com/labstack/echo/v4"
            )

            func registerMux() {
                http.HandleFunc("GET /api/items/{id}", showItem)
            }

            func registerGin() {
                r := gin.Default()
                api := r.Group("/api")
                api.GET("/users/:id", showUser)
                r.Any("/ping", pingAny)
            }

            func registerEcho() {
                e := echo.New()
                v1 := e.Group("/v1")
                v1.POST("/notes/:id", createNote)
            }

            func clients() {
                http.Get("/api/items/1")
                http.Get("/ping")
            }
            """);
    }

    // Java group: a Spring @RestController with a class-level @RequestMapping prefix + @GetMapping method
    // (verb-attested), a second controller with a method-less @RequestMapping (verbless), and java.net.http
    // HttpRequest builder clients. Both controllers share the one .java file; the test discriminates by band.
    private static void WriteJavaBackendGroup(string repo)
    {
        string src = Path.Combine(repo, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "UserController.java"), """
            import java.net.URI;
            import java.net.http.HttpRequest;
            import org.springframework.web.bind.annotation.*;

            @RestController
            @RequestMapping("/api")
            class UserController {
                @GetMapping("/users/{id}")
                public User getUser() { return null; }

                void callClient() {
                    HttpRequest req = HttpRequest.newBuilder(URI.create("/api/users/1")).build();
                    HttpRequest legacy = HttpRequest.newBuilder(URI.create("/legacy")).build();
                }
            }

            @RestController
            class LegacyController {
                @RequestMapping("/legacy")
                public String legacy() { return "ok"; }
            }
            """);
    }

    // Ruby group: a config/routes.rb draw block with a verb DSL route (get ..., to: "controller#action"),
    // resources :users (Rails-semantics expansion is Miller's job), and mount ... => "/jobs" (the rails.mount.v1
    // emitter, evidence-only), a matching UsersController, and Net::HTTP clients with literal URI(...).
    private static void WriteRubyBackendGroup(string repo)
    {
        string config = Path.Combine(repo, "config");
        string controllers = Path.Combine(repo, "app", "controllers");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(controllers);

        File.WriteAllText(Path.Combine(config, "routes.rb"), """
            require "net/http"
            require "uri"

            Rails.application.routes.draw do
              get "/health", to: "health#show"
              resources :users
              mount Sidekiq::Web => "/jobs"
            end
            """);
        File.WriteAllText(Path.Combine(controllers, "users_controller.rb"), """
            class UsersController < ApplicationController
              def index
              end

              def show
              end
            end
            """);
        File.WriteAllText(Path.Combine(repo, "app", "clients.rb"), """
            require "net/http"
            require "uri"

            def call_clients
              Net::HTTP.get(URI("/health"))
              Net::HTTP.get(URI("/users/1"))
            end
            """);
    }

    // C# group (Task 5 live proof): an attribute-routed controller ([Route("api/users")] + [HttpGet("{id}")]) and
    // a service HttpClient whose GetFromJsonAsync target is the parameterized /api/users/{id} — dotnet-web's
    // RouteBridge requires exact canonical-route equality, so the {id}-literal client folds onto the endpoint.
    private static void WriteCsharpBackendGroup(string repo)
    {
        string server = Path.Combine(repo, "server");
        string client = Path.Combine(repo, "client");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(client);

        File.WriteAllText(Path.Combine(server, "UsersController.cs"), """
            using Microsoft.AspNetCore.Mvc;

            namespace Demo.Api;

            [ApiController]
            [Route("api/users")]
            public sealed class UsersController : ControllerBase
            {
                [HttpGet("{id}")]
                public IActionResult GetUser(int id) => Ok();
            }
            """);
        File.WriteAllText(Path.Combine(client, "UserApiClient.cs"), """
            using System.Net.Http;
            using System.Net.Http.Json;

            namespace Demo.Client;

            public sealed class UserApiClient
            {
                public async Task Load(HttpClient client)
                {
                    await client.GetFromJsonAsync<object>("/api/users/{id}");
                }
            }
            """);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Undisciplined fixture writer (the honesty-probe traps).
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void WriteUndisciplinedFixture(string repo)
    {
        string cs = Path.Combine(repo, "server");
        string ts = Path.Combine(repo, "web");
        string tests = Path.Combine(repo, "server.tests");
        Directory.CreateDirectory(cs);
        Directory.CreateDirectory(ts);
        Directory.CreateDirectory(tests);

        // CLEAN ground-truth shapes (these SHOULD bridge).
        File.WriteAllText(Path.Combine(cs, "Customer.cs"), """
            namespace Shop.Domain;

            public sealed class Customer
            {
                public int Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public string Email { get; set; } = string.Empty;
            }
            """);
        File.WriteAllText(Path.Combine(cs, "CustomerDto.cs"), """
            namespace Shop.Contracts;

            public sealed class CustomerDto
            {
                public int Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public string Email { get; set; } = string.Empty;
            }
            """);
        File.WriteAllText(Path.Combine(cs, "UpdateCustomerRequest.cs"), """
            namespace Shop.Contracts;

            public sealed class UpdateCustomerRequest
            {
                public string Name { get; set; } = string.Empty;
                public string Email { get; set; } = string.Empty;
            }
            """);

        // EF DbContext: DbSet<Customer> Customers (Leg 3 ground truth).
        File.WriteAllText(Path.Combine(cs, "ShopDbContext.cs"), """
            using Microsoft.EntityFrameworkCore;
            using Shop.Domain;

            namespace Shop.Data;

            public sealed class ShopDbContext : DbContext
            {
                public DbSet<Customer> Customers { get; set; } = null!;
            }
            """);

        // AutoMapper: the clean map + an INBOUND map (Request->entity) + the AMBIGUOUS Account map.
        File.WriteAllText(Path.Combine(cs, "ShopProfile.cs"), """
            using AutoMapper;
            using Shop.Domain;
            using Shop.Contracts;

            namespace Shop.Mapping;

            public sealed class ShopProfile : Profile
            {
                public ShopProfile()
                {
                    CreateMap<Customer, CustomerDto>();
                    CreateMap<UpdateCustomerRequest, Customer>();
                    CreateMap<Billing.Account, AccountDto>();
                }
            }
            """);

        // AMBIGUOUS duplicate type name "Account" in two namespaces (trap (a)).
        File.WriteAllText(Path.Combine(cs, "BillingAccount.cs"), """
            namespace Billing;

            public sealed class Account
            {
                public int Id { get; set; }
                public decimal Balance { get; set; }
            }
            """);
        File.WriteAllText(Path.Combine(cs, "CrmAccount.cs"), """
            namespace Crm;

            public sealed class Account
            {
                public int Id { get; set; }
                public string Owner { get; set; } = string.Empty;
            }
            """);
        File.WriteAllText(Path.Combine(cs, "AccountDto.cs"), """
            namespace Shop.Contracts;

            public sealed class AccountDto
            {
                public int Id { get; set; }
                public decimal Balance { get; set; }
            }
            """);

        // 1-FIELD WRAPPER pair sharing only "Id" (trap (b)) — no CreateMap, must NEVER anchor a field-set edge.
        File.WriteAllText(Path.Combine(cs, "IdHolder.cs"), """
            namespace Shop.Util;

            public sealed class IdHolder
            {
                public int Id { get; set; }
            }
            """);
        File.WriteAllText(Path.Combine(cs, "IdHolderDto.cs"), """
            namespace Shop.Contracts;

            public sealed class IdHolderDto
            {
                public int Id { get; set; }
            }
            """);

        // Controller with a POST endpoint CreateOrder (Leg 1 ground truth) + a GET reports endpoint.
        File.WriteAllText(Path.Combine(cs, "OrdersController.cs"), """
            using Microsoft.AspNetCore.Mvc;
            using Shop.Contracts;

            namespace Shop.Api;

            [ApiController]
            [Route("api/[controller]")]
            public sealed class OrdersController : ControllerBase
            {
                [HttpPost]
                public ActionResult CreateOrder([FromBody] CustomerDto body) => Ok();
            }
            """);
        File.WriteAllText(Path.Combine(cs, "ReportsController.cs"), """
            using Microsoft.AspNetCore.Mvc;

            namespace Shop.Api;

            [ApiController]
            [Route("api/[controller]")]
            public sealed class ReportsController : ControllerBase
            {
                [HttpGet]
                public ActionResult GetReports() => Ok();
            }
            """);

        // TS client: a STRONG axios.post (ground truth) + a verb-less fetch (trap (c)).
        File.WriteAllText(Path.Combine(ts, "orders.api.ts"), """
            import axios from "axios";

            export interface CustomerDto {
              id: number;
              name: string;
              email: string;
            }

            export async function createOrder(body: CustomerDto): Promise<void> {
              await axios.post("/api/orders", body);
            }

            export async function loadReports(): Promise<unknown> {
              const res = await fetch("/api/reports");
              return res.json();
            }
            """);

        // C# TEST file with an HttpClient url literal "/api/secret" (trap (d)) — must be excluded by language+test_role.
        File.WriteAllText(Path.Combine(tests, "SecretEndpointTests.cs"), """
            using System.Net.Http;
            using System.Threading.Tasks;
            using Xunit;

            namespace Shop.Tests;

            public sealed class SecretEndpointTests
            {
                [Fact]
                public async Task Secret_IsReachable()
                {
                    using var client = new HttpClient();
                    var res = await client.GetAsync("/api/secret");
                    Assert.NotNull(res);
                }
            }
            """);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Honesty classification of one emitted edge: is it a correct bridge or a false positive?
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static (bool ok, string why) ClassifyHonesty(BridgeGraph graph, ScoredEdge edge)
    {
        string src = EndpointDisplayOf(graph, edge, EndpointSide.Source);
        string tgt = EndpointDisplayOf(graph, edge, EndpointSide.Target);
        var names = new HashSet<string>(new[] { src, tgt }, StringComparer.Ordinal);

        // Correct ground-truth bridges.
        if (edge.Edge.Kind == BridgeKind.StoredIn && names.Contains("Customer") && names.Contains("Customers"))
            return (true, "Leg3 Customer->Customers");
        if (edge.Edge.Kind == BridgeKind.MapsTo && names.Contains("Customer") && names.Contains("CustomerDto"))
            return (true, "Leg2 Customer<->CustomerDto");
        if (edge.Edge.Kind == BridgeKind.Hits && names.Contains("CreateOrder"))
            return (true, "Leg1 POST /api/orders -> CreateOrder");

        // The inbound CreateMap<UpdateCustomerRequest, Customer> is a REAL, correct structural edge (CreateMap is a
        // valid breadcrumb). It is correct as long as the Request stays a DTO-side endpoint and it is not High-via-name.
        if (edge.Edge.Kind == BridgeKind.MapsTo && names.Contains("UpdateCustomerRequest") && names.Contains("Customer"))
            return (true, "Leg2 inbound CreateMap (structural; Request not mislabeled as entity)");

        // The Account CreateMap is a real structural breadcrumb. It is acceptable as long as it never becomes a
        // name-anchored High (the ambiguous-name guard owns that, asserted separately). Count as a structural TP.
        if (edge.Edge.Kind == BridgeKind.MapsTo && (names.Contains("AccountDto") || names.Contains("Account")))
            return (true, "Leg2 Account CreateMap (structural breadcrumb; ambiguity capped, not High)");

        // CustomerDto is the request-body type of POST /api/orders, so a Consumes endpoint->DTO edge
        // (CreateOrder -> CustomerDto) is a legitimate structural Leg-1 edge.
        if (edge.Edge.Kind == BridgeKind.Consumes && names.Contains("CreateOrder") && names.Contains("CustomerDto"))
            return (true, "Leg1 consumes CreateOrder -> CustomerDto ([FromBody])");

        // GetReports is the GET /api/reports endpoint. The TS carrier is verb-less fetch, so any Hits edge it forms is
        // route-only (verb-unknown). That is a HONEST, correct route bridge (Medium, never assumed-GET): the route DOES
        // line up; only the verb is unknown, which the IsVerbUnknown flag faithfully reports (Guard 4 asserts that). It
        // is therefore a true positive, NOT a phantom — a verb-unknown route match is the designed Medium outcome.
        if (edge.Edge.Kind == BridgeKind.Hits && names.Contains("GetReports"))
            return (true, "Leg1 route-only /api/reports -> GetReports (verb-unknown, honest Medium)");

        // Anything else is a false positive (notably: an /api/secret edge, an IdHolder field-set edge, an
        // assumed-GET /api/reports edge — each of which a hard guard above also asserts must not exist).
        return (false, $"unexpected {edge.Edge.Kind} {src}->{tgt}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Extraction + graph helpers.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static MillerRepositoryIndex ExtractAndLoad(string binary, TempWorkspace work)
    {
        var runner = new JulieExtractRunner(binary);
        ExtractReport report = runner.Scan(work.Repo, work.Db, force: true);
        Assert.Equal("scan", report.Operation);
        Assert.NotEqual("failed", report.Status);
        Assert.NotNull(report.Artifact);
        Assert.Equal(MillerExtractContract.ExpectedSqliteSchemaVersion, report.Artifact!.SqliteSchemaVersion);
        Assert.Equal(MillerExtractContract.ExpectedExtractContractVersion, report.Artifact.ExtractContractVersion);
        Assert.True(report.SymbolsExtracted > 0, "scan should extract at least one symbol");

        return RepositoryIndexLoader.Load(work.Db);
    }

    private static IReadOnlyCollection<string> StructuralPatternIds(string dbPath, string like)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT pattern_id
            FROM structural_facts
            WHERE pattern_id LIKE $like
            ORDER BY pattern_id;
            """;
        command.Parameters.AddWithValue("$like", like);

        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    // How many facts one pattern id emitted for one file — proves a specific source SHAPE fired (e.g. both
    // route-handler export shapes in one route.ts).
    private static int StructuralFactCount(string dbPath, string patternId, string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM structural_facts
            WHERE pattern_id = $patternId AND path = $path;
            """;
        command.Parameters.AddWithValue("$patternId", patternId);
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyCollection<string> StructuralFactContainingSymbolIds(string dbPath, string patternId, string path)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(containing_symbol_id, '')
            FROM structural_facts
            WHERE pattern_id = $patternId AND path = $path
            ORDER BY structural_fact_id;
            """;
        command.Parameters.AddWithValue("$patternId", patternId);
        command.Parameters.AddWithValue("$path", path);

        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    // The distinct emitting files for one pattern id — proves WHERE a fact family fired (e.g. htmx from TSX).
    private static IReadOnlyCollection<string> StructuralFactPaths(string dbPath, string patternId)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT path
            FROM structural_facts
            WHERE pattern_id = $patternId
            ORDER BY path;
            """;
        command.Parameters.AddWithValue("$patternId", patternId);

        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    // The distinct languages one pattern id emitted for — proves per-client-language coverage of
    // http.client_request.v1 (js/ts, vue, python, go, java, ruby, csharp) on a real extract.
    private static IReadOnlyCollection<string> StructuralFactLanguages(string dbPath, string patternId)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT language
            FROM structural_facts
            WHERE pattern_id = $patternId
            ORDER BY language;
            """;
        command.Parameters.AddWithValue("$patternId", patternId);

        using var reader = command.ExecuteReader();
        var languages = new List<string>();
        while (reader.Read())
            languages.Add(reader.GetString(0));
        return languages;
    }

    // Every distinct bridge edge in the graph, deduped by the graph's own signature (Kind|sortedIds). BridgeGraph
    // has no node-set accessor, so we sweep node ids breadth-first from a complete seed set: every symbol id the
    // index knows (covers symbol-backed endpoints, e.g. both sides of a MapsTo) plus the synthesized table/route ids
    // the fixtures could create (covers non-symbol endpoints). Incident edges of any vertex name both endpoint ids,
    // so the BFS reaches every connected vertex from those seeds.
    private static IReadOnlyList<ScoredEdge> EnumerateDistinctEdges(MillerRepositoryIndex index, BridgeGraph graph)
    {
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>();
        foreach (var seed in SeedNodeIds(index, graph))
        {
            if (discovered.Add(seed))
                frontier.Enqueue(seed);
        }

        var seenEdges = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ScoredEdge>();
        while (frontier.Count > 0)
        {
            var id = frontier.Dequeue();
            foreach (var edge in graph.Incident(id))
            {
                string? a = BridgeGraph.NodeIdOf(edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source);
                string? b = BridgeGraph.NodeIdOf(edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target);
                if (a is null || b is null)
                    continue;

                var (x, y) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
                if (seenEdges.Add($"{edge.Edge.Kind}|{x}|{y}"))
                    result.Add(edge);

                if (discovered.Add(a)) frontier.Enqueue(a);
                if (discovered.Add(b)) frontier.Enqueue(b);
            }
        }
        return result;
    }

    // The complete seed set: every indexed symbol id (so symbol-backed components are entered) + the synthesized
    // table/route ids any fixture could produce. Filtered to actual vertices via Contains. Deterministic, needs no
    // node-enumeration accessor on BridgeGraph.
    private static IReadOnlyCollection<string> SeedNodeIds(MillerRepositoryIndex index, BridgeGraph graph)
    {
        var seeds = new List<string>();
        foreach (var symbolId in AllSymbolIds(index))
            if (graph.Contains(symbolId))
                seeds.Add(symbolId);

        foreach (var table in TableDisplays)
            seeds.Add(BridgeGraph.SynthesizeId(BridgeNodeKind.DbTable, table));
        foreach (var route in RouteDisplays)
            seeds.Add(BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, route));

        return seeds.Where(graph.Contains).Distinct(StringComparer.Ordinal).ToList();
    }

    private static readonly string[] TableDisplays = { "AppSettings", "Customers", "Reports", "Orders" };

    private static readonly string[] RouteDisplays =
    {
        "/api/appsettings/{}", "api/appsettings/{}", "/api/orders", "api/orders",
        "/api/reports", "api/reports", "/api/secret", "api/secret",
        "/api/appsettings", "api/appsettings",
    };

    // Every symbol id the index holds (DocIds are contiguous 0..DocumentCount-1).
    private static IReadOnlyCollection<string> AllSymbolIds(MillerRepositoryIndex index)
    {
        var ids = new List<string>(index.DocumentCount);
        for (int docId = 0; docId < index.DocumentCount; docId++)
            ids.Add(index.Resolve(docId).SymbolId);
        return ids;
    }

    private static ScoredEdge SingleEdgeOfKind(BridgeGraph graph, string startId, BridgeKind kind)
    {
        var edges = graph.Walk(startId, 3).Where(e => e.Edge.Kind == kind).ToList();
        Assert.True(edges.Count >= 1, $"expected at least one {kind} edge from {startId}, found none");
        return edges[0];
    }

    private static bool EdgeTouchesDisplay(BridgeGraph graph, ScoredEdge edge, string display) =>
        string.Equals(EndpointDisplayOf(graph, edge, EndpointSide.Source), display, StringComparison.Ordinal)
        || string.Equals(EndpointDisplayOf(graph, edge, EndpointSide.Target), display, StringComparison.Ordinal);

    private static string EndpointDisplayOf(BridgeGraph graph, ScoredEdge edge, EndpointSide side)
    {
        var edgeRef = side == EndpointSide.Source ? edge.Edge.SourceRef : edge.Edge.TargetRef;
        var id = BridgeGraph.NodeIdOf(edgeRef, edge.Edge.Kind, side);
        if (id is not null)
        {
            var node = graph.Node(id);
            if (node is not null)
                return node.Display;
        }
        // Fall back to the ref's own display (set by the leg) when the node is not in the lookup.
        return edgeRef.Display ?? string.Empty;
    }

    private static string DescribeEdge(BridgeGraph graph, ScoredEdge edge) =>
        $"{EndpointDisplayOf(graph, edge, EndpointSide.Source)} --{edge.Edge.Kind}--> {EndpointDisplayOf(graph, edge, EndpointSide.Target)}";

    private static string FindSymbolId(MillerRepositoryIndex index, string name, string kind)
    {
        string? id = FindSymbolIdOrNull(index, name, kind);
        Assert.True(id is not null, $"fixture symbol '{name}' (kind {kind}) was not extracted/indexed");
        return id!;
    }

    // A TS export yields TWO same-name symbols (an `export`-kind wrapper plus the `function` itself) and the
    // extractor may bind a fact to either, so handler assertions check membership in the full same-name,
    // same-file id set instead of guessing one (relative-unix paths per IndexedSymbol).
    private static IReadOnlyCollection<string> SymbolIdsInFile(MillerRepositoryIndex index, string name, string filePath)
    {
        var ids = index.FindByName(name)
            .Where(s => string.Equals(s.FilePath, filePath, StringComparison.Ordinal))
            .Select(s => s.SymbolId)
            .ToList();
        Assert.True(ids.Count > 0, $"fixture symbol '{name}' in '{filePath}' was not extracted/indexed");
        return ids;
    }

    private static string? FindSymbolIdOrNull(MillerRepositoryIndex index, string name, string kind)
    {
        var matches = index.FindByName(name)
            .Where(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            matches = index.FindByName(name).ToList();
        return matches.Count > 0 ? matches[0].SymbolId : null;
    }

    private static string Fmt(double value) =>
        value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Throwaway workspace: a repo dir to extract + the DB the extract writes to. MILLER_KEEP_FIXTURE=1 keeps it.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private readonly struct TempWorkspace : IDisposable
    {
        public string Root { get; }
        public string Repo => Path.Combine(Root, "repo");
        public string Db => Path.Combine(Root, "symbols.db");

        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "miller-lbt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Repo);
        }

        public void Dispose()
        {
            if (Environment.GetEnvironmentVariable("MILLER_KEEP_FIXTURE") == "1")
                return;
            try { Directory.Delete(Root, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
