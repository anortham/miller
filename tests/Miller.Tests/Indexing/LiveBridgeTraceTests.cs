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
              return <Link href="/settings">Settings</Link>;
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
              <NuxtLink to="/about">About</NuxtLink>
            </template>
            """);
        File.WriteAllText(Path.Combine(composables, "useMarker.ts"), """
            export function useMarker() {
              return "marker";
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
