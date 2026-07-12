using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Core.Resolver;
using System.Text.Json;
using Xunit;

namespace Miller.Tests.Graph;

/// <summary>
/// Tests for <see cref="BridgeGraphBuilder"/> (plan Task 8): the per-leg reductions (CreateMap grouping with ordinal
/// direction, controller-endpoint reduction with the [FromBody] honesty guard, TsClientCall reduction) plus the
/// end-to-end run legs to scorer to graph. Pure, in-memory fixtures; no DB.
///
/// <para>Every fixture uses ONLY the verified lean contracts:
/// <c>TypeArgument(IdentifierId, Ordinal, ParentArgId, TypeName, FilePath)</c> and
/// <c>SymbolDetail(Id, Name, Kind, FilePath, Signature, Namespace, IsTest, ParentClassName)</c>. The endpoint
/// class <c>[Route]</c> join is by <see cref="SymbolDetail.ParentClassName"/> (the contract carries no parent symbol
/// id), and the Dapper-FROM secondary anchor is not expressible (the contract carries no per-arg containing-symbol id
/// or span), so the entity-table edge here is exercised via the <c>DbSet&lt;T&gt;</c> PRIMARY breadcrumb.</para>
/// </summary>
public sealed class BridgeGraphBuilderTests
{
    private static SymbolDetail Type(string id, string name, string kind = "class", string? ns = null, string file = "src/X.cs") =>
        new(id, name, kind, file, Signature: name, Namespace: ns, IsTest: false, ParentClassName: null);

    private static SymbolDetail Method(string id, string name, string signature, string parentClassName, string file) =>
        new(id, name, "method", file, signature, Namespace: "Api.Controllers", IsTest: false, ParentClassName: parentClassName);

    private static TypeArgument Arg(string identifierId, int ordinal, string typeName, string file = "src/Profile.cs") =>
        new(IdentifierId: identifierId, Ordinal: ordinal, ParentArgId: null, TypeName: typeName, FilePath: file);

    private static DbSetProperty DbSet(string table, string entity, string file = "src/Db.cs", int line = 10) =>
        new("prop:" + table, table, entity, file, line);

    private static StructuralFactRecord Fact(
        string id,
        string patternId,
        string language,
        string path,
        string containingSymbolId,
        int startByte,
        IReadOnlyDictionary<string, string> metadata) =>
        new(
            id,
            patternId,
            language,
            path,
            CaptureName: "capture",
            NodeKind: "node",
            ContainingSymbolId: containingSymbolId,
            Span: new StructuralFactSpan(1, 0, 1, 1, startByte, startByte + 1),
            Confidence: 1.0,
            Metadata: metadata);

    [Fact]
    public void CreateMap_chain_resolves_UserDto_to_ApplicationUser_to_table_with_High_edges()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("sym-userdto", "UserDto", "class", "Api.Dtos", "src/Dtos/UserDto.cs"),
            Type("sym-appuser", "ApplicationUser", "class", "Domain", "src/Domain/User.cs"),
        };

        var typeArgs = new List<TypeArgument>
        {
            Arg("cm1", 0, "UserDto"),
            Arg("cm1", 1, "ApplicationUser"),
        };

        var dbSets = new List<DbSetProperty> { DbSet("ApplicationUsers", "ApplicationUser") };

        var graph = BridgeGraphBuilder.Build(symbols, typeArgs, literals: [], annotations: [], dbSetProperties: dbSets);

        var fromDto = graph.Walk("sym-userdto", maxDepth: 5);
        Assert.Contains(fromDto, e => e.Edge.Kind == BridgeKind.MapsTo && e.Band == ConfidenceBand.High);

        var fromEntity = graph.Walk("sym-appuser", maxDepth: 5);
        Assert.Contains(fromEntity, e => e.Edge.Kind == BridgeKind.StoredIn && e.Band == ConfidenceBand.High);

        Assert.Equal(2, graph.Walk("sym-userdto", maxDepth: 5).Count);
    }

    [Fact]
    public void CreateMap_edge_direction_is_source_to_dest_not_flipped()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("sym-req", "CreateOrderRequest", "class", "Api.Requests"),
            Type("sym-order", "Order", "class", "Domain"),
        };

        var typeArgs = new List<TypeArgument>
        {
            Arg("cm1", 0, "CreateOrderRequest"),
            Arg("cm1", 1, "Order"),
        };

        var graph = BridgeGraphBuilder.Build(symbols, typeArgs, [], [], []);

        var edge = graph.Incident("sym-req").Single(e => e.Edge.Kind == BridgeKind.MapsTo);
        Assert.Equal("sym-req", edge.Edge.SourceRef.SymbolId);
        Assert.Equal("sym-order", edge.Edge.TargetRef.SymbolId);
    }

    [Fact]
    public void CreateMap_ignores_nested_generic_args_and_partial_and_oversized_groups()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("sym-a", "A"),
            Type("sym-b", "B"),
        };

        var typeArgs = new List<TypeArgument>
        {
            Arg("cm1", 0, "A"),
            Arg("cm1", 1, "B"),
            new(IdentifierId: "cm1", Ordinal: 0, ParentArgId: "outer", TypeName: "Nested", FilePath: "src/Profile.cs"),
            Arg("cm2", 0, "OnlySource"),
            Arg("cm3", 0, "K"),
            Arg("cm3", 1, "V"),
            Arg("cm3", 2, "W"),
        };

        var graph = BridgeGraphBuilder.Build(symbols, typeArgs, [], [], []);

        Assert.Single(graph.Incident("sym-a"), e => e.Edge.Kind == BridgeKind.MapsTo);
    }

    [Fact]
    public void Ambiguous_entity_name_yields_no_High_edge()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("sym-userdto", "UserDto", "class", "Api.Dtos", "api/Dtos/UserDto.cs"),
            Type("sym-appuser-1", "ApplicationUser", "class", "Domain.A", "domainA/User.cs"),
            Type("sym-appuser-2", "ApplicationUser", "class", "Domain.B", "domainB/User.cs"),
        };

        var typeArgs = new List<TypeArgument>
        {
            Arg("cm1", 0, "UserDto", "shared/Profile.cs"),
            Arg("cm1", 1, "ApplicationUser", "shared/Profile.cs"),
        };

        var graph = BridgeGraphBuilder.Build(symbols, typeArgs, [], [], []);

        var dtoEdges = graph.Incident("sym-userdto");
        Assert.All(dtoEdges, e => Assert.NotEqual(ConfidenceBand.High, e.Band));
    }

    [Fact]
    public void DbSet_property_resolves_an_entity_to_table_StoredIn_edge()
    {
        var symbols = new List<SymbolDetail> { Type("sym-appsetting", "AppSetting", "class", "Domain") };
        var dbSets = new List<DbSetProperty> { DbSet("AppSettings", "AppSetting") };

        var graph = BridgeGraphBuilder.Build(symbols, typeArguments: [], literals: [], annotations: [], dbSetProperties: dbSets);

        Assert.Contains(graph.Incident("sym-appsetting"), e => e.Edge.Kind == BridgeKind.StoredIn);
    }

    [Fact]
    public void DbSet_entity_is_taken_from_the_generic_arg_not_the_property_name()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("sym-appsetting", "AppSetting", "class", "Domain"),
            Type("sym-plural", "AppSettings", "class", "Domain"),
        };
        var dbSets = new List<DbSetProperty> { DbSet("AppSettings", "AppSetting") };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], dbSets);

        Assert.Contains(graph.Incident("sym-appsetting"), e => e.Edge.Kind == BridgeKind.StoredIn);
        Assert.DoesNotContain(graph.Incident("sym-plural"), e => e.Edge.Kind == BridgeKind.StoredIn);
    }

    [Fact]
    public void Build_DefaultProvider_ReportsDotnetWebAndNextJsCapability()
    {
        var symbols = new List<SymbolDetail> { Type("sym-appsetting", "AppSetting", "class", "Domain") };
        var dbSets = new List<DbSetProperty> { DbSet("AppSettings", "AppSetting") };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], dbSets);

        Assert.Contains("dotnet-web", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.dbsets"]);
        Assert.Contains(graph.CapabilityReport.SkippedProviders, skipped =>
            skipped.ProviderId == "nextjs" &&
            skipped.Reason.Contains("no nextjs bridge evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(graph.CapabilityReport.SkippedProviders, skipped =>
            skipped.ProviderId == "nuxt" &&
            skipped.Reason.Contains("no nuxt bridge evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.routeReferences"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.fileRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.ambiguousMatches"]);
        Assert.Contains(graph.CapabilityReport.SkippedProviders, skipped =>
            skipped.ProviderId == "nextjs-api" &&
            skipped.Reason.Contains("no nextjs-api bridge evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(graph.CapabilityReport.SkippedProviders, skipped =>
            skipped.ProviderId == "nuxt-api" &&
            skipped.Reason.Contains("no nuxt-api bridge evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs-api.clientRequests"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs-api.routeHandlers"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs-api.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs-api.ambiguousMatches"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nuxt-api.clientRequests"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nuxt-api.serverRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nuxt-api.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nuxt-api.ambiguousMatches"]);
    }

    [Fact]
    public void Build_ExplicitEmptyProviderSet_DisablesDotnetWebBridge()
    {
        var symbols = new List<SymbolDetail> { Type("sym-appsetting", "AppSetting", "class", "Domain") };
        var dbSets = new List<DbSetProperty> { DbSet("AppSettings", "AppSetting") };

        var graph = BridgeGraphBuilder.Build(
            symbols,
            [],
            [],
            [],
            dbSets,
            providers: []);

        Assert.Empty(graph.Incident("sym-appsetting"));
        Assert.Empty(graph.CapabilityReport.ActiveProviders);
        Assert.Contains(
            graph.CapabilityReport.Notes,
            note => note.Contains("no bridge providers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Controller_endpoint_reduction_builds_the_expanded_route_and_hits_edge()
    {
        var classSym = Type("sym-class", "AppSettingsController", "class", "Api.Controllers", "api/AppSettingsController.cs");
        var methodSym = Method("sym-get", "Get", "Task<ActionResult<AppSetting>> Get(int id)",
            "AppSettingsController", "api/AppSettingsController.cs");
        var dto = Type("sym-appsetting", "AppSetting", "class", "Domain");
        var tsFn = Type("sym-tsfn", "fetchAppSetting", "function", file: "web/api.ts");

        var symbols = new List<SymbolDetail> { classSym, methodSym, dto, tsFn };

        var annotations = new List<SymbolAnnotation>
        {
            new(SymbolId: "sym-class", Ordinal: 0, Annotation: "Route", AnnotationKey: "route",
                RawText: "Route(\"api/[controller]\")", Carrier: "Route"),
            new(SymbolId: "sym-get", Ordinal: 0, Annotation: "HttpGet", AnnotationKey: "httpget",
                RawText: "HttpGet(\"{id}\")", Carrier: "HttpGet"),
        };

        var literal = MakeLiteral("/api/appsettings/{}", kind: "url", language: "typescript",
            carrier: "axios.get", containingSymbolId: "sym-tsfn", spanStart: 0);

        var graph = BridgeGraphBuilder.Build(symbols, typeArguments: [], literals: [literal], annotations: annotations, dbSetProperties: []);

        var endpointEdges = graph.Incident("sym-get");
        Assert.Contains(endpointEdges, e => e.Edge.Kind == BridgeKind.Hits && e.Band == ConfidenceBand.High);
        Assert.Contains(endpointEdges, e => e.Edge.Kind == BridgeKind.Responds);
    }

    [Fact]
    public void StructuralFacts_htmx_get_hits_minimal_api_mapget_with_client_and_endpoint_evidence()
    {
        var mapGet = Method("sym-mapget", "MapGet", "IResult MapGet()", "Program", "api/Program.cs");
        var htmlNode = Type("sym-htmx", "loadTodos", "element", file: "web/index.html");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-mapget",
                patternId: "aspnet.minimal_api.route.v1",
                language: "csharp",
                path: "api/Program.cs",
                containingSymbolId: "sym-mapget",
                startLine: 12,
                metadataJson: """{"verb":"GET","route_template":"/todos"}"""),
            StructuralFact(
                factId: "fact-hx-get",
                patternId: "htmx.attribute.v1",
                language: "html",
                path: "web/index.html",
                containingSymbolId: "sym-htmx",
                startLine: 4,
                metadataJson: """{"attribute_name":"hx-get","verb":"GET","target_path":"/todos"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [mapGet, htmlNode],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-mapget"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Contains(hit.Edge.Evidence, e => e.FilePath == "api/Program.cs" && e.Line == 12);
        Assert.Contains(hit.Edge.Evidence, e => e.FilePath == "web/index.html" && e.Line == 4);
        Assert.Equal(2, graph.CapabilityReport.EvidenceCounts["dotnet-web.structuralFacts"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.aspnetMinimalRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.htmxCalls"]);
    }

    [Fact]
    public void StructuralFacts_htmx_post_does_not_match_minimal_api_mapget_for_same_route()
    {
        var mapGet = Method("sym-mapget", "MapGet", "IResult MapGet()", "Program", "api/Program.cs");
        var htmlNode = Type("sym-htmx", "createTodo", "element", file: "web/index.html");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-mapget",
                patternId: "aspnet.minimal_api.route.v1",
                language: "csharp",
                path: "api/Program.cs",
                containingSymbolId: "sym-mapget",
                startLine: 12,
                metadataJson: """{"verb":"GET","route_template":"/todos"}"""),
            StructuralFact(
                factId: "fact-hx-post",
                patternId: "htmx.attribute.v1",
                language: "html",
                path: "web/index.html",
                containingSymbolId: "sym-htmx",
                startLine: 4,
                metadataJson: """{"attribute_name":"hx-post","verb":"POST","target_path":"/todos"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [mapGet, htmlNode],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-mapget"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Theory]
    [InlineData("data-hx-post")]
    [InlineData("DATA-HX-POST")]
    public void StructuralFacts_data_hx_post_hits_minimal_api_mappost_with_attested_verb(string attributeName)
    {
        var mapPost = Method("sym-mappost", "MapPost", "IResult MapPost()", "Program", "api/Program.cs");
        var htmlNode = Type("sym-htmx", "createTodo", "element", file: "web/index.html");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-mappost",
                patternId: "aspnet.minimal_api.route.v1",
                language: "csharp",
                path: "api/Program.cs",
                containingSymbolId: "sym-mappost",
                startLine: 12,
                metadataJson: """{"verb":"POST","route_template":"/todos"}"""),
            StructuralFact(
                factId: "fact-data-hx-post",
                patternId: "htmx.attribute.v1",
                language: "html",
                path: "web/index.html",
                containingSymbolId: "sym-htmx",
                startLine: 4,
                metadataJson: $$"""{"attribute_name":"{{attributeName}}","target_path":"/todos"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [mapPost, htmlNode],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-mappost"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.htmxCalls"]);
    }

    [Fact]
    public void StructuralFacts_htmx_non_route_attributes_do_not_produce_client_calls()
    {
        var mapGet = Method("sym-mapget", "MapGet", "IResult MapGet()", "Program", "api/Program.cs");
        var htmlNode = Type("sym-htmx", "targetTodos", "element", file: "web/index.html");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-mapget",
                patternId: "aspnet.minimal_api.route.v1",
                language: "csharp",
                path: "api/Program.cs",
                containingSymbolId: "sym-mapget",
                startLine: 12,
                metadataJson: """{"verb":"GET","route_template":"/todos"}"""),
            StructuralFact(
                factId: "fact-hx-target",
                patternId: "htmx.attribute.v1",
                language: "html",
                path: "web/index.html",
                containingSymbolId: "sym-htmx",
                startLine: 4,
                metadataJson: """{"attribute_name":"hx-target","verb":"GET","target_path":"/todos"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [mapGet, htmlNode],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-mapget"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.aspnetMinimalRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["dotnet-web.htmxCalls"]);
    }

    [Fact]
    public void StructuralFacts_vue_router_link_hits_minimal_api_mapget_with_client_and_endpoint_evidence()
    {
        var mapGet = Method("sym-mapget", "MapGet", "IResult MapGet()", "Program", "api/Program.cs");
        var vueNode = Type("sym-vue", "TodoLink", "component", file: "web/TodoLink.vue");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-mapget",
                patternId: "aspnet.minimal_api.route.v1",
                language: "csharp",
                path: "api/Program.cs",
                containingSymbolId: "sym-mapget",
                startLine: 12,
                metadataJson: """{"verb":"GET","route_template":"/todos"}"""),
            StructuralFact(
                factId: "fact-vue-router-link",
                patternId: "vue.route_reference.v1",
                language: "vue",
                path: "web/TodoLink.vue",
                containingSymbolId: "sym-vue",
                startLine: 6,
                metadataJson: """{"source_kind":"RouterLink","attribute_name":"to","target_path":"/todos"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [mapGet, vueNode],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-mapget"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.Medium, hit.Band);
        Assert.True(hit.IsVerbUnknown);
        Assert.Contains(hit.Edge.Evidence, e => e.FilePath == "api/Program.cs" && e.Line == 12);
        Assert.Contains(hit.Edge.Evidence, e => e.FilePath == "web/TodoLink.vue" && e.Line == 6);
        Assert.Equal(2, graph.CapabilityReport.EvidenceCounts["dotnet-web.structuralFacts"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.aspnetMinimalRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.vueCalls"]);
    }

    [Fact]
    public void StructuralFacts_vue_bound_to_literal_hits_minimal_api_mapget()
    {
        var mapGet = Method("sym-mapget", "MapGet", "IResult MapGet()", "Program", "api/Program.cs");
        var vueNode = Type("sym-vue", "TodoLink", "component", file: "web/TodoLink.vue");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-mapget",
                patternId: "aspnet.minimal_api.route.v1",
                language: "csharp",
                path: "api/Program.cs",
                containingSymbolId: "sym-mapget",
                startLine: 12,
                metadataJson: """{"verb":"GET","route_template":"/todos"}"""),
            StructuralFact(
                factId: "fact-vue-bound-to",
                patternId: "vue.route_reference.v1",
                language: "vue",
                path: "web/TodoLink.vue",
                containingSymbolId: "sym-vue",
                startLine: 7,
                metadataJson: """{"source_kind":"bound_attribute","attribute_name":":to","expression":"'\/todos'","target_path":"/todos"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [mapGet, vueNode],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-mapget"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.Medium, hit.Band);
        Assert.True(hit.IsVerbUnknown);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.vueCalls"]);
    }

    [Fact]
    public void StructuralFacts_vue_route_facts_without_target_path_do_not_produce_client_calls()
    {
        var mapGet = Method("sym-mapget", "MapGet", "IResult MapGet()", "Program", "api/Program.cs");
        var vueNode = Type("sym-vue", "TodoLink", "component", file: "web/TodoLink.vue");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-mapget",
                patternId: "aspnet.minimal_api.route.v1",
                language: "csharp",
                path: "api/Program.cs",
                containingSymbolId: "sym-mapget",
                startLine: 12,
                metadataJson: """{"verb":"GET","route_template":"/todos"}"""),
            StructuralFact(
                factId: "fact-vue-missing-target",
                patternId: "vue.route_reference.v1",
                language: "vue",
                path: "web/TodoLink.vue",
                containingSymbolId: "sym-vue",
                startLine: 7,
                metadataJson: """{"source_kind":"RouterLink","attribute_name":"to","verb":"GET"}"""),
            StructuralFact(
                factId: "fact-vue-nonliteral-expression",
                patternId: "vue.route_reference.v1",
                language: "vue",
                path: "web/TodoLink.vue",
                containingSymbolId: "sym-vue",
                startLine: 8,
                metadataJson: """{"source_kind":"bound_attribute","attribute_name":":to","expression":"todo.href","verb":"GET"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [mapGet, vueNode],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-mapget"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.aspnetMinimalRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["dotnet-web.vueCalls"]);
    }

    [Fact]
    public void Client_call_container_symbol_is_a_bridge_start_node()
    {
        var classSym = Type("sym-class", "ReportMenuController", "class", "Api.Controllers",
            "MyraNext/MyraNext.Web/Controllers/ReportMenuController.cs");
        var methodSym = Method("sym-put", "Put", "Task<ActionResult> Put(IEnumerable<ReportMenuGroup> menu)",
            "ReportMenuController", "MyraNext/MyraNext.Web/Controllers/ReportMenuController.cs");
        var tsFn = Type("sym-update-report-menu", "updateReportMenuOrder", "function",
            file: "MyraNext/MyraNext.Web/ClientApp/src/services/api/reportMenuService.ts");
        var symbols = new List<SymbolDetail> { classSym, methodSym, tsFn };

        var annotations = new List<SymbolAnnotation>
        {
            new("sym-class", 0, "Route", "route", "Route(\"api/[controller]\")", "Route"),
            new("sym-put", 0, "HttpPut", "httpput", "HttpPut", "HttpPut"),
        };

        var literal = MakeLiteral("/api/reportmenu", kind: "url", language: "typescript",
            carrier: "axios.put", containingSymbolId: "sym-update-report-menu", spanStart: 0);

        var graph = BridgeGraphBuilder.Build(symbols, [], [literal], annotations, []);

        var fromClientFunction = graph.Incident("sym-update-report-menu");
        var hits = Assert.Single(fromClientFunction, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal("sym-update-report-menu", hits.Edge.SourceRef.SymbolId);
        Assert.Equal("sym-put", hits.Edge.TargetRef.SymbolId);
    }

    [Fact]
    public void Dotnet_web_resolution_ignores_generated_typescript_symbols_for_server_types()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("cs-appsetting", "AppSetting", "class", "MyraNext.Core.Domain",
                "MyraNext/MyraNext.Core/Domain/AppSetting.cs"),
            Type("ts-appsetting-interface", "AppSetting", "interface", "Client.Models",
                "MyraNext/MyraNext.Web/ClientApp/src/models/AppSetting.ts"),
            Type("ts-appsetting-export", "AppSetting", "export", "Client.Models",
                "MyraNext/MyraNext.Web/ClientApp/src/models/index.ts"),
            Type("ts-appsetting-import", "AppSetting", "import", "Client.Pages",
                "MyraNext/MyraNext.Web/ClientApp/src/pages/AppSettings.vue"),
            Type("sym-class", "AppSettingsController", "class", "Api.Controllers",
                "MyraNext/MyraNext.Web/Controllers/AppSettingsController.cs"),
            Method("sym-post", "Post", "Task<ActionResult> Post(AppSetting appSetting)",
                "AppSettingsController", "MyraNext/MyraNext.Web/Controllers/AppSettingsController.cs"),
        };

        var annotations = new List<SymbolAnnotation>
        {
            new("sym-class", 0, "Route", "route", "Route(\"api/[controller]\")", "Route"),
            new("sym-post", 0, "HttpPost", "httppost", "HttpPost", "HttpPost"),
        };

        var dbSets = new List<DbSetProperty>
        {
            DbSet("AppSettings", "AppSetting", "MyraNext/MyraNext.Core/Persistence/MyraNextContext.cs"),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], annotations, dbSets);

        Assert.Contains(graph.Incident("cs-appsetting"), e => e.Edge.Kind == BridgeKind.StoredIn);
        var consumes = Assert.Single(graph.Incident("sym-post"), e => e.Edge.Kind == BridgeKind.Consumes);
        Assert.Equal("cs-appsetting", consumes.Edge.TargetRef.SymbolId);
        Assert.DoesNotContain(graph.Incident("ts-appsetting-interface"), e => e.Edge.Kind is BridgeKind.StoredIn or BridgeKind.Consumes);
    }

    [Fact]
    public void FromBody_honesty_a_route_primitive_param_does_not_produce_a_Consumes_edge()
    {
        var classSym = Type("sym-class", "ItemsController", "class", "Api.Controllers", "api/ItemsController.cs");
        var methodSym = Method("sym-get", "GetById", "Task<ActionResult<Item>> GetById(int id)",
            "ItemsController", "api/ItemsController.cs");
        var dto = Type("sym-item", "Item", "class", "Domain");

        var symbols = new List<SymbolDetail> { classSym, methodSym, dto };

        var annotations = new List<SymbolAnnotation>
        {
            new("sym-class", 0, "Route", "route", "Route(\"api/[controller]\")", "Route"),
            new("sym-get", 0, "HttpGet", "httpget", "HttpGet(\"{id}\")", "HttpGet"),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], annotations, []);

        Assert.DoesNotContain(graph.Incident("sym-get"), e => e.Edge.Kind == BridgeKind.Consumes);
    }

    [Fact]
    public void FromBody_a_complex_param_on_a_POST_produces_a_Consumes_edge()
    {
        var classSym = Type("sym-class", "ItemsController", "class", "Api.Controllers", "api/ItemsController.cs");
        var methodSym = Method("sym-post", "Create", "Task<ActionResult<Item>> Create(CreateItemRequest request)",
            "ItemsController", "api/ItemsController.cs");
        var dto = Type("sym-item", "Item", "class", "Domain");
        var req = Type("sym-req", "CreateItemRequest", "class", "Api.Requests");

        var symbols = new List<SymbolDetail> { classSym, methodSym, dto, req };

        var annotations = new List<SymbolAnnotation>
        {
            new("sym-class", 0, "Route", "route", "Route(\"api/[controller]\")", "Route"),
            new("sym-post", 0, "HttpPost", "httppost", "HttpPost", "HttpPost"),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], annotations, []);

        var consumes = graph.Incident("sym-post").Single(e => e.Edge.Kind == BridgeKind.Consumes);
        Assert.Equal("sym-req", consumes.Edge.TargetRef.SymbolId);
    }

    [Fact]
    public void Csharp_test_httpclient_literal_does_not_produce_a_hits_edge()
    {
        var classSym = Type("sym-class", "AppSettingsController", "class", "Api.Controllers", "api/AppSettingsController.cs");
        var methodSym = Method("sym-get", "Get", "Task<ActionResult<AppSetting>> Get()",
            "AppSettingsController", "api/AppSettingsController.cs");
        var dto = Type("sym-appsetting", "AppSetting", "class", "Domain");
        var symbols = new List<SymbolDetail> { classSym, methodSym, dto };

        var annotations = new List<SymbolAnnotation>
        {
            new("sym-class", 0, "Route", "route", "Route(\"api/[controller]\")", "Route"),
            new("sym-get", 0, "HttpGet", "httpget", "HttpGet", "HttpGet"),
        };

        var literal = MakeLiteral("/api/appsettings", kind: "url", language: "csharp",
            carrier: "GetAsync", containingSymbolId: "sym-test", spanStart: 0);

        var graph = BridgeGraphBuilder.Build(symbols, [], [literal], annotations, []);

        Assert.DoesNotContain(graph.Incident("sym-get"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void Builder_accepts_a_reader_supplied_literal_site_lookup()
    {
        var classSym = Type("sym-class", "AppSettingsController", "class", "Api.Controllers", "api/AppSettingsController.cs");
        var methodSym = Method("sym-get", "Get", "Task<ActionResult<AppSetting>> Get()",
            "AppSettingsController", "api/AppSettingsController.cs");
        var dto = Type("sym-appsetting", "AppSetting", "class", "Domain");
        var tsFn = Type("sym-tsfn", "fetchAppSettings", "function", file: "web/api.ts");
        var symbols = new List<SymbolDetail> { classSym, methodSym, dto, tsFn };

        var annotations = new List<SymbolAnnotation>
        {
            new("sym-class", 0, "Route", "route", "Route(\"api/[controller]\")", "Route"),
            new("sym-get", 0, "HttpGet", "httpget", "HttpGet", "HttpGet"),
        };

        var literal = MakeLiteral("/api/appsettings", kind: "url", language: "typescript",
            carrier: "axios.get", containingSymbolId: "sym-tsfn", spanStart: 0);

        var sites = new Dictionary<LiteralRecord, LiteralSite> { [literal] = new("web/api.ts", 42) };

        var graph = BridgeGraphBuilder.Build(symbols, [], [literal], annotations, [], sites);

        Assert.Contains(graph.Incident("sym-get"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void ReduceClientCalls_TestContainerLiteral_ExcludedFromBridge()
    {
        // A url literal whose containing symbol is julie-flagged is_test => the reduced TsClientCall carries
        // IsTest=true => RouteBridge.IsRealClientCall drops it => no Hits edge, even with a matching endpoint.
        var symbols = new List<SymbolDetail>
        {
            // containing TS function, flagged test (IsTest is the 7th positional SymbolDetail param)
            new("ts.testfn", "should_call_api", "function", "web/src/api.spec.ts", "function should_call_api()", "Web", IsTest: true, ParentClassName: null),
            new("cs.endpoint", "List", "method", "Api/Controllers/AppSettingsController.cs", "Task<ActionResult> List()", "Api.Controllers", IsTest: false, ParentClassName: "AppSettingsController"),
            new("cs.ctrl", "AppSettingsController", "class", "Api/Controllers/AppSettingsController.cs", "class AppSettingsController", "Api.Controllers", IsTest: false, ParentClassName: null),
        };
        var literals = new List<LiteralRecord>
        {
            new("/api/appsettings", "url", "axios.get", 0, "typescript", "ts.testfn", new SourceSpan(0, 16)),
        };
        var annotations = new List<SymbolAnnotation>
        {
            new("cs.endpoint", 0, "HttpGet", "httpget", "HttpGet", "HttpGet"),     // verb on the method
            new("cs.ctrl", 0, "Route", "route", "Route(\"api/[controller]\")", "Route"),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], literals, annotations, []);

        // No Hits edge incident on the endpoint node: the test-flagged container's literal was dropped.
        Assert.DoesNotContain(graph.Incident("cs.endpoint"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void ReduceClientCalls_ProductionContainerLiteral_YieldsHitsEdge()
    {
        // Positive control: an identical literal whose container is NOT a test produces a Hits edge — proving the
        // previous test's exclusion is driven by the container's IsTest flag, not by the route/verb shape.
        var symbols = new List<SymbolDetail>
        {
            new("ts.fn", "callApi", "function", "web/src/api.ts", "function callApi()", "Web", IsTest: false, ParentClassName: null),
            new("cs.endpoint", "List", "method", "Api/Controllers/AppSettingsController.cs", "Task<ActionResult> List()", "Api.Controllers", IsTest: false, ParentClassName: "AppSettingsController"),
            new("cs.ctrl", "AppSettingsController", "class", "Api/Controllers/AppSettingsController.cs", "class AppSettingsController", "Api.Controllers", IsTest: false, ParentClassName: null),
        };
        var literals = new List<LiteralRecord>
        {
            new("/api/appsettings", "url", "axios.get", 0, "typescript", "ts.fn", new SourceSpan(0, 16)),
        };
        var annotations = new List<SymbolAnnotation>
        {
            new("cs.endpoint", 0, "HttpGet", "httpget", "HttpGet", "HttpGet"),
            new("cs.ctrl", 0, "Route", "route", "Route(\"api/[controller]\")", "Route"),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], literals, annotations, []);

        Assert.Contains(graph.Incident("cs.endpoint"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void StructuralFacts_VueRouteReference_YieldsHitsEdgeToMinimalApiHandler()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("vue.header", "AppHeader", "component", file: "web/AppHeader.vue"),
            Method("cs.calendar", "CalendarAsync", "Task<IResult> CalendarAsync()", string.Empty, "Api/CalendarEndpoints.cs"),
        };
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-vue-calendar",
                "vue.route_reference.v1",
                "vue",
                "web/AppHeader.vue",
                "vue.header",
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "vue",
                    ["target_path"] = "/calendar",
                    ["verb"] = "GET",
                }),
            Fact(
                "sf-route-calendar",
                "aspnet.minimal_api.route.v1",
                "csharp",
                "Api/CalendarEndpoints.cs",
                "cs.map",
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "aspnet",
                    ["route_template"] = "/calendar",
                    ["verb"] = "GET",
                    ["handler_name"] = "CalendarAsync",
                }),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], [], structuralFacts: facts);

        var hits = Assert.Single(graph.Incident("vue.header"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal("cs.calendar", hits.Edge.TargetRef.SymbolId);
        Assert.Equal(ConfidenceBand.Medium, hits.Band);
        Assert.True(hits.IsVerbUnknown);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.structuralClientCalls"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.structuralEndpoints"]);
    }

    [Fact]
    public void StructuralFacts_MinimalApiEffectiveRouteTemplate_YieldsHitsEdgeWithoutMapGroupFallback()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("htmx.form", "ConnectorForm", "component", file: "components/ConnectorForm.razor"),
            Method("cs.save", "SaveAsync", "Task<IResult> SaveAsync()", string.Empty, "Api/AdminConnectorsEndpoints.cs"),
        };
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-htmx-save",
                "htmx.attribute.v1",
                "razor",
                "components/ConnectorForm.razor",
                "htmx.form",
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "htmx",
                    ["target_path"] = "/admin/connectors/save",
                    ["verb"] = "POST",
                }),
            Fact(
                "sf-route-save",
                "aspnet.minimal_api.route.v1",
                "csharp",
                "Api/AdminConnectorsEndpoints.cs",
                "cs.map",
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "aspnet",
                    ["route_template"] = "/save",
                    ["effective_route_template"] = "/admin/connectors/save",
                    ["verb"] = "POST",
                    ["handler_name"] = "SaveAsync",
                }),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], [], structuralFacts: facts);

        var hits = Assert.Single(graph.Incident("htmx.form"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal("cs.save", hits.Edge.TargetRef.SymbolId);
        Assert.Equal(ConfidenceBand.High, hits.Band);
    }

    [Theory]
    [InlineData("vue.route_reference.v1", "vue", "web/CalendarLink.vue", "vue.link", "target_path", "/calendar")]
    [InlineData("react.route_reference.v1", "tsx", "web/App.tsx", "react.link", "target_path", "/calendar")]
    [InlineData("nextjs.route_reference.v1", "tsx", "web/nav.tsx", "next.link", "target_path", "/calendar")]
    [InlineData("nuxt.route_reference.v1", "vue", "app/components/Nav.vue", "nuxt.link", "target_path", "/calendar")]
    public void StructuralFacts_FrontendRouteFacts_YieldHitsEdgeToMinimalApiHandler(
        string frontendPattern,
        string frontendLanguage,
        string frontendPath,
        string frontendSymbol,
        string routeKey,
        string routeValue)
    {
        var symbols = new List<SymbolDetail>
        {
            Type(frontendSymbol, "CalendarRoute", "component", file: frontendPath),
            Method("cs.calendar", "CalendarAsync", "Task<IResult> CalendarAsync()", string.Empty, "Api/CalendarEndpoints.cs"),
        };
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-frontend-calendar",
                frontendPattern,
                frontendLanguage,
                frontendPath,
                frontendSymbol,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = frontendPattern.Split('.')[0],
                    [routeKey] = routeValue,
                }),
            Fact(
                "sf-route-calendar",
                "aspnet.minimal_api.route.v1",
                "csharp",
                "Api/CalendarEndpoints.cs",
                "cs.map",
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "aspnet",
                    ["route_template"] = "/calendar",
                    ["verb"] = "GET",
                    ["handler_name"] = "CalendarAsync",
                }),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], [], structuralFacts: facts);

        var hits = Assert.Single(graph.Incident(frontendSymbol), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal("cs.calendar", hits.Edge.TargetRef.SymbolId);
        Assert.Equal(ConfidenceBand.Medium, hits.Band);
        Assert.True(hits.IsVerbUnknown);
    }

    [Theory]
    [InlineData("vue.route_definition.v1", "vue", "web/router.ts", "vue.router", "target_path", "/calendar")]
    [InlineData("react.route_definition.v1", "tsx", "web/routes.tsx", "react.routes", "route_path", "/calendar")]
    [InlineData("nextjs.file_route.v1", "tsx", "web/app/calendar/page.tsx", "next.page", "route_path", "/calendar")]
    [InlineData("nuxt.file_route.v1", "vue", "app/pages/calendar.vue", "nuxt.page", "route_path", "/calendar")]
    public void StructuralFacts_DefinitionRouteFacts_DoNotYieldHitsEdgeToMinimalApiHandler(
        string frontendPattern,
        string frontendLanguage,
        string frontendPath,
        string frontendSymbol,
        string routeKey,
        string routeValue)
    {
        var symbols = new List<SymbolDetail>
        {
            Type(frontendSymbol, "CalendarRoute", "component", file: frontendPath),
            Method("cs.calendar", "CalendarAsync", "Task<IResult> CalendarAsync()", string.Empty, "Api/CalendarEndpoints.cs"),
        };
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-frontend-calendar",
                frontendPattern,
                frontendLanguage,
                frontendPath,
                frontendSymbol,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = frontendPattern.Split('.')[0],
                    [routeKey] = routeValue,
                }),
            Fact(
                "sf-route-calendar",
                "aspnet.minimal_api.route.v1",
                "csharp",
                "Api/CalendarEndpoints.cs",
                "cs.map",
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "aspnet",
                    ["route_template"] = "/calendar",
                    ["verb"] = "GET",
                    ["handler_name"] = "CalendarAsync",
                }),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], [], structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident(frontendSymbol), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["dotnet-web.structuralClientCalls"]);
    }

    [Fact]
    public void StructuralFacts_FrontendRouteFacts_FromTestPaths_AreIgnored()
    {
        var symbols = new List<SymbolDetail>
        {
            Method("cs.calendar", "CalendarAsync", "Task<IResult> CalendarAsync()", string.Empty, "Api/CalendarEndpoints.cs"),
        };
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-next-test-page",
                "nextjs.file_route.v1",
                "tsx",
                "web/app/calendar/__tests__/page.test.tsx",
                string.Empty,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["route_path"] = "/calendar",
                }),
            Fact(
                "sf-route-calendar",
                "aspnet.minimal_api.route.v1",
                "csharp",
                "Api/CalendarEndpoints.cs",
                "cs.map",
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "aspnet",
                    ["route_template"] = "/calendar",
                    ["verb"] = "GET",
                    ["handler_name"] = "CalendarAsync",
                }),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], [], structuralFacts: facts);

        Assert.Empty(graph.Edges);
        Assert.DoesNotContain(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.TsType &&
            node.Display == "/calendar" &&
            node.FilePath == "web/app/calendar/__tests__/page.test.tsx");
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["dotnet-web.structuralClientCalls"]);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadRouteReference_ReadsRouteAndUnknownVerbForNavigation()
    {
        var fact = Fact(
            "sf-react-calendar",
            "react.route_reference.v1",
            "tsx",
            "web/Nav.tsx",
            "react.link",
            100,
            new Dictionary<string, string>
            {
                ["target_path"] = "/calendar",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadRouteReference(fact, new Dictionary<string, SymbolDetail>(), out var reference));
        Assert.Equal("/calendar", reference.RoutePath);
        Assert.Null(reference.Verb);
        Assert.Equal("react.link", reference.ContainingSymbolId);
        Assert.Equal("web/Nav.tsx", reference.FilePath);
        Assert.Equal(1, reference.Line);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadRouteReference_ReadsRazorTargetPath()
    {
        var fact = Fact(
            "sf-razor-orders",
            BridgeStructuralPatterns.RazorRouteReference,
            "razor",
            "Components/NavMenu.razor",
            "route_reference",
            100,
            new Dictionary<string, string>
            {
                ["target_path"] = "/orders/42",
                ["source_kind"] = "navigate_to",
                ["route_source"] = "string_literal",
                ["framework"] = "blazor",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadRouteReference(fact, new Dictionary<string, SymbolDetail>(), out var reference));
        Assert.Equal("/orders/42", reference.RoutePath);
        Assert.Null(reference.Verb);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadFileRoute_ReadsRoutePath()
    {
        var fact = Fact(
            "sf-next-calendar",
            "nextjs.file_route.v1",
            "tsx",
            "web/app/calendar/page.tsx",
            string.Empty,
            100,
            new Dictionary<string, string>
            {
                ["route_path"] = "/calendar",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadFileRoute(fact, new Dictionary<string, SymbolDetail>(), out var route));
        Assert.Equal("/calendar", route.RoutePath);
        Assert.Equal("web/app/calendar/page.tsx", route.FilePath);
        Assert.Equal(1, route.Line);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadFileRoute_PreservesRazorRouteTemplate()
    {
        var fact = Fact(
            "sf-razor-orders-page",
            BridgeStructuralPatterns.RazorPageDirective,
            "razor",
            "Pages/Orders.razor",
            string.Empty,
            100,
            new Dictionary<string, string>
            {
                ["route_template"] = "/orders/{orderId?}",
                ["route"] = "/orders/{orderId?}",
                ["route_parameters"] = "[{\"name\":\"orderId\",\"optional\":true,\"catch_all\":false}]",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadFileRoute(fact, new Dictionary<string, SymbolDetail>(), out var route));
        Assert.Equal("/orders/{orderId?}", route.RoutePath);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadClientRequest_ReadsPathVerbAndClient()
    {
        var fact = Fact(
            "sf-fetch-messages",
            "http.client_request.v1",
            "typescript",
            "web/api.ts",
            "sym-tsfn",
            100,
            new Dictionary<string, string>
            {
                ["client"] = "fetch",
                ["framework"] = "fetch",
                ["target_path"] = "/api/messages",
                ["url_kind"] = "path",
                ["verb"] = "GET",
                ["verb_source"] = "default",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadClientRequest(fact, new Dictionary<string, SymbolDetail>(), out var request));
        Assert.Equal("/api/messages", request.RoutePath);
        Assert.Equal("GET", request.Verb);
        Assert.Equal("default", request.VerbSource);
        Assert.Equal("fetch", request.Client);
        Assert.Equal("sym-tsfn", request.ContainingSymbolId);
        Assert.Equal("web/api.ts", request.FilePath);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("absolute")]
    public void StructuralRouteFactAdapter_TryReadClientRequest_RejectsNonPathUrlKinds(string urlKind)
    {
        var fact = Fact(
            "sf-fetch-external",
            "http.client_request.v1",
            "typescript",
            "web/api.ts",
            "sym-tsfn",
            100,
            new Dictionary<string, string>
            {
                ["client"] = "fetch",
                ["target_path"] = "/api/messages",
                ["url_kind"] = urlKind,
                ["verb"] = "GET",
                ["verb_source"] = "default",
            });

        Assert.False(StructuralRouteFactAdapter.TryReadClientRequest(fact, new Dictionary<string, SymbolDetail>(), out _));
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadRouteHandler_ReadsBracketRoutePathAndVerb()
    {
        var fact = Fact(
            "sf-next-users-handler",
            "nextjs.route_handler.v1",
            "typescript",
            "web/app/api/users/[id]/route.ts",
            "sym-handler",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "nextjs",
                ["router"] = "app",
                ["route_path"] = "/api/users/[id]",
                ["normalized_route_template"] = "/api/users/:id",
                ["verb"] = "GET",
                ["verb_source"] = "attested",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadRouteHandler(fact, new Dictionary<string, SymbolDetail>(), out var handler));
        Assert.Equal("/api/users/[id]", handler.RoutePath); // bracket route_path preferred over the colon form
        Assert.Equal("GET", handler.Verb);
        Assert.Equal("sym-handler", handler.ContainingSymbolId);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadRouteHandler_SuffixlessNuxtRouteHasNullVerb()
    {
        var fact = Fact(
            "sf-nuxt-notes-handler",
            "nuxt.server_route.v1",
            "typescript",
            "server/api/notes.ts",
            string.Empty,
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "nuxt",
                ["route_path"] = "/api/notes",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadRouteHandler(fact, new Dictionary<string, SymbolDetail>(), out var handler));
        Assert.Equal("/api/notes", handler.RoutePath);
        Assert.Null(handler.Verb); // a suffix-less server route answers every method — never assumed GET
    }

    [Fact]
    public void StructuralRouteFactAdapter_IsTestFact_UsesContainerFlagAndTestPath()
    {
        var symbolsById = new Dictionary<string, SymbolDetail>
        {
            ["react.link"] = new(
                "react.link",
                "CalendarLink",
                "component",
                "web/Nav.tsx",
                Signature: "CalendarLink",
                Namespace: null,
                IsTest: true,
                ParentClassName: null),
        };
        var containerFact = Fact(
            "sf-react-calendar",
            "react.route_reference.v1",
            "tsx",
            "web/Nav.tsx",
            "react.link",
            100,
            new Dictionary<string, string>
            {
                ["target_path"] = "/calendar",
            });
        var pathFact = Fact(
            "sf-next-calendar-test",
            "nextjs.file_route.v1",
            "tsx",
            "web/app/calendar/__tests__/page.test.tsx",
            string.Empty,
            100,
            new Dictionary<string, string>
            {
                ["route_path"] = "/calendar",
            });
        var productionFact = Fact(
            "sf-next-calendar",
            "nextjs.file_route.v1",
            "tsx",
            "web/app/calendar/page.tsx",
            string.Empty,
            100,
            new Dictionary<string, string>
            {
                ["route_path"] = "/calendar",
            });

        Assert.True(StructuralRouteFactAdapter.IsTestFact(containerFact, symbolsById));
        Assert.True(StructuralRouteFactAdapter.IsTestFact(pathFact, symbolsById));
        Assert.False(StructuralRouteFactAdapter.IsTestFact(productionFact, symbolsById));
    }

    [Fact]
    public void Build_DefaultProviders_BuildsNextNavigationFromStructuralFacts()
    {
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-next-settings-link",
                "nextjs.route_reference.v1",
                "tsx",
                "web/Nav.tsx",
                string.Empty,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["target_path"] = "/settings",
                }),
            Fact(
                "sf-next-settings-page",
                "nextjs.file_route.v1",
                "tsx",
                "web/app/settings/page.tsx",
                string.Empty,
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["route_path"] = "/settings",
                }),
        };

        var graph = BridgeGraphBuilder.Build([], [], [], [], [], structuralFacts: facts);

        var edge = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.NavigatesTo);
        Assert.Equal(ConfidenceBand.High, edge.Band);
        Assert.Equal("/settings", edge.Edge.SourceRef.Display);
        Assert.Equal("/settings", edge.Edge.TargetRef.Display);
        Assert.Contains("nextjs", graph.CapabilityReport.ActiveProviders);
        Assert.DoesNotContain("dotnet-web", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs.routeReferences"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs.fileRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.ambiguousMatches"]);
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.FileRoute &&
            node.Display == "/settings" &&
            node.FilePath == "web/app/settings/page.tsx" &&
            node.Line == 1);
    }

    [Fact]
    public void Build_DefaultProviders_DeduplicatesRouteObservationNodesAcrossDotnetAndNext()
    {
        var mapGet = Method("sym-mapget", "MapGet", "IResult MapGet()", "Program", "api/Program.cs");
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-next-settings-link",
                "nextjs.route_reference.v1",
                "tsx",
                "web/Nav.tsx",
                string.Empty,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["target_path"] = "/settings",
                }),
            Fact(
                "sf-next-settings-page",
                "nextjs.file_route.v1",
                "tsx",
                "web/app/settings/page.tsx",
                string.Empty,
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["route_path"] = "/settings",
                }),
            Fact(
                "sf-aspnet-settings",
                "aspnet.minimal_api.route.v1",
                "csharp",
                "api/Program.cs",
                "sym-mapget",
                300,
                new Dictionary<string, string>
                {
                    ["framework"] = "aspnet",
                    ["route_template"] = "/settings",
                    ["verb"] = "GET",
                }),
        };

        var graph = BridgeGraphBuilder.Build([mapGet], [], [], [], [], structuralFacts: facts);

        Assert.Contains("dotnet-web", graph.CapabilityReport.ActiveProviders);
        Assert.Contains("nextjs", graph.CapabilityReport.ActiveProviders);
        Assert.Contains(graph.Edges, edge => edge.Edge.Kind == BridgeKind.Hits);
        Assert.Contains(graph.Edges, edge => edge.Edge.Kind == BridgeKind.NavigatesTo);
        Assert.Single(graph.Nodes.Values, node => node.Kind == BridgeNodeKind.TsType && node.Display == "/settings");
    }

    [Fact]
    public void Build_DefaultProviders_BuildsNextDynamicNavigationFromBracketFileRouteFact()
    {
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-next-user-link",
                "nextjs.route_reference.v1",
                "tsx",
                "web/Nav.tsx",
                string.Empty,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["target_path"] = "/users/42",
                }),
            Fact(
                "sf-next-user-page",
                "nextjs.file_route.v1",
                "tsx",
                "web/app/users/[id]/page.tsx",
                string.Empty,
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["route_path"] = "/users/[id]",
                    ["normalized_route_template"] = "/users/:id",
                }),
        };

        var graph = BridgeGraphBuilder.Build([], [], [], [], [], structuralFacts: facts);

        var edge = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.NavigatesTo);
        Assert.Equal("/users/42", edge.Edge.SourceRef.Display);
        Assert.Equal("/users/[id]", edge.Edge.TargetRef.Display);
        Assert.Equal(ConfidenceBand.High, edge.Band);
    }

    [Fact]
    public void Build_DefaultProviders_RetainsUnmatchedNextRoutesAsObservationNodes()
    {
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-next-missing-link",
                "nextjs.route_reference.v1",
                "tsx",
                "web/Nav.tsx",
                string.Empty,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["target_path"] = "/missing",
                }),
            Fact(
                "sf-next-settings-page",
                "nextjs.file_route.v1",
                "tsx",
                "web/app/settings/page.tsx",
                string.Empty,
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["route_path"] = "/settings",
                }),
        };

        var graph = BridgeGraphBuilder.Build([], [], [], [], [], structuralFacts: facts);

        Assert.DoesNotContain(graph.Edges, edge => edge.Edge.Kind == BridgeKind.NavigatesTo);
        Assert.Contains("nextjs", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.candidates"]);
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.TsType &&
            node.Display == "/missing" &&
            node.FilePath == "web/Nav.tsx" &&
            node.Line == 1);
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.FileRoute &&
            node.Display == "/settings" &&
            node.FilePath == "web/app/settings/page.tsx" &&
            node.Line == 1);
    }

    [Fact]
    public void Build_DefaultProviders_BuildsNuxtNavigationFromStructuralFacts()
    {
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-nuxt-about-link",
                "nuxt.route_reference.v1",
                "vue",
                "app/components/Nav.vue",
                string.Empty,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "nuxt",
                    ["target_path"] = "/about",
                }),
            Fact(
                "sf-nuxt-about-page",
                "nuxt.file_route.v1",
                "vue",
                "app/pages/about.vue",
                string.Empty,
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "nuxt",
                    ["route_path"] = "/about",
                }),
        };

        var graph = BridgeGraphBuilder.Build([], [], [], [], [], structuralFacts: facts);

        var edge = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.NavigatesTo);
        Assert.Equal(ConfidenceBand.High, edge.Band);
        Assert.Equal("/about", edge.Edge.SourceRef.Display);
        Assert.Equal("/about", edge.Edge.TargetRef.Display);
        Assert.Contains("nuxt", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nuxt.routeReferences"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nuxt.fileRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nuxt.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nuxt.ambiguousMatches"]);
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.FileRoute &&
            node.Display == "/about" &&
            node.FilePath == "app/pages/about.vue" &&
            node.Line == 1);
    }

    [Fact]
    public void Build_DefaultProviders_BuildsNuxtDynamicNavigationFromBracketFileRouteFact()
    {
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-nuxt-blog-link",
                "nuxt.route_reference.v1",
                "vue",
                "app/components/Nav.vue",
                string.Empty,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "nuxt",
                    ["target_path"] = "/blog/hello",
                }),
            Fact(
                "sf-nuxt-blog-page",
                "nuxt.file_route.v1",
                "vue",
                "app/pages/blog/[slug].vue",
                string.Empty,
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "nuxt",
                    ["route_path"] = "/blog/[slug]",
                    ["normalized_route_template"] = "/blog/:slug",
                }),
        };

        var graph = BridgeGraphBuilder.Build([], [], [], [], [], structuralFacts: facts);

        var edge = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.NavigatesTo);
        Assert.Equal("/blog/hello", edge.Edge.SourceRef.Display);
        Assert.Equal("/blog/[slug]", edge.Edge.TargetRef.Display);
        Assert.Equal(ConfidenceBand.High, edge.Band);
    }

    [Fact]
    public void Build_DefaultProviders_BuildsVueNavigationFromRouteDefinitions()
    {
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-vue-user-link",
                "vue.route_reference.v1",
                "vue",
                "web/AppHeader.vue",
                "vue.header",
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "vue",
                    ["target_path"] = "/users/42",
                }),
            Fact(
                "sf-vue-user-route",
                "vue.route_definition.v1",
                "typescript",
                "web/router.ts",
                "vue.router",
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "vue",
                    ["route_path"] = "/users/:id",
                }),
        };

        var graph = BridgeGraphBuilder.Build([], [], [], [], [], structuralFacts: facts);

        var edge = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.NavigatesTo);
        Assert.Equal(ConfidenceBand.High, edge.Band);
        Assert.Equal("/users/42", edge.Edge.SourceRef.Display);
        Assert.Equal("/users/:id", edge.Edge.TargetRef.Display);
        Assert.Contains("vue", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["vue.routeReferences"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["vue.fileRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["vue.candidates"]);
    }

    [Fact]
    public void Build_DefaultProviders_BuildsReactNavigationFromRouteDefinitions()
    {
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-react-settings-link",
                "react.route_reference.v1",
                "tsx",
                "web/App.tsx",
                "react.link",
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "react",
                    ["target_path"] = "/settings",
                }),
            Fact(
                "sf-react-settings-route",
                "react.route_definition.v1",
                "tsx",
                "web/routes.tsx",
                "react.routes",
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "react",
                    ["route_path"] = "/settings",
                }),
        };

        var graph = BridgeGraphBuilder.Build([], [], [], [], [], structuralFacts: facts);

        var edge = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.NavigatesTo);
        Assert.Equal(ConfidenceBand.High, edge.Band);
        Assert.Equal("/settings", edge.Edge.SourceRef.Display);
        Assert.Equal("/settings", edge.Edge.TargetRef.Display);
        Assert.Contains("react", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["react.routeReferences"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["react.fileRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["react.candidates"]);
    }

    [Fact]
    public void FileRouteBridge_StaticReference_YieldsNavigatesToEdge()
    {
        var edges = FileRouteBridge.Resolve(
            [NextRouteReference("/settings")],
            [NextFileRoute("/settings", "web/app/settings/page.tsx")]).Edges;

        var edge = Assert.Single(edges);
        Assert.Equal(BridgeKind.NavigatesTo, edge.Kind);
        Assert.Equal("/settings", edge.SourceRef.Display);
        Assert.Equal("/settings", edge.TargetRef.Display);
        var signal = Assert.Single(edge.Signals);
        var structural = Assert.IsType<StructuralSignal>(signal);
        Assert.Equal(SignalRule.RouteReferenceMatch, structural.Rule);
        Assert.True(structural.Present);
    }

    [Theory]
    [InlineData("/users/[id]")]
    [InlineData("/users/{}")]
    [InlineData("/users/:id")]
    public void FileRouteBridge_DynamicReference_YieldsHighConfidenceEdge(string fileRoute)
    {
        var edge = Assert.Single(FileRouteBridge.Resolve(
            [NextRouteReference("/users/123")],
            [NextFileRoute(fileRoute, "web/app/users/[id]/page.tsx")]).Edges);

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.Equal(fileRoute, edge.TargetRef.Display);
    }

    [Fact]
    public void FileRouteBridge_CatchAllRequiresAtLeastOneTrailingSegment()
    {
        var edges = FileRouteBridge.Resolve(
            [
                NextRouteReference("/docs", "next.docs.index"),
                NextRouteReference("/docs/a/b", "next.docs.deep"),
            ],
            [NextFileRoute("/docs/[...slug]", "web/app/docs/[...slug]/page.tsx")]).Edges;

        var edge = Assert.Single(edges);
        Assert.Equal("/docs/a/b", edge.SourceRef.Display);
        Assert.Equal("/docs/[...slug]", edge.TargetRef.Display);
    }

    [Fact]
    public void FileRouteBridge_ColonCatchAllRequiresAtLeastOneTrailingSegment()
    {
        var edges = FileRouteBridge.Resolve(
            [
                NextRouteReference("/docs", "next.docs.index"),
                NextRouteReference("/docs/a/b", "next.docs.deep"),
            ],
            [NextFileRoute("/docs/:slug*", "web/app/docs/[...slug]/page.tsx")]).Edges;

        var edge = Assert.Single(edges);
        Assert.Equal("/docs/a/b", edge.SourceRef.Display);
        Assert.Equal("/docs/:slug*", edge.TargetRef.Display);
    }

    [Fact]
    public void FileRouteBridge_OptionalCatchAllMatchesZeroOrMoreTrailingSegments()
    {
        var edges = FileRouteBridge.Resolve(
            [
                NextRouteReference("/docs", "next.docs.index"),
                NextRouteReference("/docs/a/b", "next.docs.deep"),
            ],
            [NextFileRoute("/docs/[[...slug]]", "web/app/docs/[[...slug]]/page.tsx")]).Edges;

        Assert.Collection(
            edges.OrderBy(edge => edge.SourceRef.Display, StringComparer.Ordinal),
            edge =>
            {
                Assert.Equal("/docs", edge.SourceRef.Display);
                Assert.Equal("/docs/[[...slug]]", edge.TargetRef.Display);
            },
            edge =>
            {
                Assert.Equal("/docs/a/b", edge.SourceRef.Display);
                Assert.Equal("/docs/[[...slug]]", edge.TargetRef.Display);
            });
    }

    [Theory]
    [InlineData("/docs/:slug*?")]
    [InlineData("/docs/:slug?")]
    public void FileRouteBridge_ColonOptionalCatchAllMatchesZeroOrMoreTrailingSegments(string fileRoute)
    {
        var edges = FileRouteBridge.Resolve(
            [
                NextRouteReference("/docs", "next.docs.index"),
                NextRouteReference("/docs/a/b", "next.docs.deep"),
            ],
            [NextFileRoute(fileRoute, "web/app/docs/[[...slug]]/page.tsx")]).Edges;

        Assert.Collection(
            edges.OrderBy(edge => edge.SourceRef.Display, StringComparer.Ordinal),
            edge =>
            {
                Assert.Equal("/docs", edge.SourceRef.Display);
                Assert.Equal(fileRoute, edge.TargetRef.Display);
            },
            edge =>
            {
                Assert.Equal("/docs/a/b", edge.SourceRef.Display);
                Assert.Equal(fileRoute, edge.TargetRef.Display);
            });
    }

    [Fact]
    public void FileRouteBridge_OptionalCatchAllOnlyMatchesTrailingSegments()
    {
        var edges = FileRouteBridge.Resolve(
            [NextRouteReference("/docs/a/edit")],
            [NextFileRoute("/docs/[[...slug]]/edit", "web/app/docs/[[...slug]]/edit/page.tsx")]).Edges;

        Assert.Empty(edges);
    }

    [Fact]
    public void FileRouteMatcher_BraceOptionalMatchesZeroOrOneSegment()
    {
        Assert.True(FileRouteMatcher.Matches("/orders", "/orders/{orderId?}"));
        Assert.True(FileRouteMatcher.Matches("/orders/42", "/orders/{orderId?}"));
        Assert.False(FileRouteMatcher.Matches("/orders/a/b", "/orders/{orderId?}"));
    }

    [Fact]
    public void FileRouteMatcher_BraceCatchAllMatchesZeroOrMoreTrailingSegments()
    {
        Assert.True(FileRouteMatcher.Matches("/files/a/b/c", "/files/{*path}"));
        Assert.True(FileRouteMatcher.Matches("/files", "/files/{*path}"));
    }

    [Fact]
    public void FileRouteBridge_RouteGroupSegmentsDoNotParticipateInMatching()
    {
        var edge = Assert.Single(FileRouteBridge.Resolve(
            [NextRouteReference("/settings")],
            [NextFileRoute("/(admin)/settings", "web/app/(admin)/settings/page.tsx")]).Edges);

        Assert.Equal("/settings", edge.SourceRef.Display);
        Assert.Equal("/(admin)/settings", edge.TargetRef.Display);
    }

    [Fact]
    public void FileRouteBridge_PrefersDynamicSegmentOverCatchAll()
    {
        var result = FileRouteBridge.Resolve(
            [NextRouteReference("/users/42")],
            [
                NextFileRoute("/users/[...slug]", "web/app/users/[...slug]/page.tsx", "sf-next-users-catchall"),
                NextFileRoute("/users/[id]", "web/app/users/[id]/page.tsx", "sf-next-users-id"),
            ]);

        var edge = Assert.Single(result.Edges);
        Assert.Equal("/users/[id]", edge.TargetRef.Display);
        Assert.Equal(0, result.AmbiguousMatches);
    }

    [Fact]
    public void FileRouteBridge_AmbiguousFileRouteMatchesEmitNoEdge()
    {
        var result = FileRouteBridge.Resolve(
            [NextRouteReference("/settings")],
            [
                NextFileRoute("/settings", "web/app/settings/page.tsx", "sf-next-settings-app"),
                NextFileRoute("/(admin)/settings", "web/app/(admin)/settings/page.tsx", "sf-next-settings-admin"),
            ]);

        Assert.Empty(result.Edges);
        Assert.Equal(1, result.AmbiguousMatches);
    }

    [Fact]
    public void StructuralFacts_VueRouteReference_YieldsHitsEdgeToSyntheticLambdaEndpoint()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("vue.header", "AppHeader", "component", file: "web/AppHeader.vue"),
        };
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-vue-keepalive",
                "vue.route_reference.v1",
                "vue",
                "web/AppHeader.vue",
                "vue.header",
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "vue",
                    ["target_path"] = "/keep-alive.html",
                }),
            Fact(
                "sf-route-keepalive",
                "aspnet.minimal_api.route.v1",
                "csharp",
                "Api/KeepAlive.cs",
                "cs.map",
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "aspnet",
                    ["route_template"] = "/keep-alive.html",
                    ["verb"] = "GET",
                    ["handler_kind"] = "lambda",
                }),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], [], structuralFacts: facts);

        var hits = Assert.Single(graph.Incident("vue.header"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Null(hits.Edge.TargetRef.SymbolId);
        var endpointNodeId = BridgeGraph.NodeIdOf(hits.Edge.TargetRef, BridgeKind.Hits, EndpointSide.Target);
        Assert.NotNull(endpointNodeId);
        var endpointNode = graph.Node(endpointNodeId);
        Assert.NotNull(endpointNode);
        Assert.Equal(BridgeNodeKind.Endpoint, endpointNode.Kind);
        Assert.Equal("GET /keep-alive.html", endpointNode.Display);
        Assert.Equal("Api/KeepAlive.cs", endpointNode.FilePath);
        Assert.Equal(1, endpointNode.Line);
        Assert.Equal(ConfidenceBand.Medium, hits.Band);
        Assert.True(hits.IsVerbUnknown);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.structuralEndpoints"]);
    }

    private static StructuralRouteReference NextRouteReference(
        string routePath,
        string containingSymbolId = "next.link",
        string id = "sf-next-reference",
        string filePath = "web/Nav.tsx") =>
        new(
            Fact(
                id,
                "nextjs.route_reference.v1",
                "tsx",
                filePath,
                containingSymbolId,
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["target_path"] = routePath,
                }),
            routePath,
            "GET",
            containingSymbolId,
            filePath,
            Line: 1);

    private static StructuralFileRoute NextFileRoute(
        string routePath,
        string filePath,
        string id = "sf-next-file-route") =>
        new(
            Fact(
                id,
                "nextjs.file_route.v1",
                "tsx",
                filePath,
                containingSymbolId: string.Empty,
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "nextjs",
                    ["route_path"] = routePath,
                }),
            routePath,
            "GET",
            ContainingSymbolId: string.Empty,
            filePath,
            Line: 1);

    [Fact]
    public void StructuralFacts_UnmatchedFrontendAndBackendRoutes_AreRetainedAsObservationNodes()
    {
        var symbols = new List<SymbolDetail>
        {
            Type("vue.header", "AppHeader", "component", file: "web/AppHeader.vue"),
        };
        var facts = new List<StructuralFactRecord>
        {
            Fact(
                "sf-vue-calendar",
                "vue.route_reference.v1",
                "vue",
                "web/AppHeader.vue",
                "vue.header",
                100,
                new Dictionary<string, string>
                {
                    ["framework"] = "vue",
                    ["target_path"] = "/calendar",
                    ["verb"] = "GET",
                }),
            Fact(
                "sf-route-keepalive",
                "aspnet.minimal_api.route.v1",
                "csharp",
                "Api/KeepAlive.cs",
                "cs.map",
                200,
                new Dictionary<string, string>
                {
                    ["framework"] = "aspnet",
                    ["route_template"] = "/keep-alive.html",
                    ["verb"] = "GET",
                    ["handler_kind"] = "lambda",
                }),
        };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], [], structuralFacts: facts);

        Assert.Empty(graph.Edges);
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.TsType &&
            node.Display == "/calendar" &&
            node.FilePath == "web/AppHeader.vue" &&
            node.Line == 1);
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.Endpoint &&
            node.Display == "GET /keep-alive.html" &&
            node.FilePath == "Api/KeepAlive.cs" &&
            node.Line == 1);
    }

    // ---- 2.6.0 HTTP boundary facts: http.client_request.v1 + aspnet.attribute_route.v1 (Tasks 2–3) ----

    [Fact]
    public void StructuralFacts_AttestedPostFetch_HitsAttributeRoutePostEndpoint_High()
    {
        // A bare [HttpPost] (no route_template) inherits the controller's effective template.
        var create = Method("sym-create", "Create", "Task<IResult> Create(CreateMessageRequest request)",
            "MessagesController", "api/MessagesController.cs");
        var tsFn = Type("sym-tsfn", "createMessage", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httppost",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/MessagesController.cs",
                containingSymbolId: "sym-create",
                startLine: 14,
                metadataJson: """{"attribute_kind":"http_method","verb":"POST","controller_route_template":"api/[controller]","effective_route_template":"/api/messages","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-fetch-post",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [create, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-create"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-tsfn", hit.Edge.SourceRef.SymbolId);
        Assert.Contains(hit.Edge.Evidence, e => e.FilePath == "web/api.ts" && e.Line == 8);
        Assert.Contains(hit.Edge.Evidence, e => e.FilePath == "api/MessagesController.cs" && e.Line == 14);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientRequests"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.attributeRoutes"]);
    }

    [Fact]
    public void StructuralFacts_DefaultVerbFetch_IsVerbKnownGet_AndDoesNotMatchPostOnlyEndpoint()
    {
        var list = Method("sym-list", "List", "Task<IResult> List()", "MessagesController", "api/MessagesController.cs");
        var create = Method("sym-create", "Create", "Task<IResult> Create(CreateMessageRequest request)",
            "MessagesController", "api/MessagesController.cs");
        var tsFn = Type("sym-tsfn", "loadMessages", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/MessagesController.cs",
                containingSymbolId: "sym-list",
                startLine: 10,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/messages","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-httppost",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/MessagesController.cs",
                containingSymbolId: "sym-create",
                startLine: 20,
                metadataJson: """{"attribute_kind":"http_method","verb":"POST","controller_route_template":"api/[controller]","effective_route_template":"/api/messages","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-bare-fetch",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 5,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [list, create, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        // verb_source=default is verb-known GET by fetch spec: High against the GET action, NO edge to the POST one.
        var hit = Assert.Single(graph.Incident("sym-list"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.DoesNotContain(graph.Incident("sym-create"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Theory]
    [InlineData("absolute", "https://api.example.com/api/messages")]
    [InlineData("relative", "api/messages")]
    public void StructuralFacts_NonPathClientRequests_AreNotBridgeCandidates(string urlKind, string targetPath)
    {
        var list = Method("sym-list", "List", "Task<IResult> List()", "MessagesController", "api/MessagesController.cs");
        var tsFn = Type("sym-tsfn", "loadMessages", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/MessagesController.cs",
                containingSymbolId: "sym-list",
                startLine: 10,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/messages","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-non-path",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 5,
                metadataJson: $$"""{"client":"fetch","framework":"fetch","target_path":"{{targetPath}}","url_kind":"{{urlKind}}","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [list, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-list"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientRequests"]);
    }

    [Fact]
    public void StructuralFacts_ClientRequestFromTestPath_IsIgnored()
    {
        var list = Method("sym-list", "List", "Task<IResult> List()", "MessagesController", "api/MessagesController.cs");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/MessagesController.cs",
                containingSymbolId: "sym-list",
                startLine: 10,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/messages","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-test-fetch",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/__tests__/api.test.ts",
                containingSymbolId: string.Empty,
                startLine: 5,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [list], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-list"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientRequests"]);
    }

    [Fact]
    public void StructuralFacts_CsharpHttpClientRequest_HitsAttributeRouteEndpoint_High()
    {
        // C# service-to-service (Task 5): a NON-test HttpClient.GetAsync($"/api/users/{id}") reduces to an
        // http.client_request.v1 fact carrying an attested verb (AttestedVerb != null). The narrowed csharp
        // exclusion now admits it as a real client call, and it folds onto the attribute-route GET
        // /api/users/{id} endpoint — a verb-known High Hits edge through dotnet-web. (RouteNormalizer folds the
        // {id} placeholder to {}; a bare numeric literal like /api/users/42 does NOT fold, so the interpolated
        // parameterized path is the shape that actually matches the parameterized endpoint.)
        var getById = Method("sym-get", "GetById", "Task<IResult> GetById(int id)",
            "UsersController", "api/UsersController.cs");
        var client = Method("sym-client", "FetchUser", "Task<User> FetchUser(int id)",
            "UserApiClient", "src/UserApiClient.cs");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget-id",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/UsersController.cs",
                containingSymbolId: "sym-get",
                startLine: 18,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","route_template":"{id}","controller_route_template":"api/[controller]","effective_route_template":"/api/users/{id}","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-httpclient-get",
                patternId: "http.client_request.v1",
                language: "csharp",
                path: "src/UserApiClient.cs",
                containingSymbolId: "sym-client",
                startLine: 12,
                metadataJson: """{"client":"HttpClient","framework":"httpclient","target_path":"/api/users/{id}","url_kind":"path","verb":"GET","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [getById, client], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        // Client /api/users/{id} and endpoint /api/users/{id} both canonicalize to api/users/{} — verb-known GET High.
        var hit = Assert.Single(graph.Incident("sym-get"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-client", hit.Edge.SourceRef.SymbolId);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientRequests"]);
    }

    [Fact]
    public void StructuralFacts_CsharpHttpClientRequestFromTestSymbol_IsExcluded()
    {
        // Test-noise safety (Task 5): a test-project HttpClient call — its containing symbol is julie-flagged
        // is_test — is rejected at TryReadClientRequest (IsTestFact container-flag branch), so it never becomes
        // a structural client call, never gains an AttestedVerb, and produces no edge. This is exactly the
        // test-HttpClient noise the csharp exclusion was built to block; narrowing to AttestedVerb-null keeps it out.
        var getById = Method("sym-get", "GetById", "Task<IResult> GetById(int id)",
            "UsersController", "api/UsersController.cs");
        var testMethod = new SymbolDetail("sym-test", "GetUser_ReturnsOk", "method", "tests/UsersApiTests.cs",
            "Task GetUser_ReturnsOk()", "Api.Tests", IsTest: true, ParentClassName: "UsersApiTests");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget-id",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/UsersController.cs",
                containingSymbolId: "sym-get",
                startLine: 18,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","route_template":"{id}","controller_route_template":"api/[controller]","effective_route_template":"/api/users/{id}","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-test-httpclient",
                patternId: "http.client_request.v1",
                language: "csharp",
                path: "tests/UsersApiTests.cs",
                containingSymbolId: "sym-test",
                startLine: 20,
                metadataJson: """{"client":"HttpClient","framework":"httpclient","target_path":"/api/users/{id}","url_kind":"path","verb":"GET","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [getById, testMethod], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-get"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientRequests"]);
    }

    [Fact]
    public void StructuralFacts_LegacyCsharpUrlLiteral_StaysExcludedEvenWithMatchingEndpoint()
    {
        // Legacy exclusion preserved (Task 5): a raw csharp url literal carries NO attested verb
        // (AttestedVerb == null). The narrowed rule still drops it — only structural-fact-derived csharp calls
        // become real client calls. Paired here with the SAME endpoint the structural call in the positive test
        // hits, proving the csharp+AttestedVerb-null exclusion (not a route miss) is what suppresses the edge.
        var getById = Method("sym-get", "GetById", "Task<IResult> GetById(int id)",
            "UsersController", "api/UsersController.cs");
        var client = new SymbolDetail("sym-client", "FetchUser", "method", "src/UserApiClient.cs",
            "Task FetchUser()", "Api.Clients", IsTest: false, ParentClassName: "UserApiClient");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget-id",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/UsersController.cs",
                containingSymbolId: "sym-get",
                startLine: 18,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","route_template":"{id}","controller_route_template":"api/[controller]","effective_route_template":"/api/users/{id}","route_tokens":["controller"]}"""),
        };
        var literals = new List<LiteralRecord>
        {
            // Verb-known (GetAsync -> GET) and route api/users/{} — this WOULD match the endpoint if admitted;
            // it is dropped only because it is a legacy csharp literal with no attested verb.
            MakeLiteral("/api/users/{id}", kind: "url", language: "csharp", carrier: "GetAsync",
                containingSymbolId: "sym-client", spanStart: 0),
        };

        var graph = BridgeGraphBuilder.Build(
            [getById, client], typeArguments: [], literals: literals, annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-get"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void StructuralFacts_AttributeRouteEffectiveTemplate_MatchesCanonicalClientCall()
    {
        var getById = Method("sym-get", "GetById", "Task<IResult> GetById(int id)",
            "UsersController", "api/UsersController.cs");
        var tsFn = Type("sym-tsfn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget-id",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/UsersController.cs",
                containingSymbolId: "sym-get",
                startLine: 18,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","route_template":"{id}","controller_route_template":"api/[controller]","effective_route_template":"/api/users/{id}","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-axios-get",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 6,
                metadataJson: """{"client":"axios","framework":"axios","import_source":"axios","target_path":"/api/users/${userId}","url_kind":"path","verb":"GET","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [getById, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        // /api/users/${userId} and /api/users/{id} both canonicalize to api/users/{} — verb-known High.
        var hit = Assert.Single(graph.Incident("sym-get"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
    }

    [Fact]
    public void StructuralFacts_ControllerRouteFact_ProducesNoEndpoint()
    {
        var controller = Type("sym-users-controller", "UsersController", "class", "Api.Controllers", "api/UsersController.cs");
        var tsFn = Type("sym-tsfn", "loadUsers", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-controller-route",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/UsersController.cs",
                containingSymbolId: "sym-users-controller",
                startLine: 8,
                metadataJson: """{"attribute_kind":"controller_route","route_template":"api/[controller]","route_tokens":["controller"],"effective_route_template":"/api/users"}"""),
            StructuralFact(
                factId: "fact-fetch-users",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 5,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/users","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [controller, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        // A class-level prefix fact is never an endpoint: no backend evidence, provider inactive, no edges.
        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["dotnet-web.attributeRoutes"]);
        Assert.DoesNotContain("dotnet-web", graph.CapabilityReport.ActiveProviders);
    }

    [Fact]
    public void StructuralFacts_MethodRouteFactWithoutVerb_IsCountedInEvidenceButNotAnEndpoint()
    {
        // RouteBridge.TryBuildHitsEdge yields NO edge for a verb-known client against a verb-unknown endpoint
        // (verified 2026-07-01), so method [Route] facts are evidence-only until that arm learns an honest Medium.
        var legacy = Method("sym-legacy", "Legacy", "Task<IResult> Legacy()", "UsersController", "api/UsersController.cs");
        var tsFn = Type("sym-tsfn", "loadLegacy", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-method-route",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/UsersController.cs",
                containingSymbolId: "sym-legacy",
                startLine: 30,
                metadataJson: """{"attribute_kind":"route","route_template":"legacy","controller_route_template":"api/[controller]","effective_route_template":"/api/users/legacy","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-fetch-legacy",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 5,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/users/legacy","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [legacy, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.attributeRoutes"]);
        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.DoesNotContain("dotnet-web", graph.CapabilityReport.ActiveProviders);
    }

    [Fact]
    public void StructuralFacts_AnnotationAndStructuralAttributeEndpoint_YieldOneEndpointAndOneEdge()
    {
        var classSym = Type("sym-class", "UsersController", "class", "Api.Controllers", "api/UsersController.cs");
        var getById = Method("sym-get", "GetById", "Task<IResult> GetById(int id)", "UsersController", "api/UsersController.cs");
        var tsFn = Type("sym-tsfn", "loadUser", "function", file: "web/api.ts");

        var annotations = new List<SymbolAnnotation>
        {
            new(SymbolId: "sym-class", Ordinal: 0, Annotation: "Route", AnnotationKey: "route",
                RawText: "Route(\"api/[controller]\")", Carrier: "Route"),
            new(SymbolId: "sym-get", Ordinal: 0, Annotation: "HttpGet", AnnotationKey: "httpget",
                RawText: "HttpGet(\"{id}\")", Carrier: "HttpGet"),
        };

        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget-id",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/UsersController.cs",
                containingSymbolId: "sym-get",
                startLine: 18,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","route_template":"{id}","controller_route_template":"api/[controller]","effective_route_template":"/api/users/{id}","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-fetch-user",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 6,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/users/${id}","url_kind":"path","verb":"GET","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [classSym, getById, tsFn], typeArguments: [], literals: [], annotations: annotations,
            dbSetProperties: [], structuralFacts: facts);

        // The annotation-derived endpoint and the structural attribute-route fact describe ONE endpoint
        // (same method symbol + verb): structural wins, no duplicate endpoint, one Hits edge.
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.endpoints"]);
        var hit = Assert.Single(graph.Incident("sym-get"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
    }

    [Fact]
    public void StructuralFacts_LiteralAndStructuralClientRequest_CollapseToTheHigherBandEdge()
    {
        var list = Method("sym-list", "List", "Task<IResult> List()", "UsersController", "api/UsersController.cs");
        var tsFn = Type("sym-tsfn", "loadUsers", "function", file: "web/api.ts");

        var annotations = new List<SymbolAnnotation>
        {
            new(SymbolId: "sym-list", Ordinal: 0, Annotation: "HttpGet", AnnotationKey: "httpget",
                RawText: "HttpGet(\"/api/users\")", Carrier: "HttpGet"),
        };

        // The SAME call site as a legacy url literal (carrier fetch = verb-unknown Medium) and as a 2.6.0
        // structural client request (fetch spec-default GET = verb-known High).
        var literal = MakeLiteral("/api/users", kind: "url", language: "typescript",
            carrier: "fetch", containingSymbolId: "sym-tsfn", spanStart: 80);
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-fetch-users",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/users","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [list, tsFn], typeArguments: [], literals: [literal], annotations: annotations,
            dbSetProperties: [], structuralFacts: facts);

        // One edge per (client, endpoint) pair — the covered literal is suppressed pre-bridge (per-site
        // DedupeClientCalls), so ONLY the verb-known High structural edge exists. Before suppression this
        // held only via graph dedupe, which cannot protect a different-target false edge.
        var hit = Assert.Single(graph.Incident("sym-list"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientCalls"]);
    }

    [Fact]
    public void StructuralFacts_ClientRequestsAlone_LeaveDotnetWebInactive()
    {
        var tsFn = Type("sym-tsfn", "loadMessages", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-bare-fetch",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 5,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        // The active gate stays backend-evidence-based: a pure-frontend repo must not activate dotnet-web.
        Assert.DoesNotContain("dotnet-web", graph.CapabilityReport.ActiveProviders);
        Assert.Contains(graph.CapabilityReport.SkippedProviders, skipped =>
            skipped.ProviderId == "dotnet-web" &&
            skipped.Reason.Contains("no dotnet-web backend evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientRequests"]);
        Assert.Empty(graph.Edges);
    }

    // ---- Task 4: nextjs-api / nuxt-api client→handler providers (http.client_request.v1 → route handlers) ----

    [Fact]
    public void ApiBridge_FetchGet_HitsNextRouteHandlerSymbol_High()
    {
        var handler = Type("sym-handler", "GET", "function", file: "web/app/api/messages/route.ts");
        var tsFn = Type("sym-tsfn", "loadMessages", "function", file: "web/lib/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-next-get",
                patternId: "nextjs.route_handler.v1",
                language: "typescript",
                path: "web/app/api/messages/route.ts",
                containingSymbolId: "sym-handler",
                startLine: 3,
                metadataJson: """{"framework":"nextjs","router":"app","route_path":"/api/messages","verb":"GET","verb_source":"attested"}"""),
            StructuralFact(
                factId: "fact-fetch-get",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/lib/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"GET","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-handler"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-tsfn", hit.Edge.SourceRef.SymbolId);
        Assert.Equal("sym-handler", hit.Edge.TargetRef.SymbolId);
        Assert.Contains(hit.Edge.Evidence, e => e.FilePath == "web/lib/api.ts" && e.Line == 8);
        Assert.Contains(hit.Edge.Evidence, e => e.FilePath == "web/app/api/messages/route.ts" && e.Line == 3);
        Assert.Contains("nextjs-api", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs-api.clientRequests"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs-api.routeHandlers"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs-api.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs-api.ambiguousMatches"]);
        // The same client-request fact feeds dotnet-web, but with zero backend evidence it stays inactive.
        Assert.DoesNotContain("dotnet-web", graph.CapabilityReport.ActiveProviders);
    }

    [Fact]
    public void ApiBridge_DynamicSegmentFetch_MatchesBracketRouteHandler_High()
    {
        var handler = Type("sym-handler", "GET", "function", file: "web/app/api/users/[id]/route.ts");
        var tsFn = Type("sym-tsfn", "loadUser", "function", file: "web/lib/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-next-get-id",
                patternId: "nextjs.route_handler.v1",
                language: "typescript",
                path: "web/app/api/users/[id]/route.ts",
                containingSymbolId: "sym-handler",
                startLine: 3,
                metadataJson: """{"framework":"nextjs","router":"app","route_path":"/api/users/[id]","normalized_route_template":"/api/users/:id","verb":"GET","verb_source":"attested"}"""),
            StructuralFact(
                factId: "fact-fetch-42",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/lib/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 12,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/users/42","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        // fetch("/api/users/42") segment-matches route_path=/api/users/[id]; verb-known GET (spec default) → High.
        var hit = Assert.Single(graph.Incident("sym-handler"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-handler", hit.Edge.TargetRef.SymbolId);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs-api.candidates"]);
    }

    [Fact]
    public void ApiBridge_PostClient_DoesNotHitGetOnlyHandler()
    {
        var handler = Type("sym-handler", "GET", "function", file: "web/app/api/messages/route.ts");
        var tsFn = Type("sym-tsfn", "createMessage", "function", file: "web/lib/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-next-get",
                patternId: "nextjs.route_handler.v1",
                language: "typescript",
                path: "web/app/api/messages/route.ts",
                containingSymbolId: "sym-handler",
                startLine: 3,
                metadataJson: """{"framework":"nextjs","router":"app","route_path":"/api/messages","verb":"GET","verb_source":"attested"}"""),
            StructuralFact(
                factId: "fact-fetch-post",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/lib/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        // A real verb distinction, not a route-only fallback: verb-known POST never hits a GET-only handler.
        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Contains("nextjs-api", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs-api.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs-api.ambiguousMatches"]);
    }

    [Fact]
    public void ApiBridge_EquallySpecificHandlers_CountAmbiguous_NoEdge()
    {
        var handlerId = Type("sym-handler-id", "GET", "function", file: "web/app/api/users/[id]/route.ts");
        var handlerSlug = Type("sym-handler-slug", "GET", "function", file: "web/app/api/users/[slug]/route.ts");
        var tsFn = Type("sym-tsfn", "loadUser", "function", file: "web/lib/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-next-get-id",
                patternId: "nextjs.route_handler.v1",
                language: "typescript",
                path: "web/app/api/users/[id]/route.ts",
                containingSymbolId: "sym-handler-id",
                startLine: 3,
                metadataJson: """{"framework":"nextjs","router":"app","route_path":"/api/users/[id]","verb":"GET","verb_source":"attested"}"""),
            StructuralFact(
                factId: "fact-next-get-slug",
                patternId: "nextjs.route_handler.v1",
                language: "typescript",
                path: "web/app/api/users/[slug]/route.ts",
                containingSymbolId: "sym-handler-slug",
                startLine: 3,
                metadataJson: """{"framework":"nextjs","router":"app","route_path":"/api/users/[slug]","verb":"GET","verb_source":"attested"}"""),
            StructuralFact(
                factId: "fact-fetch-42",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/lib/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 12,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/users/42","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [handlerId, handlerSlug, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs-api.ambiguousMatches"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs-api.candidates"]);
    }

    [Fact]
    public void ApiBridge_GetClient_SuffixlessNuxtServerRoute_MediumVerbUnknown()
    {
        var tsFn = Type("sym-tsfn", "loadNotes", "function", file: "app/composables/notes.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-nuxt-notes",
                patternId: "nuxt.server_route.v1",
                language: "typescript",
                path: "server/api/notes.ts",
                containingSymbolId: string.Empty,
                startLine: 1,
                metadataJson: """{"framework":"nuxt","route_path":"/api/notes"}"""),
            StructuralFact(
                factId: "fact-fetch-notes",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "app/composables/notes.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 6,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/notes","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        // The suffix-less server route answers every method, but its accepted verb set is not source-attested:
        // the edge stays honest-Medium with the verb_unknown flag — never assumed GET.
        var hit = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.Medium, hit.Band);
        Assert.True(hit.IsVerbUnknown);
        Assert.Equal("sym-tsfn", hit.Edge.SourceRef.SymbolId);
        Assert.Null(hit.Edge.TargetRef.SymbolId);
        Assert.Contains("nuxt-api", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nuxt-api.clientRequests"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nuxt-api.serverRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nuxt-api.candidates"]);
        // The handler has no containing symbol: the target is a synthesized Endpoint node (route-only display).
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.Endpoint &&
            node.Display == "/api/notes" &&
            node.FilePath == "server/api/notes.ts");
    }

    [Fact]
    public void ApiBridge_UnmatchedRequestsAndHandlers_BecomeObservationNodes_BothProvidersActive()
    {
        var handler = Type("sym-handler", "GET", "function", file: "web/app/api/messages/route.ts");
        var tsFn = Type("sym-tsfn", "loadUnknown", "function", file: "web/lib/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-next-get",
                patternId: "nextjs.route_handler.v1",
                language: "typescript",
                path: "web/app/api/messages/route.ts",
                containingSymbolId: "sym-handler",
                startLine: 3,
                metadataJson: """{"framework":"nextjs","router":"app","route_path":"/api/messages","verb":"GET","verb_source":"attested"}"""),
            StructuralFact(
                factId: "fact-nuxt-notes",
                patternId: "nuxt.server_route.v1",
                language: "typescript",
                path: "server/api/notes.ts",
                containingSymbolId: string.Empty,
                startLine: 1,
                metadataJson: """{"framework":"nuxt","route_path":"/api/notes"}"""),
            StructuralFact(
                factId: "fact-fetch-unknown",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/lib/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 5,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/unknown","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Contains("nextjs-api", graph.CapabilityReport.ActiveProviders);
        Assert.Contains("nuxt-api", graph.CapabilityReport.ActiveProviders);
        // Unmatched client request → canonical-route TsType node; unmatched handlers → Endpoint nodes
        // (verb-known handlers render the dotnet-web "VERB /route" shape; verb-less ones the route alone).
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.TsType &&
            node.Display == "/api/unknown" &&
            node.FilePath == "web/lib/api.ts");
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.Endpoint &&
            node.Display == "GET /api/messages" &&
            node.FilePath == "web/app/api/messages/route.ts");
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.Endpoint &&
            node.Display == "/api/notes" &&
            node.FilePath == "server/api/notes.ts");
    }

    // ---- adversarial-review fixes: F4 legacy-literal suppression, F1 attested-verb carry, F2 verb-exact
    // ---- priority, F3 cross-provider source-node identity ------------------------------------------------

    [Fact]
    public void StructuralFacts_CoveredUrlLiteral_DoesNotFabricateRouteOnlyEdgeToOtherVerbEndpoint()
    {
        // F4: fetch("/api/orders", {method:"POST"}) is observed BOTH as a legacy url literal (carrier fetch,
        // verb-unknown) and as an http.client_request.v1 POST fact at the SAME call site. GET and POST
        // endpoints share the route: the structural leg correctly refuses the GET endpoint, and the covered
        // literal must not resurrect it as a Medium route-only edge (a different target is a different edge
        // signature, so graph dedupe cannot collapse the false edge away).
        var list = Method("sym-list-orders", "ListOrders", "Task<IResult> ListOrders()",
            "OrdersController", "api/OrdersController.cs");
        var create = Method("sym-create-order", "CreateOrder", "Task<IResult> CreateOrder(CreateOrderRequest request)",
            "OrdersController", "api/OrdersController.cs");
        var tsFn = Type("sym-tsfn", "createOrder", "function", file: "web/api.ts");

        var literal = MakeLiteral("/api/orders", kind: "url", language: "typescript",
            carrier: "fetch", containingSymbolId: "sym-tsfn", spanStart: 80);
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/OrdersController.cs",
                containingSymbolId: "sym-list-orders",
                startLine: 10,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/orders","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-httppost",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/OrdersController.cs",
                containingSymbolId: "sym-create-order",
                startLine: 20,
                metadataJson: """{"attribute_kind":"http_method","verb":"POST","controller_route_template":"api/[controller]","effective_route_template":"/api/orders","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-fetch-post",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/orders","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [list, create, tsFn], typeArguments: [], literals: [literal], annotations: [],
            dbSetProperties: [], structuralFacts: facts);

        // ONE Hits edge in the whole graph: the correct High edge to the POST action. No false Medium
        // route-only edge to the GET action.
        var hit = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-create-order", hit.Edge.TargetRef.SymbolId);
        Assert.DoesNotContain(graph.Incident("sym-list-orders"), e => e.Edge.Kind == BridgeKind.Hits);
        // The covered literal is suppressed pre-bridge: only the structural call remains.
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientCalls"]);
    }

    [Fact]
    public void StructuralFacts_CoveredBareFetchLiteral_DoesNotFabricateEdgeToPostOnlyEndpoint()
    {
        // F4 symmetric arm: a bare fetch("/api/orders") emits a verb-unknown literal plus a verb-known GET
        // (spec default) structural request. Against a POST-only endpoint the structural leg yields no edge —
        // and the covered literal must not fabricate a Medium route-only edge either.
        var create = Method("sym-create-order", "CreateOrder", "Task<IResult> CreateOrder(CreateOrderRequest request)",
            "OrdersController", "api/OrdersController.cs");
        var tsFn = Type("sym-tsfn", "loadOrders", "function", file: "web/api.ts");

        var literal = MakeLiteral("/api/orders", kind: "url", language: "typescript",
            carrier: "fetch", containingSymbolId: "sym-tsfn", spanStart: 80);
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httppost",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/OrdersController.cs",
                containingSymbolId: "sym-create-order",
                startLine: 20,
                metadataJson: """{"attribute_kind":"http_method","verb":"POST","controller_route_template":"api/[controller]","effective_route_template":"/api/orders","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-bare-fetch",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/orders","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [create, tsFn], typeArguments: [], literals: [literal], annotations: [],
            dbSetProperties: [], structuralFacts: facts);

        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientCalls"]);
    }

    [Fact]
    public void StructuralFacts_UncoveredUrlLiteral_SurvivesClientRequestSuppression()
    {
        // F4 survival guard: suppression is not global across different keys. A ky wrapper literal with no
        // covering structural request must keep its honest Medium route-only edge even while a covered fetch
        // literal elsewhere is suppressed.
        var legacy = Method("sym-legacy-get", "LegacyGet", "Task<IResult> LegacyGet()",
            "LegacyController", "api/LegacyController.cs");
        var create = Method("sym-create-order", "CreateOrder", "Task<IResult> CreateOrder(CreateOrderRequest request)",
            "OrdersController", "api/OrdersController.cs");
        var tsFn = Type("sym-tsfn", "createOrder", "function", file: "web/api.ts");
        var other = Type("sym-other", "loadLegacy", "function", file: "web/legacy.ts");

        var coveredLiteral = MakeLiteral("/api/orders", kind: "url", language: "typescript",
            carrier: "fetch", containingSymbolId: "sym-tsfn", spanStart: 80);
        var uncoveredLiteral = MakeLiteral("/api/legacy", kind: "url", language: "typescript",
            carrier: "ky", containingSymbolId: "sym-other", spanStart: 120);
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget-legacy",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/LegacyController.cs",
                containingSymbolId: "sym-legacy-get",
                startLine: 10,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/legacy","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-httppost",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/OrdersController.cs",
                containingSymbolId: "sym-create-order",
                startLine: 20,
                metadataJson: """{"attribute_kind":"http_method","verb":"POST","controller_route_template":"api/[controller]","effective_route_template":"/api/orders","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-fetch-post",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/orders","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [legacy, create, tsFn, other], typeArguments: [], literals: [coveredLiteral, uncoveredLiteral],
            annotations: [], dbSetProperties: [], structuralFacts: facts);

        // The uncovered ky literal keeps its verb-unknown Medium edge; the covered fetch site stays High.
        var legacyHit = Assert.Single(graph.Incident("sym-legacy-get"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.Medium, legacyHit.Band);
        Assert.True(legacyHit.IsVerbUnknown);
        Assert.Equal("sym-other", legacyHit.Edge.SourceRef.SymbolId);
        var orderHit = Assert.Single(graph.Incident("sym-create-order"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, orderHit.Band);
        // 1 surviving literal + 1 structural request.
        Assert.Equal(2, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientCalls"]);
    }

    [Fact]
    public void StructuralFacts_SameFunctionSameRouteWrapperLiteral_IsSuppressedByCoarseClientRequestKey()
    {
        // F4 intentionally keys suppression by (containing symbol, canonical route), not by call-site span.
        // A wrapper literal in the same function and route as a structural fetch fact is suppressed; wrapper
        // survival remains covered by the different-symbol/different-route test above.
        var create = Method("sym-create-order", "CreateOrder", "Task<IResult> CreateOrder(CreateOrderRequest request)",
            "OrdersController", "api/OrdersController.cs");
        var tsFn = Type("sym-tsfn", "createOrder", "function", file: "web/api.ts");

        var coveredFetchLiteral = MakeLiteral("/api/orders", kind: "url", language: "typescript",
            carrier: "fetch", containingSymbolId: "sym-tsfn", spanStart: 80);
        var sameFunctionWrapperLiteral = MakeLiteral("/api/orders", kind: "url", language: "typescript",
            carrier: "ky", containingSymbolId: "sym-tsfn", spanStart: 140);
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httppost",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/OrdersController.cs",
                containingSymbolId: "sym-create-order",
                startLine: 20,
                metadataJson: """{"attribute_kind":"http_method","verb":"POST","controller_route_template":"api/[controller]","effective_route_template":"/api/orders","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-fetch-post",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/orders","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [create, tsFn], typeArguments: [], literals: [coveredFetchLiteral, sameFunctionWrapperLiteral],
            annotations: [], dbSetProperties: [], structuralFacts: facts);

        var orderHit = Assert.Single(graph.Incident("sym-create-order"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, orderHit.Band);
        Assert.False(orderHit.IsVerbUnknown);
        Assert.Equal("sym-tsfn", orderHit.Edge.SourceRef.SymbolId);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientCalls"]);
    }

    [Fact]
    public void StructuralFacts_SymbollessCoveredUrlLiteral_SuppressedByFilePathFallback()
    {
        // F4 fallback leg: a module-scope call site has no containing symbol on either evidence form — the
        // suppression falls back to same file path + same canonical route.
        var list = Method("sym-list-orders", "ListOrders", "Task<IResult> ListOrders()",
            "OrdersController", "api/OrdersController.cs");
        var create = Method("sym-create-order", "CreateOrder", "Task<IResult> CreateOrder(CreateOrderRequest request)",
            "OrdersController", "api/OrdersController.cs");

        var literal = MakeLiteral("/api/orders", kind: "url", language: "typescript",
            carrier: "fetch", containingSymbolId: string.Empty, spanStart: 80);
        var sites = new Dictionary<LiteralRecord, LiteralSite> { [literal] = new("web/boot.ts", 3) };
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/OrdersController.cs",
                containingSymbolId: "sym-list-orders",
                startLine: 10,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/orders","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-httppost",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/OrdersController.cs",
                containingSymbolId: "sym-create-order",
                startLine: 20,
                metadataJson: """{"attribute_kind":"http_method","verb":"POST","controller_route_template":"api/[controller]","effective_route_template":"/api/orders","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-fetch-post",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/boot.ts",
                containingSymbolId: string.Empty,
                startLine: 3,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/orders","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [list, create], typeArguments: [], literals: [literal], annotations: [],
            dbSetProperties: [], literalSites: sites, structuralFacts: facts);

        var hit = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal("sym-create-order", hit.Edge.TargetRef.SymbolId);
        Assert.DoesNotContain(graph.Incident("sym-list-orders"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientCalls"]);
    }

    [Fact]
    public void StructuralFacts_AttestedNonWhitelistVerb_YieldsNoEdgeToDifferentVerbEndpoint()
    {
        // F1: julie 2.6.0 attests ANY static method: literal — fetch(url, {method:"PURGE"}) is verb-known
        // PURGE. The synthesized carrier "fetch.purge" is outside VerbFromCarrier's whitelist, so the lossy
        // round-trip degraded it to verb-unknown and fabricated a Medium route-only edge to the GET endpoint.
        // Doctrine: both verbs known and different => NO edge; the evidence is still counted.
        var cacheGet = Method("sym-cache-get", "GetCache", "Task<IResult> GetCache()",
            "CacheController", "api/CacheController.cs");
        var tsFn = Type("sym-tsfn", "purgeCache", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/CacheController.cs",
                containingSymbolId: "sym-cache-get",
                startLine: 10,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/cache","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-fetch-purge",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/cache","url_kind":"path","verb":"PURGE","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [cacheGet, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.clientRequests"]);
    }

    [Fact]
    public void ApiBridge_VerbExactHandlerBeatsVerbNullFallbackOnSameRoute()
    {
        // F2: Nuxt supports server/api/notes.get.ts (verb GET) coexisting with suffix-less server/api/notes.ts
        // (verb null fallback) — Nitro routes GET to the .get.ts file deterministically. The specificity tie on
        // the identical route must not drop the legitimate verb-exact High match as ambiguous.
        var getHandler = Type("sym-notes-get", "handler", "function", file: "server/api/notes.get.ts");
        var tsFn = Type("sym-tsfn", "loadNotes", "function", file: "app/composables/notes.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-nuxt-notes-get",
                patternId: "nuxt.server_route.v1",
                language: "typescript",
                path: "server/api/notes.get.ts",
                containingSymbolId: "sym-notes-get",
                startLine: 1,
                metadataJson: """{"framework":"nuxt","route_path":"/api/notes","verb":"GET","verb_source":"attested"}"""),
            StructuralFact(
                factId: "fact-nuxt-notes-fallback",
                patternId: "nuxt.server_route.v1",
                language: "typescript",
                path: "server/api/notes.ts",
                containingSymbolId: string.Empty,
                startLine: 1,
                metadataJson: """{"framework":"nuxt","route_path":"/api/notes"}"""),
            StructuralFact(
                factId: "fact-fetch-notes",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "app/composables/notes.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 6,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/notes","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [getHandler, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-notes-get", hit.Edge.TargetRef.SymbolId);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nuxt-api.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nuxt-api.ambiguousMatches"]);
    }

    [Fact]
    public void ApiBridge_SymbollessClientRequest_SharesOneSourceNodeWithDotnetWeb()
    {
        // F3: one module-scope fetch("/api/x") in a mixed repo (ASP.NET endpoint + Next.js route handler on
        // the same route) must synthesize ONE TsType source node across providers. dotnet-web's RouteBridge
        // uses the canonical route ("api/x", no leading slash); the file-route client-request edge must use
        // the identical form or trace sees two divergent starts and bails.
        var getX = Method("sym-getx", "GetX", "Task<IResult> GetX()", "XController", "api/XController.cs");
        var handler = Type("sym-handler", "GET", "function", file: "web/app/api/x/route.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "fact-httpget",
                patternId: "aspnet.attribute_route.v1",
                language: "csharp",
                path: "api/XController.cs",
                containingSymbolId: "sym-getx",
                startLine: 10,
                metadataJson: """{"attribute_kind":"http_method","verb":"GET","controller_route_template":"api/[controller]","effective_route_template":"/api/x","route_tokens":["controller"]}"""),
            StructuralFact(
                factId: "fact-next-get",
                patternId: "nextjs.route_handler.v1",
                language: "typescript",
                path: "web/app/api/x/route.ts",
                containingSymbolId: "sym-handler",
                startLine: 3,
                metadataJson: """{"framework":"nextjs","router":"app","route_path":"/api/x","verb":"GET","verb_source":"attested"}"""),
            StructuralFact(
                factId: "fact-module-fetch",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/lib/boot.ts",
                containingSymbolId: string.Empty,
                startLine: 5,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/x","url_kind":"path","verb":"GET","verb_source":"default"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [getX, handler], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hits = graph.Edges.Where(e => e.Edge.Kind == BridgeKind.Hits).ToList();
        Assert.Equal(2, hits.Count);

        string expectedSourceId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "api/x");
        Assert.All(hits, hit => Assert.Equal(
            expectedSourceId,
            BridgeGraph.NodeIdOf(hit.Edge.SourceRef, hit.Edge.Kind, EndpointSide.Source)));

        // Both Hits edges are incident on the ONE shared source node — the trace start.
        Assert.Equal(2, graph.Incident(expectedSourceId).Count(e => e.Edge.Kind == BridgeKind.Hits));
        Assert.Contains(graph.Incident(expectedSourceId), e => e.Edge.TargetRef.SymbolId == "sym-getx");
        Assert.Contains(graph.Incident(expectedSourceId), e => e.Edge.TargetRef.SymbolId == "sym-handler");
    }

    [Fact]
    public void Build_DefaultProviders_RecordsObservationNodeProvenancePerProvider()
    {
        // One node per provider family, plus the shared http.client_request.v1 fact BOTH api providers
        // observe: the merged graph must remember exactly which provider(s) emitted each observation node.
        var handler = Type("sym-handler", "GET", "function", file: "web/app/api/messages/route.ts");
        var tsFn = Type("sym-tsfn", "createMessage", "function", file: "web/lib/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            StructuralFact(
                factId: "sf-dashboard-page",
                patternId: "nextjs.file_route.v1",
                language: "tsx",
                path: "web/app/dashboard/page.tsx",
                containingSymbolId: string.Empty,
                startLine: 1,
                metadataJson: """{"route_path":"/dashboard"}"""),
            StructuralFact(
                factId: "sf-messages-handler",
                patternId: "nextjs.route_handler.v1",
                language: "typescript",
                path: "web/app/api/messages/route.ts",
                containingSymbolId: "sym-handler",
                startLine: 1,
                metadataJson: """{"framework":"nextjs","router":"app","route_path":"/api/messages","verb":"GET","verb_source":"attested"}"""),
            StructuralFact(
                factId: "sf-post-messages",
                patternId: "http.client_request.v1",
                language: "typescript",
                path: "web/lib/api.ts",
                containingSymbolId: "sym-tsfn",
                startLine: 8,
                metadataJson: """{"client":"fetch","framework":"fetch","target_path":"/api/messages","url_kind":"path","verb":"POST","verb_source":"attested"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, tsFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.True(graph.HasObservationProvenance);
        Assert.Equal(
            ["nextjs"],
            graph.ObservationProviders(BridgeGraph.SynthesizeId(BridgeNodeKind.FileRoute, "/dashboard")));
        Assert.Equal(
            ["nextjs-api"],
            graph.ObservationProviders(BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, "GET /api/messages")));
        var requestProviders = graph.ObservationProviders(BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, "/api/messages"));
        Assert.Contains("nextjs-api", requestProviders);
        Assert.Contains("nuxt-api", requestProviders);
        Assert.DoesNotContain("nextjs", requestProviders);
        Assert.Empty(graph.ObservationProviders("nope"));
    }

    // ============================ backend-http provider (plan Task 2) ==========================================
    // These fixtures exercise BackendHttpBridgeProvider through the default BridgeGraphBuilder.Build set: a
        // http.client_request.v1 client call sites join backend route-template families
        // (BridgeStructuralPatterns.BackendRoutePatternIds) via FileRouteBridge.ResolveClientRequests — every verb rule inherited from that
    // resolver. Assertions are caller-facing (graph.Incident / CapabilityReport), never on private helpers.

    [Fact]
    public void BackendHttp_express_post_route_hits_client_request_bound_to_handler_symbol_High()
    {
        // Invariant: a verb-equal backend route (Express POST /api/users) joins a POST client request to the
        // route fact's containing handler symbol at High, verb-known.
        var handler = Method("sym-express-handler", "createUser", "createUser(req, res)", string.Empty, "api/users.js");
        var clientFn = Type("sym-client-fn", "createUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-post", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/users",
                    ["url_kind"] = "path",
                    ["verb"] = "POST",
                    ["verb_source"] = "attested",
                }),
            Fact("sf-express-route", "express.route.v1", "javascript", "api/users.js", "sym-express-handler", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/api/users",
                    ["verb"] = "POST",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-express-handler"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-client-fn", hit.Edge.SourceRef.SymbolId);
        Assert.Equal("sym-express-handler", hit.Edge.TargetRef.SymbolId);
        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.clientRequests"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.routeFacts"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.ambiguousMatches"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.mounts"]);
    }

    [Fact]
    public void BackendHttp_colon_param_fastapi_route_hits_concrete_path_client_High()
    {
        // Invariant: the resolver folds the client's concrete segment (/api/users/42) against the route's
        // colon-param template (:user_id) canonically, so a verb-equal fastapi route joins at High.
        var handler = Method("sym-fastapi-handler", "get_user", "get_user(user_id)", string.Empty, "app/routes.py");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/users/42",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-fastapi-route", "fastapi.route.v1", "python", "app/routes.py", "sym-fastapi-handler", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/api/users/:user_id",
                    ["verb"] = "GET",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-fastapi-handler"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-fastapi-handler", hit.Edge.TargetRef.SymbolId);
    }

    [Fact]
    public void BackendHttp_two_equally_specific_routes_are_ambiguous_no_edge()
    {
        // Invariant: two verb-exact backend routes equally specific for the same path/verb tie on specificity —
        // ambiguousMatches increments and no edge is emitted (resolver BestMatch ambiguity).
        var handlerA = Method("sym-fastapi-a", "get_user", "get_user(user_id)", string.Empty, "app/a.py");
        var handlerB = Method("sym-fastapi-b", "show_user", "show_user(other_id)", string.Empty, "app/b.py");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/users/42",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-fastapi-a", "fastapi.route.v1", "python", "app/a.py", "sym-fastapi-a", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/api/users/:user_id",
                    ["verb"] = "GET",
                }),
            Fact("sf-fastapi-b", "fastapi.route.v1", "python", "app/b.py", "sym-fastapi-b", 300,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/api/users/:other_id",
                    ["verb"] = "GET",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handlerA, handlerB, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-fastapi-a"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.DoesNotContain(graph.Incident("sym-fastapi-b"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.ambiguousMatches"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.candidates"]);
        Assert.Equal(2, graph.CapabilityReport.EvidenceCounts["backend-http.routeFacts"]);
    }

    [Fact]
    public void BackendHttp_gin_any_route_with_no_verb_hits_client_Medium_verb_unknown()
    {
        // Invariant: a verbless backend handler (gin Any) matches on route alone → Medium, flagged verb-unknown
        // (never assumed to accept the client verb).
        var handler = Method("sym-gin-handler", "Health", "Health(c *gin.Context)", string.Empty, "server/routes.go");
        var clientFn = Type("sym-client-fn", "checkHealth", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/health",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-gin-route", "gin.route.v1", "go", "server/routes.go", "sym-gin-handler", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/api/health",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-gin-handler"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.Medium, hit.Band);
        Assert.True(hit.IsVerbUnknown);
    }

    [Fact]
    public void BackendHttp_post_client_and_get_only_spring_route_produce_no_edge()
    {
        // Invariant: a verb-known backend handler whose verb differs from the client's is NOT a candidate (a
        // real verb distinction), so no edge is emitted.
        var handler = Method("sym-spring-handler", "listOrders", "ResponseEntity listOrders()", string.Empty, "src/OrderController.java");
        var clientFn = Type("sym-client-fn", "createOrder", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-post", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/orders",
                    ["url_kind"] = "path",
                    ["verb"] = "POST",
                    ["verb_source"] = "attested",
                }),
            Fact("sf-spring-route", "spring.request_mapping.v1", "java", "src/OrderController.java", "sym-spring-handler", 200,
                new Dictionary<string, string>
                {
                    ["attribute_kind"] = "http_method",
                    ["normalized_route_template"] = "/api/orders",
                    ["verb"] = "GET",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-spring-handler"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.candidates"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.routeFacts"]);
    }

    [Fact]
    public void BackendHttp_django_path_pattern_no_verb_hits_client_Medium_verb_unknown()
    {
        // Invariant: a Django path-syntax URLconf route carries no verb → Medium, flagged verb-unknown.
        var handler = Method("sym-django-handler", "article_detail", "article_detail(request)", string.Empty, "app/urls.py");
        var clientFn = Type("sym-client-fn", "loadArticles", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/articles",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-django-route", "django.url_pattern.v1", "python", "app/urls.py", "sym-django-handler", 200,
                new Dictionary<string, string>
                {
                    ["route_syntax"] = "path",
                    ["normalized_route_template"] = "/api/articles",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-django-handler"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.Medium, hit.Band);
        Assert.True(hit.IsVerbUnknown);
    }

    [Fact]
    public void BackendHttp_client_only_repo_is_active_with_observation_node_and_no_edges()
    {
        // Invariant: a pure-frontend repo (client requests, zero backend routes) is ACTIVE and emits a client
        // observation node, but fabricates no edge — candidates == 0 (mirrors the nextjs-api/nuxt-api arm).
        var clientFn = Type("sym-client-fn", "loadUsers", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/users",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.clientRequests"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.routeFacts"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.candidates"]);
        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        var clientNodeId = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, FileRouteBridgeProvider.RouteDisplay("/api/users"));
        Assert.True(graph.Contains(clientNodeId));
        Assert.Contains("backend-http", graph.ObservationProviders(clientNodeId));
    }

    [Fact]
    public void BackendHttp_mounts_evidence_counts_mount_facts_and_evidence_only_rails_mount()
    {
        // Invariant: backend-http.mounts counts all mount/include facts observed (the 4 read families) PLUS
        // evidence-only rails.mount facts; Task 2 collects but composes nothing — no edge is emitted.
        var mountFn = Type("sym-mount", "app", "variable", file: "api/app.js");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-express-mount", "express.router_mount.v1", "javascript", "api/app.js", "sym-mount", 100,
                new Dictionary<string, string>
                {
                    ["normalized_mount_path"] = "/api",
                    ["mount_target"] = "usersRouter",
                }),
            Fact("sf-rails-mount", "rails.mount.v1", "ruby", "config/routes.rb", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["mount_path"] = "/sidekiq",
                    ["mount_target"] = "Sidekiq::Web",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [mountFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(2, graph.CapabilityReport.EvidenceCounts["backend-http.mounts"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.candidates"]);
        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
    }

    // ============================ backend-http mount composition (plan Task 3) =================================
    // Cross-file mount-prefix composition: a mount/include fact anchors — deterministically, unambiguous-or-nothing
    // — to route facts in ANOTHER file, and Miller APPENDS composed route variants (RoutePath =
    // JoinRoute(mountPath, routePath); verb/symbol/file/line unchanged) so a client hitting the mounted prefix
    // joins the router-local route. Composition is STRICTLY ADDITIVE — the original router-local entry is never
    // replaced (route facts carry no receiver identity, so ownership of a route by the mounted router is
    // unprovable; replacing would hide legitimate direct routes). An ambiguous/tied/absent anchor composes NOTHING
    // (ambiguity poisons, never degrades) and is counted in backend-http.unanchoredMounts.

    [Fact]
    public void BackendHttp_express_router_mount_composes_prefixed_route_High_and_keeps_original()
    {
        // Invariant: express router.get("/:id") in web/users.js (symbol usersRouter also defined there) +
        // app.use("/users", usersRouter) in web/app.js → composed /users/:id joins fetch("/users/42") at High,
        // bound to the route fact's handler symbol. ADDITIVE: the original router-local /:id is still counted in
        // routeFacts AND still present as an unmatched endpoint observation node — never replaced.
        var handler = Method("sym-users-show", "show", "show(req, res)", string.Empty, "web/users.js");
        var routerSym = Type("sym-users-router", "usersRouter", "variable", file: "web/users.js");
        var appSym = Type("sym-app", "app", "variable", file: "web/app.js");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/users/42",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-express-route", "express.route.v1", "javascript", "web/users.js", "sym-users-show", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/:id",
                    ["verb"] = "GET",
                }),
            Fact("sf-express-mount", "express.router_mount.v1", "javascript", "web/app.js", "sym-app", 300,
                new Dictionary<string, string>
                {
                    ["normalized_mount_path"] = "/users",
                    ["mount_target"] = "usersRouter",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, routerSym, appSym, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-users-show"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-client-fn", hit.Edge.SourceRef.SymbolId);
        Assert.Equal("sym-users-show", hit.Edge.TargetRef.SymbolId);

        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
        // Additive proof #1: the original /:id backend route is still counted (routeFacts counts originals only).
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.routeFacts"]);
        // Additive proof #2: the original router-local /:id is still an (unmatched) endpoint observation node —
        // the composed /users/:id was ADDED beside it, not substituted for it.
        var originalEndpointId = BridgeGraph.SynthesizeId(
            BridgeNodeKind.Endpoint,
            FileRouteBridge.HandlerDisplay(new StructuralRouteHandler(
                facts[1], "/:id", "GET", "sym-users-show", "web/users.js", 1)));
        Assert.True(graph.Contains(originalEndpointId));
        Assert.Contains("backend-http", graph.ObservationProviders(originalEndpointId));
    }

    [Fact]
    public void BackendHttp_django_url_include_module_anchor_composes_Medium_verb_unknown()
    {
        // Invariant: django url_include(mount_path="/shop/", included_module="shop.urls") anchors by MODULE PATH to
        // url_pattern facts in a file ending shop/urls.py (dots→'/', + ".py"), composing /shop/posts. Django
        // URLconf carries no verb → the composed edge is Medium verb_unknown.
        var clientFn = Type("sym-client-fn", "loadPosts", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/shop/posts",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-django-route", "django.url_pattern.v1", "python", "src/shop/urls.py", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["route_syntax"] = "path",
                    ["normalized_route_template"] = "/posts",
                }),
            Fact("sf-django-include", "django.url_include.v1", "python", "src/config/urls.py", string.Empty, 300,
                new Dictionary<string, string>
                {
                    ["normalized_mount_path"] = "/shop/",
                    ["included_module"] = "shop.urls",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-client-fn"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.Medium, hit.Band);
        Assert.True(hit.IsVerbUnknown);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
    }

    [Fact]
    public void BackendHttp_django_url_include_unmatched_module_composes_nothing_and_counts_unanchored()
    {
        // Invariant (absence poisons): included_module="shop.urls" but the only url_pattern lives in other/urls.py
        // (does not end with shop/urls.py) → zero module-anchor match → NO compose, unanchoredMounts counted.
        var clientFn = Type("sym-client-fn", "loadPosts", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/shop/posts",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-django-route", "django.url_pattern.v1", "python", "src/other/urls.py", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["route_syntax"] = "path",
                    ["normalized_route_template"] = "/posts",
                }),
            Fact("sf-django-include", "django.url_include.v1", "python", "src/config/urls.py", string.Empty, 300,
                new Dictionary<string, string>
                {
                    ["normalized_mount_path"] = "/shop/",
                    ["included_module"] = "shop.urls",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
    }

    [Fact]
    public void BackendHttp_two_files_define_router_identifier_is_ambiguous_composes_nothing()
    {
        // Invariant (tie poisons): two non-test files both define `usersRouter` AND both own express routes → the
        // identifier anchor is a TIE → NO compose, unanchoredMounts counted, zero composed edges.
        var handlerA = Method("sym-a-show", "show", "show(req,res)", string.Empty, "web/a/users.js");
        var routerA = Type("sym-a-router", "usersRouter", "variable", file: "web/a/users.js");
        var handlerB = Method("sym-b-show", "show", "show(req,res)", string.Empty, "web/b/users.js");
        var routerB = Type("sym-b-router", "usersRouter", "variable", file: "web/b/users.js");
        var appSym = Type("sym-app", "app", "variable", file: "web/app.js");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/users/42",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-route-a", "express.route.v1", "javascript", "web/a/users.js", "sym-a-show", 200,
                new Dictionary<string, string> { ["normalized_route_template"] = "/:id", ["verb"] = "GET" }),
            Fact("sf-route-b", "express.route.v1", "javascript", "web/b/users.js", "sym-b-show", 210,
                new Dictionary<string, string> { ["normalized_route_template"] = "/:id", ["verb"] = "GET" }),
            Fact("sf-mount", "express.router_mount.v1", "javascript", "web/app.js", "sym-app", 300,
                new Dictionary<string, string> { ["normalized_mount_path"] = "/users", ["mount_target"] = "usersRouter" }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handlerA, routerA, handlerB, routerB, appSym, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
    }

    [Fact]
    public void BackendHttp_middleware_mount_target_resolves_to_no_route_owning_file_composes_nothing()
    {
        // Invariant (absence poisons): app.use("/api", express.json()) → identifier `json` (call args dropped)
        // names no route-owning file → NO compose, unanchoredMounts counted.
        var handler = Method("sym-users-show", "show", "show(req,res)", string.Empty, "web/users.js");
        var routerSym = Type("sym-users-router", "usersRouter", "variable", file: "web/users.js");
        var appSym = Type("sym-app", "app", "variable", file: "web/app.js");
        var clientFn = Type("sym-client-fn", "loadConfig", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/config",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-express-route", "express.route.v1", "javascript", "web/users.js", "sym-users-show", 200,
                new Dictionary<string, string> { ["normalized_route_template"] = "/:id", ["verb"] = "GET" }),
            Fact("sf-mw-mount", "express.router_mount.v1", "javascript", "web/app.js", "sym-app", 300,
                new Dictionary<string, string> { ["normalized_mount_path"] = "/api", ["mount_target"] = "express.json()" }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, routerSym, appSym, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void BackendHttp_prefixless_fastapi_include_is_not_a_mount_and_composes_nothing()
    {
        // Invariant: a fastapi include_router with NO mount_path is rejected at TryReadMountFact (Task 1) → it
        // never reaches mountFacts, is not counted in backend-http.mounts, and composes nothing. The fastapi
        // route still joins its client DIRECTLY (no prefix added).
        var handler = Method("sym-items", "list_items", "list_items()", string.Empty, "app/items.py");
        var routerSym = Type("sym-items-router", "api_router", "variable", file: "app/items.py");
        var clientFn = Type("sym-client-fn", "loadItems", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/items",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-fastapi-route", "fastapi.route.v1", "python", "app/items.py", "sym-items", 200,
                new Dictionary<string, string> { ["normalized_route_template"] = "/api/items", ["verb"] = "GET" }),
            Fact("sf-include", "fastapi.include_router.v1", "python", "app/main.py", string.Empty, 300,
                new Dictionary<string, string> { ["mount_target"] = "api_router" }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, routerSym, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-items"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.mounts"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
    }

    [Fact]
    public void BackendHttp_same_router_mounted_at_two_prefixes_composes_both_variants()
    {
        // Invariant: app.use("/a", r) + app.use("/b", r), one routes file defining `r` → composed variants under
        // BOTH /a and /b (correct Express semantics — two distinct reachable paths), each joining its own client
        // at High.
        var handler = Method("sym-r-show", "show", "show(req,res)", string.Empty, "web/r.js");
        var routerSym = Type("sym-r", "r", "variable", file: "web/r.js");
        var appSym = Type("sym-app", "app", "variable", file: "web/app.js");
        var clientA = Type("sym-client-a", "loadA", "function", file: "web/api.ts");
        var clientB = Type("sym-client-b", "loadB", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-a", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-a", 100,
                new Dictionary<string, string> { ["client"] = "fetch", ["target_path"] = "/a/42", ["url_kind"] = "path", ["verb"] = "GET", ["verb_source"] = "default" }),
            Fact("sf-client-b", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-b", 110,
                new Dictionary<string, string> { ["client"] = "fetch", ["target_path"] = "/b/42", ["url_kind"] = "path", ["verb"] = "GET", ["verb_source"] = "default" }),
            Fact("sf-route", "express.route.v1", "javascript", "web/r.js", "sym-r-show", 200,
                new Dictionary<string, string> { ["normalized_route_template"] = "/:id", ["verb"] = "GET" }),
            Fact("sf-mount-a", "express.router_mount.v1", "javascript", "web/app.js", "sym-app", 300,
                new Dictionary<string, string> { ["normalized_mount_path"] = "/a", ["mount_target"] = "r" }),
            Fact("sf-mount-b", "express.router_mount.v1", "javascript", "web/app.js", "sym-app", 310,
                new Dictionary<string, string> { ["normalized_mount_path"] = "/b", ["mount_target"] = "r" }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, routerSym, appSym, clientA, clientB], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hits = graph.Incident("sym-r-show").Where(e => e.Edge.Kind == BridgeKind.Hits).ToList();
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal(ConfidenceBand.High, h.Band));
        Assert.Contains(hits, h => h.Edge.SourceRef.SymbolId == "sym-client-a");
        Assert.Contains(hits, h => h.Edge.SourceRef.SymbolId == "sym-client-b");
        Assert.Equal(2, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
    }

    [Fact]
    public void BackendHttp_mixed_file_direct_route_also_gains_a_composed_variant_accepted_tradeoff()
    {
        // ACCEPTED, DOCUMENTED TRADEOFF (plan Task 3): route facts carry NO receiver identity, so when a mounted
        // router's routes file ALSO contains a direct app.get route, Miller cannot prove which facts belong to the
        // router. The ANCHOR is unambiguous-or-nothing (one file), but WITHIN the anchored file every composable
        // route of the family gains a prefixed variant — including the direct app.get. The composed /users/health
        // below is SPURIOUS (the real app.get is /health, not under the router), yet a client to /users/health
        // matches it. This is the accepted cost; the fix is a tighter upstream anchor (receiver facts), never a
        // lower band. Pinned so a future change cannot silently "fix" it by dropping the retained direct route.
        var showSym = Method("sym-users-show", "show", "show(req,res)", string.Empty, "web/users.js");
        var healthSym = Method("sym-health", "health", "health(req,res)", string.Empty, "web/users.js");
        var routerSym = Type("sym-users-router", "usersRouter", "variable", file: "web/users.js");
        var appSym = Type("sym-app", "app", "variable", file: "web/app.js");
        var clientFn = Type("sym-client-fn", "checkHealth", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string> { ["client"] = "fetch", ["target_path"] = "/users/health", ["url_kind"] = "path", ["verb"] = "GET", ["verb_source"] = "default" }),
            Fact("sf-route-show", "express.route.v1", "javascript", "web/users.js", "sym-users-show", 200,
                new Dictionary<string, string> { ["normalized_route_template"] = "/:id", ["verb"] = "GET" }),
            Fact("sf-route-health", "express.route.v1", "javascript", "web/users.js", "sym-health", 210,
                new Dictionary<string, string> { ["normalized_route_template"] = "/health", ["verb"] = "GET" }),
            Fact("sf-mount", "express.router_mount.v1", "javascript", "web/app.js", "sym-app", 300,
                new Dictionary<string, string> { ["normalized_mount_path"] = "/users", ["mount_target"] = "usersRouter" }),
        };

        var graph = BridgeGraphBuilder.Build(
            [showSym, healthSym, routerSym, appSym, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        // The direct app.get /health ALSO gained a composed /users/health (spurious but accepted): the client to
        // /users/health binds to the direct route's handler symbol as the literal-specificity winner.
        var hit = Assert.Single(graph.Incident("sym-health"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal("sym-health", hit.Edge.TargetRef.SymbolId);
        // Both the router-local /:id and the direct /health composed → two composed variants.
        Assert.Equal(2, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
    }

    [Fact]
    public void BackendHttp_route_with_effective_template_is_never_double_composed()
    {
        // Invariant: a route fact already carrying effective_route_template (/users/:id) was prefixed UPSTREAM
        // (same-file app.use); composing again would double-prefix (/users/users/:id). Such facts are skipped by
        // composition. The client joins the already-effective route DIRECTLY at High; composedRoutes==0 and no
        // /users/users node exists. The mount still ANCHORED (one candidate file) → unanchoredMounts==0.
        var handler = Method("sym-users-show", "show", "show(req,res)", string.Empty, "web/users.js");
        var routerSym = Type("sym-users-router", "usersRouter", "variable", file: "web/users.js");
        var appSym = Type("sym-app", "app", "variable", file: "web/app.js");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string> { ["client"] = "fetch", ["target_path"] = "/users/42", ["url_kind"] = "path", ["verb"] = "GET", ["verb_source"] = "default" }),
            Fact("sf-express-route", "express.route.v1", "javascript", "web/users.js", "sym-users-show", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/:id",
                    ["effective_route_template"] = "/users/:id",
                    ["verb"] = "GET",
                }),
            Fact("sf-express-mount", "express.router_mount.v1", "javascript", "web/app.js", "sym-app", 300,
                new Dictionary<string, string> { ["normalized_mount_path"] = "/users", ["mount_target"] = "usersRouter" }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, routerSym, appSym, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-users-show"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
        Assert.DoesNotContain(graph.Nodes.Values, n => n.Display.Contains("users/users", StringComparison.Ordinal));
    }

    [Fact]
    public void BackendHttp_fastapi_dotted_target_requires_module_to_match_file_stem()
    {
        // Invariant: a dotted fastapi target (users.router) anchors only when the candidate file's stem
        // (users.py → users) equals the module segment. A same-named `router` symbol in a different-stem file
        // (admin.py) is filtered out by the stem requirement, so users.py is the UNIQUE anchor (without the stem
        // filter the two `router` files would tie and compose nothing).
        var usersHandler = Method("sym-users", "get_user", "get_user()", string.Empty, "app/users.py");
        var usersRouter = Type("sym-users-router", "router", "variable", file: "app/users.py");
        var adminHandler = Method("sym-admin", "get_admin", "get_admin()", string.Empty, "app/admin.py");
        var adminRouter = Type("sym-admin-router", "router", "variable", file: "app/admin.py");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string> { ["client"] = "fetch", ["target_path"] = "/users/42", ["url_kind"] = "path", ["verb"] = "GET", ["verb_source"] = "default" }),
            Fact("sf-users-route", "fastapi.route.v1", "python", "app/users.py", "sym-users", 200,
                new Dictionary<string, string> { ["normalized_route_template"] = "/:id", ["verb"] = "GET" }),
            Fact("sf-admin-route", "fastapi.route.v1", "python", "app/admin.py", "sym-admin", 210,
                new Dictionary<string, string> { ["normalized_route_template"] = "/:id", ["verb"] = "GET" }),
            Fact("sf-include", "fastapi.include_router.v1", "python", "app/main.py", string.Empty, 300,
                new Dictionary<string, string> { ["normalized_mount_path"] = "/users", ["mount_target"] = "users.router" }),
        };

        var graph = BridgeGraphBuilder.Build(
            [usersHandler, usersRouter, adminHandler, adminRouter, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-users"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal("sym-users", hit.Edge.TargetRef.SymbolId);
        Assert.DoesNotContain(graph.Incident("sym-admin"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
    }

    [Fact]
    public void BackendHttp_no_evidence_skips_with_reason_and_zero_counts()
    {
        // Invariant: with no client requests, backend routes, mount facts, or rails.mount facts, the provider
        // SKIPS with a stable reason and all-zero evidence counts.
        var symbols = new List<SymbolDetail> { Type("sym-x", "X", "class", "Domain") };
        var dbSets = new List<DbSetProperty> { DbSet("Xs", "X") };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], dbSets);

        Assert.Contains(graph.CapabilityReport.SkippedProviders, skipped =>
            skipped.ProviderId == "backend-http" &&
            skipped.Reason.Contains("no backend-http bridge evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.clientRequests"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.routeFacts"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.mounts"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.ambiguousMatches"]);
    }

    [Fact]
    public void Build_null_collections_throw()
    {
        Assert.Throws<ArgumentNullException>(() => BridgeGraphBuilder.Build(null!, [], [], [], []));
        Assert.Throws<ArgumentNullException>(() => BridgeGraphBuilder.Build([], null!, [], [], []));
        Assert.Throws<ArgumentNullException>(() => BridgeGraphBuilder.Build([], [], null!, [], []));
        Assert.Throws<ArgumentNullException>(() => BridgeGraphBuilder.Build([], [], [], null!, []));
        Assert.Throws<ArgumentNullException>(() => BridgeGraphBuilder.Build([], [], [], [], null!));
        Assert.Throws<ArgumentNullException>(() => BridgeGraphBuilder.Build([], [], [], [], [], providers: null!));
    }

    private static LiteralRecord MakeLiteral(
        string text, string kind, string language = "csharp", string carrier = "axios.get",
        string containingSymbolId = "sym", int spanStart = 0)
        => new(
            LiteralText: text,
            Kind: kind,
            Carrier: carrier,
            ArgPosition: 0,
            Language: language,
            ContainingSymbolId: containingSymbolId,
            Span: new SourceSpan(spanStart, spanStart + text.Length));

    private static StructuralFactRecord StructuralFact(
        string factId,
        string patternId,
        string language,
        string path,
        string? containingSymbolId,
        int startLine,
        string metadataJson)
        => new(
            FactId: factId,
            PatternId: patternId,
            Language: language,
            Path: path,
            CaptureName: "framework.route",
            NodeKind: "node",
            ContainingSymbolId: containingSymbolId,
            Span: new StructuralFactSpan(
                startLine,
                StartColumn: 1,
                startLine,
                EndColumn: 1,
                startLine * 10,
                startLine * 10 + 1),
            Confidence: 1.0,
            Metadata: ParseMetadata(metadataJson));

    private static IReadOnlyDictionary<string, string> ParseMetadata(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            metadata[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }
        return metadata;
    }

    // ===== Task 1: Backend HTTP boundary — whitelist + adapter reads (16 new families) =====

    [Fact]
    public void BridgeStructuralPatterns_BridgeFactPatternIds_ContainsAll28BackendFamilies()
    {
        // The load whitelist (SqliteBridgeReader SQL gate): an id absent here never reaches a provider.
        var backendIds = new[]
        {
            // 2.7.0 wave 1 (16).
            BridgeStructuralPatterns.ExpressRoute,
            BridgeStructuralPatterns.ExpressRouterMount,
            BridgeStructuralPatterns.FastifyRoute,
            BridgeStructuralPatterns.FastApiRoute,
            BridgeStructuralPatterns.FastApiIncludeRouter,
            BridgeStructuralPatterns.FlaskRoute,
            BridgeStructuralPatterns.FlaskBlueprintRegistration,
            BridgeStructuralPatterns.DjangoUrlPattern,
            BridgeStructuralPatterns.DjangoUrlInclude,
            BridgeStructuralPatterns.SpringRequestMapping,
            BridgeStructuralPatterns.GoNetHttpRoute,
            BridgeStructuralPatterns.GinRoute,
            BridgeStructuralPatterns.EchoRoute,
            BridgeStructuralPatterns.RailsRoute,
            BridgeStructuralPatterns.RailsResourceRoute,
            BridgeStructuralPatterns.RailsMount,
            // 2.8.0 wave 2 (12): six more stacks — routes, resource declarations, and prefix/mount families.
            BridgeStructuralPatterns.NestJsRoute,
            BridgeStructuralPatterns.LaravelRoute,
            BridgeStructuralPatterns.LaravelResourceRoute,
            BridgeStructuralPatterns.LaravelRoutePrefix,
            BridgeStructuralPatterns.PhoenixRoute,
            BridgeStructuralPatterns.PhoenixResourceRoute,
            BridgeStructuralPatterns.PhoenixForward,
            BridgeStructuralPatterns.AxumRoute,
            BridgeStructuralPatterns.AxumNest,
            BridgeStructuralPatterns.ActixAttributeRoute,
            BridgeStructuralPatterns.ActixScopeRoute,
            BridgeStructuralPatterns.ActixMount,
        };

        Assert.Equal(28, backendIds.Length); // self-check: every constant enumerated
        foreach (var id in backendIds)
            Assert.Contains(id, BridgeStructuralPatterns.BridgeFactPatternIds);
    }

    [Fact]
    public void BridgeStructuralPatterns_BackendRoutePatternIds_ContainsTheSixteenRouteFamiliesOnly()
    {
        var routeIds = BridgeStructuralPatterns.BackendRoutePatternIds;

        Assert.Equal(16, routeIds.Count);
        // The 16 route-template families the provider joins against normalized_route_template (2.7.0 + 2.8.0).
        Assert.Contains(BridgeStructuralPatterns.ExpressRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.FastifyRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.FastApiRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.FlaskRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.DjangoUrlPattern, routeIds);
        Assert.Contains(BridgeStructuralPatterns.SpringRequestMapping, routeIds);
        Assert.Contains(BridgeStructuralPatterns.GoNetHttpRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.GinRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.EchoRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.RailsRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.NestJsRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.LaravelRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.PhoenixRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.AxumRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.ActixAttributeRoute, routeIds);
        Assert.Contains(BridgeStructuralPatterns.ActixScopeRoute, routeIds);
        // Mount/prefix families, resource-declaration families, and Rails evidence-only are NOT route-template inputs.
        Assert.DoesNotContain(BridgeStructuralPatterns.ExpressRouterMount, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.FastApiIncludeRouter, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.FlaskBlueprintRegistration, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.DjangoUrlInclude, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.RailsResourceRoute, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.RailsMount, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.LaravelResourceRoute, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.LaravelRoutePrefix, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.PhoenixResourceRoute, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.PhoenixForward, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.AxumNest, routeIds);
        Assert.DoesNotContain(BridgeStructuralPatterns.ActixMount, routeIds);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadBackendRoute_PrefersEffectiveRouteTemplateOverNormalized()
    {
        var fact = Fact(
            "sf-fastapi-users",
            "fastapi.route.v1",
            "python",
            "app/routers/users.py",
            "sym-fastapi-handler",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "fastapi",
                ["router_prefix"] = "/api",
                ["route_template"] = "/users/{user_id}",
                ["normalized_route_template"] = "/users/:user_id",
                ["effective_route_template"] = "/api/users/:user_id",
                ["verb"] = "GET",
                ["verb_source"] = "attested",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadBackendRoute(fact, new Dictionary<string, SymbolDetail>(), out var handler));
        Assert.Equal("/api/users/:user_id", handler.RoutePath); // effective_route_template wins over normalized
        Assert.Equal("GET", handler.Verb);
        Assert.Equal("sym-fastapi-handler", handler.ContainingSymbolId);
        Assert.Equal("app/routers/users.py", handler.FilePath);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadBackendRoute_VerblessExpressAppAllHasNullVerb()
    {
        var fact = Fact(
            "sf-express-all",
            "express.route.v1",
            "typescript",
            "src/server.ts",
            "sym-express",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "express",
                ["route_template"] = "/api/webhook",
                ["normalized_route_template"] = "/api/webhook",
                // no verb key: app.all answers every method
            });

        Assert.True(StructuralRouteFactAdapter.TryReadBackendRoute(fact, new Dictionary<string, SymbolDetail>(), out var handler));
        Assert.Equal("/api/webhook", handler.RoutePath); // normalized_route_template (no same-file prefix)
        Assert.Null(handler.Verb); // verbless app.all → null verb, never assumed GET
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadBackendRoute_UppercasesVerb()
    {
        var fact = Fact(
            "sf-gin-create",
            "gin.route.v1",
            "go",
            "main.go",
            "sym-gin",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "gin",
                ["normalized_route_template"] = "/users",
                ["verb"] = "post",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadBackendRoute(fact, new Dictionary<string, SymbolDetail>(), out var handler));
        Assert.Equal("POST", handler.Verb); // verb normalized to UPPERCASE
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadBackendRoute_RejectsSpringClassRoute()
    {
        var fact = Fact(
            "sf-spring-class",
            "spring.request_mapping.v1",
            "java",
            "src/main/java/com/example/UserController.java",
            "sym-controller",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "spring",
                ["attribute_kind"] = "class_route",
                ["class_route_template"] = "/api/users",
                ["normalized_route_template"] = "/api/users", // present, yet a prefix fact is never an endpoint
            });

        Assert.False(StructuralRouteFactAdapter.TryReadBackendRoute(fact, new Dictionary<string, SymbolDetail>(), out _));
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadBackendRoute_RejectsDjangoRegexSyntaxWithNoTemplate()
    {
        var fact = Fact(
            "sf-django-regex",
            "django.url_pattern.v1",
            "python",
            "app/urls.py",
            "sym-urlconf",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "django",
                ["route_syntax"] = "regex",
                ["view_target"] = "views.legacy",
                // no normalized_route_template: regex urlpatterns are honestly excluded, never synthesized
            });

        Assert.False(StructuralRouteFactAdapter.TryReadBackendRoute(fact, new Dictionary<string, SymbolDetail>(), out _));
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadBackendRoute_RejectsTestFacts()
    {
        var fact = Fact(
            "sf-express-test",
            "express.route.v1",
            "typescript",
            "src/routes.test.ts",
            string.Empty,
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "express",
                ["normalized_route_template"] = "/api/users",
                ["verb"] = "GET",
            });

        Assert.False(StructuralRouteFactAdapter.TryReadBackendRoute(fact, new Dictionary<string, SymbolDetail>(), out _));
    }

    [Theory]
    // Polyglot test-dir + unit-test-suffix conventions. The containing symbol is EMPTY, so the path is the only
    // signal — a module-level backend route in a test tree carries no test-marked symbol, exactly the case the
    // symbol check cannot catch. The pattern id is immaterial to the path predicate (isolated here).
    [InlineData("project/tests/urls.py")]     // Django test URLconf — `tests/` dir segment
    [InlineData("app/handlers_test.go")]      // Go unit-test file suffix `_test.`
    [InlineData("spec/routing_spec.rb")]      // Rails/RSpec — `spec/` dir + `_spec.` suffix
    [InlineData("src/test/java/App.java")]    // Java Maven `src/test/**` tree
    public void StructuralRouteFactAdapter_TryReadBackendRoute_RejectsRouteInTestPath(string path)
    {
        var fact = Fact(
            "sf-route-in-test", "express.route.v1", "typescript", path, string.Empty, 100,
            new Dictionary<string, string> { ["normalized_route_template"] = "/api/users", ["verb"] = "GET" });

        Assert.False(
            StructuralRouteFactAdapter.TryReadBackendRoute(fact, new Dictionary<string, SymbolDetail>(), out _),
            $"a route fact at test path '{path}' must be excluded from bridging");
    }

    [Theory]
    // Production paths that only LOOK test-like — the segment/suffix check must be boundary-anchored so it never
    // over-excludes a real route file (poisoning production bridging is worse than a missed test exclusion).
    [InlineData("app/latest/routes.py")]      // "latest" is not the "test" segment
    [InlineData("src/contest/api.go")]        // "contest" does not start with "test/"
    [InlineData("web/greatest.py")]           // "greatest" carries no `_test.`/`.test.` marker
    public void StructuralRouteFactAdapter_TryReadBackendRoute_KeepsRouteInTestLookalikePath(string path)
    {
        var fact = Fact(
            "sf-route-lookalike", "express.route.v1", "typescript", path, string.Empty, 100,
            new Dictionary<string, string> { ["normalized_route_template"] = "/api/users", ["verb"] = "GET" });

        Assert.True(
            StructuralRouteFactAdapter.TryReadBackendRoute(fact, new Dictionary<string, SymbolDetail>(), out _),
            $"a production route fact at test-lookalike path '{path}' must still bridge");
    }

    [Theory]
    [InlineData("express.router_mount.v1")]
    [InlineData("rails.resource_route.v1")]
    [InlineData("rails.mount.v1")]
    [InlineData("laravel.resource_route.v1")] // 2.8.0 aggregate declaration — expanded, never read as a route
    [InlineData("phoenix.resource_route.v1")]
    [InlineData("axum.nest.v1")]              // 2.8.0 mount family — not a route-template family
    [InlineData("actix.mount.v1")]
    [InlineData("laravel.route_prefix.v1")]
    public void StructuralRouteFactAdapter_TryReadBackendRoute_RejectsNonRouteFamilies(string patternId)
    {
        var fact = Fact(
            "sf-non-route",
            patternId,
            "ruby",
            "config/routes.rb",
            "sym-x",
            100,
            new Dictionary<string, string>
            {
                ["normalized_route_template"] = "/users", // even with a template, not a route-template family
                ["verb"] = "GET",
            });

        Assert.False(StructuralRouteFactAdapter.TryReadBackendRoute(fact, new Dictionary<string, SymbolDetail>(), out _));
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadMountFact_ReadsDjangoIncludedModule()
    {
        var fact = Fact(
            "sf-django-include",
            "django.url_include.v1",
            "python",
            "project/urls.py",
            "sym-rooturls",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "django",
                ["mount_path"] = "/users/",
                ["normalized_mount_path"] = "/users",
                ["included_module"] = "users.urls",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadMountFact(fact, new Dictionary<string, SymbolDetail>(), out var mount));
        Assert.Equal("/users", mount.MountPath); // normalized_mount_path preferred
        Assert.Equal("users.urls", mount.IncludedModule);
        Assert.Equal("project/urls.py", mount.FilePath);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadMountFact_ReadsExpressMountTargetAndPrefersNormalizedPath()
    {
        var fact = Fact(
            "sf-express-mount",
            "express.router_mount.v1",
            "typescript",
            "src/server.ts",
            "sym-app",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "express",
                ["mount_path"] = "/users/",
                ["normalized_mount_path"] = "/users",
                ["mount_target"] = "usersRouter",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadMountFact(fact, new Dictionary<string, SymbolDetail>(), out var mount));
        Assert.Equal("/users", mount.MountPath);
        Assert.Equal("usersRouter", mount.MountTarget);
        Assert.Null(mount.IncludedModule); // included_module is Django-only
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadMountFact_FallsBackToMountPathWhenNoNormalized()
    {
        var fact = Fact(
            "sf-flask-bp",
            "flask.blueprint_registration.v1",
            "python",
            "app/__init__.py",
            "sym-createapp",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "flask",
                ["mount_path"] = "/admin",
                ["mount_target"] = "admin_bp",
                // no normalized_mount_path
            });

        Assert.True(StructuralRouteFactAdapter.TryReadMountFact(fact, new Dictionary<string, SymbolDetail>(), out var mount));
        Assert.Equal("/admin", mount.MountPath); // falls back to mount_path
        Assert.Equal("admin_bp", mount.MountTarget);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadMountFact_RejectsPrefixlessFastapiInclude()
    {
        var fact = Fact(
            "sf-fastapi-include-noprefix",
            "fastapi.include_router.v1",
            "python",
            "app/main.py",
            "sym-mainapp",
            100,
            new Dictionary<string, string>
            {
                ["framework"] = "fastapi",
                ["mount_target"] = "users.router",
                // no mount_path / normalized_mount_path: an un-prefixed include composes nothing
            });

        Assert.False(StructuralRouteFactAdapter.TryReadMountFact(fact, new Dictionary<string, SymbolDetail>(), out _));
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadMountFact_RejectsTestFacts()
    {
        var symbolsById = new Dictionary<string, SymbolDetail>
        {
            ["sym-test-urls"] = new(
                "sym-test-urls",
                "urlpatterns",
                "variable",
                "project/tests/urls.py",
                Signature: "urlpatterns",
                Namespace: null,
                IsTest: true,
                ParentClassName: null),
        };
        var fact = Fact(
            "sf-django-include-test",
            "django.url_include.v1",
            "python",
            "project/tests/urls.py",
            "sym-test-urls",
            100,
            new Dictionary<string, string>
            {
                ["mount_path"] = "/users/",
                ["normalized_mount_path"] = "/users",
                ["included_module"] = "users.urls",
            });

        Assert.False(StructuralRouteFactAdapter.TryReadMountFact(fact, symbolsById, out _));
    }

    [Theory]
    [InlineData("rails.mount.v1")]
    [InlineData("express.route.v1")]
    [InlineData("nextjs.route_handler.v1")]
    [InlineData("nestjs.route.v1")]           // 2.8.0 plain route family — not a mount
    [InlineData("laravel.resource_route.v1")] // 2.8.0 resource declaration — not a mount
    [InlineData("actix.attribute_route.v1")]
    public void StructuralRouteFactAdapter_TryReadMountFact_RejectsNonMountFamilies(string patternId)
    {
        var fact = Fact(
            "sf-non-mount",
            patternId,
            "ruby",
            "config/routes.rb",
            "sym-x",
            100,
            new Dictionary<string, string>
            {
                ["mount_path"] = "/engine",
                ["normalized_mount_path"] = "/engine",
                ["mount_target"] = "SomeEngine",
            });

        Assert.False(StructuralRouteFactAdapter.TryReadMountFact(fact, new Dictionary<string, SymbolDetail>(), out _));
    }

    // ============================ backend-http Rails semantics (plan Task 4) ==================================
    // Rails is Miller's job (julie handoff): (a) rails.resource_route.v1 facts are expanded into concrete,
    // verb-known route handlers by deterministic Rails doctrine, and (b) Rails route handlers bind to their
    // controller-action method symbol UNAMBIGUOUSLY-OR-NOTHING. Expanded handlers carry the resource fact's
    // routes.rb file/line (trace points at the declaring DSL line); the bound method id targets the edge.

    private static StructuralFactRecord ResourceFact(
        string id,
        string resourceName,
        string resourceKind,
        IReadOnlyDictionary<string, string>? extra = null,
        string path = "config/routes.rb")
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_style"] = "dsl_routing",
            ["resource_name"] = resourceName,
            ["resource_kind"] = resourceKind,
        };
        if (extra is not null)
            foreach (var kv in extra)
                metadata[kv.Key] = kv.Value;
        return Fact(id, "rails.resource_route.v1", "ruby", path, string.Empty, 200, metadata);
    }

    [Fact]
    public void BackendHttp_rails_resources_collection_expands_to_eight_route_entries()
    {
        // Invariant: `resources :users` (collection) expands to the 8 canonical Rails handler entries
        // (index/create/new/edit/show, update as PATCH AND PUT, destroy). Provider is ACTIVE on a resource
        // fact alone even with no client and no symbols.
        var facts = new List<StructuralFactRecord> { ResourceFact("sf-users", ":users", "collection") };

        var graph = BridgeGraphBuilder.Build(
            [], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(8, graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]);
    }

    [Theory]
    [InlineData("[\"index\", \"show\"]")]     // JSON, no leading colon
    [InlineData("[\":index\", \":show\"]")]   // JSON, ruby symbol leading colon
    [InlineData("[:index, :show]")]            // raw ruby array (tolerant fallback)
    public void BackendHttp_rails_resources_only_filter_keeps_two(string onlyRaw)
    {
        // Invariant: `only:` keeps ONLY the listed actions; parsing tolerates JSON with/without leading ':'
        // and a raw ruby array — so Task 7's live extract shape cannot surprise the filter.
        var facts = new List<StructuralFactRecord>
        {
            ResourceFact("sf-users", ":users", "collection",
                new Dictionary<string, string> { ["only"] = onlyRaw }),
        };

        var graph = BridgeGraphBuilder.Build(
            [], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Equal(2, graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]);
    }

    [Fact]
    public void BackendHttp_rails_resources_except_filter_drops_destroy()
    {
        // Invariant: `except: [:destroy]` drops the destroy action (one entry) → 7 remaining; behaviorally, a
        // DELETE client to /users/42 no longer joins (the destroy route is gone).
        var clientFn = Type("sym-client-fn", "removeUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            ResourceFact("sf-users", ":users", "collection",
                new Dictionary<string, string> { ["except"] = "[\":destroy\"]" }),
            Fact("sf-client-del", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/users/42",
                    ["url_kind"] = "path",
                    ["verb"] = "DELETE",
                    ["verb_source"] = "literal",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Equal(7, graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]);
        Assert.DoesNotContain(graph.Edges, e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void BackendHttp_rails_resource_singular_expands_to_seven_no_index_no_id()
    {
        // Invariant: `resource :profile` (singular) expands to 7 entries (no index, no :id member routes); a
        // GET client to /profile joins show at High, and there is NO /profile/:id (a GET /profile/42 misses).
        var clientShow = Type("sym-client-show", "loadProfile", "function", file: "web/api.ts");
        var clientMember = Type("sym-client-member", "loadOther", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            ResourceFact("sf-profile", ":profile", "singular"),
            Fact("sf-client-show", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-show", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/profile",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
            Fact("sf-client-member", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-member", 110,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/profile/42",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientShow, clientMember], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Equal(7, graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]);
        var hit = Assert.Single(graph.Incident("sym-client-show"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        // No /profile/:id member route exists for a singular resource → the /profile/42 client joins nothing.
        Assert.DoesNotContain(graph.Incident("sym-client-member"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void BackendHttp_rails_scope_path_prefixes_every_expanded_route()
    {
        // Invariant: `scope_path="/admin"` prefixes every expanded path (JoinRoute) → a GET client to
        // /admin/users/42 joins the show route (which canonically folds :id to match 42).
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            ResourceFact("sf-users", ":users", "collection",
                new Dictionary<string, string> { ["scope_path"] = "/admin" }),
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/admin/users/42",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Equal(8, graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]);
        var hit = Assert.Single(graph.Incident("sym-client-fn"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
    }

    [Fact]
    public void BackendHttp_rails_resource_verb_specific_join_binds_to_plural_controller_methods()
    {
        // Invariant: expanded routes are VERB-KNOWN and bind to the PLURAL controller. `resources :users` +
        // UsersController.{show,destroy} → a GET /users/42 client edges to show; a DELETE /users/42 client edges
        // to destroy; both High; both target the controller METHOD symbol id (canonical :id↔42 fold). Also
        // proves resource_name leading-colon stripping affects the path (":users" → /users/...).
        var show = Method("sym-users-show", "show", "show", "UsersController", "app/controllers/users_controller.rb");
        var destroy = Method("sym-users-destroy", "destroy", "destroy", "UsersController", "app/controllers/users_controller.rb");
        var clientGet = Type("sym-client-get", "loadUser", "function", file: "web/api.ts");
        var clientDel = Type("sym-client-del", "removeUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            ResourceFact("sf-users", ":users", "collection"),
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-get", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/users/42", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
            Fact("sf-client-del", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-del", 110,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/users/42", ["url_kind"] = "path",
                    ["verb"] = "DELETE", ["verb_source"] = "literal",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [show, destroy, clientGet, clientDel], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var showHit = Assert.Single(graph.Incident("sym-users-show"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, showHit.Band);
        Assert.Equal("sym-client-get", showHit.Edge.SourceRef.SymbolId);
        Assert.Equal("sym-users-show", showHit.Edge.TargetRef.SymbolId);

        var destroyHit = Assert.Single(graph.Incident("sym-users-destroy"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, destroyHit.Band);
        Assert.Equal("sym-client-del", destroyHit.Edge.SourceRef.SymbolId);
        Assert.Equal("sym-users-destroy", destroyHit.Edge.TargetRef.SymbolId);
    }

    [Fact]
    public void BackendHttp_rails_route_controller_action_binds_to_controller_method()
    {
        // Invariant: a rails.route.v1 fact carrying controller_action="users#show" REBINDS the handler endpoint to
        // UsersController.show (CamelCase(controller)+Controller, no inflection) — controller_action IS receiver
        // identity — so the edge targets that method symbol id.
        var show = Method("sym-users-show", "show", "show", "UsersController", "app/controllers/users_controller.rb");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-rails-route", "rails.route.v1", "ruby", "config/routes.rb", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["api_style"] = "dsl_routing",
                    ["route_template"] = "/users/:id",
                    ["normalized_route_template"] = "/users/:id",
                    ["verb"] = "GET",
                    ["controller_action"] = "users#show",
                }),
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/users/42", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [show, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-users-show"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal("sym-users-show", hit.Edge.TargetRef.SymbolId);
    }

    [Fact]
    public void BackendHttp_rails_route_controller_action_absent_controller_synthesizes_endpoint_edge_still_emitted()
    {
        // Invariant: the same rails.route.v1 controller_action fact with NO matching controller symbol falls back
        // to a synthesized Endpoint node (target SymbolId null) — the edge is STILL emitted at High.
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-rails-route", "rails.route.v1", "ruby", "config/routes.rb", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["api_style"] = "dsl_routing",
                    ["route_template"] = "/users/:id",
                    ["normalized_route_template"] = "/users/:id",
                    ["verb"] = "GET",
                    ["controller_action"] = "users#show",
                }),
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/users/42", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-client-fn"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Null(hit.Edge.TargetRef.SymbolId);
    }

    [Fact]
    public void BackendHttp_rails_resource_singular_binds_plural_controller_never_singular_decoy()
    {
        // Invariant: singular `resource :profile` maps to the PLURAL ProfilesController (pluralize then CamelCase +
        // Controller). A GET /profile client binds to ProfilesController.show; the singular ProfileController.show
        // decoy is NEVER bound.
        var plural = Method("sym-profiles-show", "show", "show", "ProfilesController", "app/controllers/profiles_controller.rb");
        var decoy = Method("sym-profile-show", "show", "show", "ProfileController", "app/controllers/profile_controller.rb");
        var clientFn = Type("sym-client-fn", "loadProfile", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            ResourceFact("sf-profile", ":profile", "singular"),
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/profile", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [plural, decoy, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-profiles-show"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal("sym-profiles-show", hit.Edge.TargetRef.SymbolId);
        Assert.DoesNotContain(graph.Incident("sym-profile-show"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void BackendHttp_rails_resource_ambiguous_controller_method_does_not_bind_falls_back_to_endpoint()
    {
        // Invariant (unambiguous-or-nothing): two non-test UsersController.show methods (two files) POISON the
        // lookup → the expanded show route does NOT bind, falling back to a synthesized Endpoint node (target
        // SymbolId null) — the High edge is still emitted, never bound on similarity.
        var showA = Method("sym-users-show-a", "show", "show", "UsersController", "app/controllers/a/users_controller.rb");
        var showB = Method("sym-users-show-b", "show", "show", "UsersController", "app/controllers/b/users_controller.rb");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            ResourceFact("sf-users", ":users", "collection"),
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/users/42", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [showA, showB, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.DoesNotContain(graph.Incident("sym-users-show-a"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.DoesNotContain(graph.Incident("sym-users-show-b"), e => e.Edge.Kind == BridgeKind.Hits);
        var hit = Assert.Single(graph.Incident("sym-client-fn"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Null(hit.Edge.TargetRef.SymbolId);
    }

    // ============================ backend-http provider — julie-extractors 2.8.0 wave 2 =========================
    // Six more backend stacks join through the SAME machinery: plain route families (NestJS/Laravel/Phoenix/axum +
    // both actix provenances) read via TryReadBackendRoute; laravel/phoenix resource declarations expand like Rails;
    // axum.nest / actix.mount / phoenix.forward compose cross-file prefixes; laravel.route_prefix is target-less
    // evidence. Kotlin+Spring routes and the four new client languages reuse existing ids (no test needed here — a
    // spring.request_mapping.v1 route and an http.client_request.v1 call already have coverage above).

    [Theory]
    [InlineData("nestjs.route.v1", "typescript")]
    [InlineData("laravel.route.v1", "php")]
    [InlineData("phoenix.route.v1", "elixir")]
    [InlineData("axum.route.v1", "rust")]
    [InlineData("actix.attribute_route.v1", "rust")]
    public void BackendHttp_v280_plain_route_family_hits_client_High(string patternId, string language)
    {
        // Invariant (language parity): each new plain route family carries normalized_route_template and joins a
        // verb-equal client request at High, bound to the route fact's handler symbol — with no family-specific read.
        var handler = Method("sym-handler", "handle", "handle()", string.Empty, "server/app_routes");
        var clientFn = Type("sym-client-fn", "load", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch",
                    ["target_path"] = "/api/widgets",
                    ["url_kind"] = "path",
                    ["verb"] = "GET",
                    ["verb_source"] = "attested",
                }),
            Fact("sf-route", patternId, language, "server/app_routes", "sym-handler", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/api/widgets",
                    ["verb"] = "GET",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-handler"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
        Assert.Equal("sym-handler", hit.Edge.TargetRef.SymbolId);
        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.routeFacts"]);
    }

    [Fact]
    public void BackendHttp_actix_scope_route_prefers_effective_template_over_scope_local()
    {
        // Invariant: actix.scope_route.v1 always carries route_group_prefix + effective_route_template (the scope
        // prefix folded). TryReadBackendRoute prefers effective_route_template, so a client to the ABSOLUTE path
        // joins — the scope-local normalized_route_template (/users) alone would MISS /api/users.
        var handler = Method("sym-actix", "create", "create()", string.Empty, "src/routes.rs");
        var clientFn = Type("sym-client-fn", "create", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/api/users", ["url_kind"] = "path",
                    ["verb"] = "POST", ["verb_source"] = "attested",
                }),
            Fact("sf-actix-scope", "actix.scope_route.v1", "rust", "src/routes.rs", "sym-actix", 200,
                new Dictionary<string, string>
                {
                    ["route_group_prefix"] = "/api",
                    ["route_template"] = "/users",
                    ["normalized_route_template"] = "/users",
                    ["effective_route_template"] = "/api/users",
                    ["verb"] = "POST",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-actix"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
    }

    [Theory]
    [InlineData("axum.nest.v1", "rust")]
    [InlineData("actix.mount.v1", "rust")]
    [InlineData("phoenix.forward.v1", "elixir")]
    public void StructuralRouteFactAdapter_TryReadMountFact_ReadsV280TargetedMountFamilies(string patternId, string language)
    {
        var fact = Fact("sf-mount", patternId, language, "src/app", "sym-app", 100,
            new Dictionary<string, string>
            {
                ["mount_path"] = "/admin",
                ["normalized_mount_path"] = "/admin",
                ["mount_target"] = "admin_routes",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadMountFact(fact, new Dictionary<string, SymbolDetail>(), out var mount));
        Assert.Equal("/admin", mount.MountPath);
        Assert.Equal("admin_routes", mount.MountTarget);
    }

    [Fact]
    public void StructuralRouteFactAdapter_TryReadMountFact_LaravelRoutePrefixReadsWithNoTarget()
    {
        // Invariant: laravel.route_prefix.v1 (a Route::prefix(...)->group closure) carries mount_path only — no
        // same-file named mount_target. It reads as a mount fact with an EMPTY target, so it composes nothing
        // cross-file (evidence-only); its same-file effect already lives on the route facts' effective_route_template.
        var fact = Fact("sf-prefix", "laravel.route_prefix.v1", "php", "routes/web.php", string.Empty, 100,
            new Dictionary<string, string>
            {
                ["mount_path"] = "/admin",
                ["normalized_mount_path"] = "/admin",
            });

        Assert.True(StructuralRouteFactAdapter.TryReadMountFact(fact, new Dictionary<string, SymbolDetail>(), out var mount));
        Assert.Equal("/admin", mount.MountPath);
        Assert.Equal(string.Empty, mount.MountTarget);
    }

    [Theory]
    [InlineData("api_routes()", "api_routes")]  // bare target imported via `use crate::api::api_routes;`
    [InlineData("api::routes()", "routes")]     // idiomatic Rust path-qualified target (review finding #3: '::' must split)
    public void BackendHttp_axum_nest_composes_prefixed_route_High(string mountTarget, string routerFnName)
    {
        // Invariant: axum .route("/{id}") in src/api.rs (the nested router fn also defined there) + .nest("/api", <target>)
        // in src/main.rs → composed /api/:id joins fetch("/api/42") at High, bound to the route's handler symbol. The
        // path-qualified form api::routes() must anchor on the fn's leaf name ("routes"), not the whole "api::routes".
        var handler = Method("sym-axum-show", "show", "show()", string.Empty, "src/api.rs");
        var routerFn = Type("sym-api-routes", routerFnName, "function", file: "src/api.rs");
        var appSym = Type("sym-app", "app", "variable", file: "src/main.rs");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/api/42", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
            Fact("sf-axum-route", "axum.route.v1", "rust", "src/api.rs", "sym-axum-show", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/:id",
                    ["verb"] = "GET",
                }),
            Fact("sf-axum-nest", "axum.nest.v1", "rust", "src/main.rs", "sym-app", 300,
                new Dictionary<string, string>
                {
                    ["normalized_mount_path"] = "/api",
                    ["mount_target"] = mountTarget,
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, routerFn, appSym, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-axum-show"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal("sym-axum-show", hit.Edge.TargetRef.SymbolId);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
    }

    [Theory]
    [InlineData("admin_config", "admin_config")]  // bare configure target
    [InlineData("config::admin", "admin")]        // path-qualified configure target (review finding #3: '::' must split)
    public void BackendHttp_actix_mount_composes_attribute_route_High(string mountTarget, string configFnName)
    {
        // Invariant: actix web::scope("/admin").configure(<target>) mounts the attribute-routed handlers the
        // configured fn registers → composed /admin/users joins fetch POST /admin/users at High. The path-qualified
        // form config::admin must anchor on the fn's leaf name ("admin"), not the whole "config::admin".
        var handler = Method("sym-actix-create", "create", "create()", string.Empty, "src/admin.rs");
        var configFn = Type("sym-admin-config", configFnName, "function", file: "src/admin.rs");
        var appSym = Type("sym-app", "app", "variable", file: "src/main.rs");
        var clientFn = Type("sym-client-fn", "createUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-post", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/admin/users", ["url_kind"] = "path",
                    ["verb"] = "POST", ["verb_source"] = "attested",
                }),
            Fact("sf-actix-attr", "actix.attribute_route.v1", "rust", "src/admin.rs", "sym-actix-create", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/users",
                    ["verb"] = "POST",
                }),
            Fact("sf-actix-mount", "actix.mount.v1", "rust", "src/main.rs", "sym-app", 300,
                new Dictionary<string, string>
                {
                    ["normalized_mount_path"] = "/admin",
                    ["mount_target"] = mountTarget,
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, configFn, appSym, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-actix-create"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
    }

    [Theory]
    [InlineData("MyAppWeb.AdminRouter")]  // namespaced module — real Phoenix (review finding #2: leaf-strip would miss)
    [InlineData("HealthPlug")]            // bare single-segment module still anchors (name == target)
    public void BackendHttp_phoenix_forward_composes_prefixed_route_High(string moduleName)
    {
        // Invariant: phoenix forward "/admin", <Module> + a phoenix.route "/dashboard" in that module's file →
        // composed /admin/dashboard joins fetch GET /admin/dashboard at High. Elixir names the module symbol by its
        // FULL dotted alias, so anchoring must match the whole mount_target, not just its leaf segment.
        var handler = Method("sym-dash", "dashboard", "dashboard()", string.Empty, "lib/admin_router.ex");
        var routerSym = Type("sym-admin-router", moduleName, "module", file: "lib/admin_router.ex");
        var clientFn = Type("sym-client-fn", "openDashboard", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/admin/dashboard", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
            Fact("sf-phoenix-route", "phoenix.route.v1", "elixir", "lib/admin_router.ex", "sym-dash", 200,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/dashboard",
                    ["verb"] = "GET",
                }),
            Fact("sf-phoenix-forward", "phoenix.forward.v1", "elixir", "lib/router.ex", string.Empty, 300,
                new Dictionary<string, string>
                {
                    ["normalized_mount_path"] = "/admin",
                    ["mount_target"] = moduleName,
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, routerSym, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-dash"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
    }

    [Fact]
    public void BackendHttp_laravel_route_prefix_is_target_less_evidence_composes_nothing()
    {
        // Invariant: laravel.route_prefix.v1 carries no mount_target, so it can never anchor cross-file — it is
        // counted as an unanchored mount and composes nothing. A same-file direct laravel route is unaffected.
        var handler = Method("sym-dash", "index", "index()", string.Empty, "routes/web.php");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-laravel-route", "laravel.route.v1", "php", "routes/web.php", "sym-dash", 100,
                new Dictionary<string, string>
                {
                    ["normalized_route_template"] = "/dashboard",
                    ["verb"] = "GET",
                }),
            Fact("sf-laravel-prefix", "laravel.route_prefix.v1", "php", "routes/web.php", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["mount_path"] = "/admin",
                    ["normalized_mount_path"] = "/admin",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.mounts"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["backend-http.composedRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["backend-http.unanchoredMounts"]);
    }

    [Fact]
    public void BackendHttp_laravel_resource_expands_to_eight_and_index_joins_client()
    {
        // Invariant: Route::resource('photos', ...) expands to 8 verb entries (update answers PUT AND PATCH); a GET
        // client to /photos joins the index route (High, verb-known).
        var clientFn = Type("sym-client-fn", "listPhotos", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-photos", "laravel.resource_route.v1", "php", "routes/web.php", string.Empty, 200,
                new Dictionary<string, string> { ["resource_name"] = "photos", ["resource_kind"] = "resource" }),
            Fact("sf-client-index", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/photos", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(8, graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]);
        var hit = Assert.Single(graph.Incident("sym-client-fn"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
    }

    [Fact]
    public void BackendHttp_laravel_api_resource_expands_to_six_no_create_no_edit()
    {
        // Invariant: Route::apiResource drops the HTML-form-only create+edit routes → 6 verb entries.
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-photos", "laravel.resource_route.v1", "php", "routes/api.php", string.Empty, 200,
                new Dictionary<string, string> { ["resource_name"] = "photos", ["resource_kind"] = "api_resource" }),
        };

        var graph = BridgeGraphBuilder.Build(
            [], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Equal(6, graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]);
    }

    [Fact]
    public void BackendHttp_laravel_resource_binds_namespaced_controller_action_method()
    {
        // Invariant: the resource fact's namespaced `controller` reference resolves by its LEAF name to the action
        // method symbol; a GET /photos client binds to PhotoController::index.
        var indexMethod = Method("sym-photo-index", "index", "index()", "PhotoController", "app/Http/Controllers/PhotoController.php");
        var clientFn = Type("sym-client-fn", "listPhotos", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-photos", "laravel.resource_route.v1", "php", "routes/web.php", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["resource_name"] = "photos",
                    ["resource_kind"] = "resource",
                    ["controller"] = "App\\Http\\Controllers\\PhotoController",
                }),
            Fact("sf-client-index", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/photos", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [indexMethod, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-photo-index"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.Equal("sym-photo-index", hit.Edge.TargetRef.SymbolId);
    }

    [Fact]
    public void BackendHttp_phoenix_resources_expands_to_eight_and_index_joins_client()
    {
        // Invariant: resources "/users", UserController expands to 8 verb entries off the base PATH
        // (normalized_resource_path); a GET client to /users joins index at High.
        var clientFn = Type("sym-client-fn", "listUsers", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-users", "phoenix.resource_route.v1", "elixir", "lib/router.ex", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["resource_path"] = "/users",
                    ["normalized_resource_path"] = "/users",
                    ["controller"] = "UserController",
                }),
            Fact("sf-client-index", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/users", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Contains("backend-http", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(8, graph.CapabilityReport.EvidenceCounts["backend-http.expandedResourceRoutes"]);
        var hit = Assert.Single(graph.Incident("sym-client-fn"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
    }

    [Fact]
    public void BackendHttp_phoenix_scoped_resources_prefix_once_not_twice()
    {
        // Invariant (regression, review finding #1): a scoped `scope "/api" do resources "/users", UserController end`
        // emits phoenix.resource_route.v1 with normalized_resource_path ALREADY folding in the scope ("/api/users")
        // AND route_group_prefix="/api" (contract: normalized_resource_path "including same-file scope prefix"). The
        // expander must prefix ONCE: routes live at /api/users…, NOT /api/api/users…. A client GET /api/users joins
        // index at High; /api/api/users (the double-prefix bug) and bare /users join nothing.
        var clientPrefixed = Type("sym-client-prefixed", "listApiUsers", "function", file: "web/api.ts");
        var clientDouble = Type("sym-client-double", "listDoubled", "function", file: "web/api.ts");
        var clientBare = Type("sym-client-bare", "listUsers", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-users", "phoenix.resource_route.v1", "elixir", "lib/router.ex", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["resource_path"] = "/users",
                    ["normalized_resource_path"] = "/api/users", // scope already folded in (per contract + extractor)
                    ["route_group_prefix"] = "/api",
                    ["controller"] = "UserController",
                }),
            Fact("sf-client-prefixed", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-prefixed", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/api/users", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
            Fact("sf-client-double", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-double", 105,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/api/api/users", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
            Fact("sf-client-bare", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-bare", 110,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/users", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientPrefixed, clientDouble, clientBare], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Single(graph.Incident("sym-client-prefixed"), e => e.Edge.Kind == BridgeKind.Hits);
        // The double-prefix bug would have put every route under /api/api — a client there must NOT join.
        Assert.DoesNotContain(graph.Incident("sym-client-double"), e => e.Edge.Kind == BridgeKind.Hits);
        // The bare /users no longer exists — every route is under /api — so it joins nothing.
        Assert.DoesNotContain(graph.Incident("sym-client-bare"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void BackendHttp_phoenix_scoped_resources_raw_path_fallback_applies_prefix_once()
    {
        // Invariant: when normalized_resource_path is absent, the expander falls back to the RAW resource_path (no
        // scope folded) and applies route_group_prefix exactly once → /api/users. Guards the else-branch of the fix.
        var clientPrefixed = Type("sym-client-prefixed", "listApiUsers", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-users", "phoenix.resource_route.v1", "elixir", "lib/router.ex", string.Empty, 200,
                new Dictionary<string, string>
                {
                    ["resource_path"] = "/users",
                    ["route_group_prefix"] = "/api",
                    ["controller"] = "UserController",
                }),
            Fact("sf-client-prefixed", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-prefixed", 100,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/api/users", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "default",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [clientPrefixed], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        Assert.Single(graph.Incident("sym-client-prefixed"), e => e.Edge.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void BackendHttp_laravel_prefix_group_optional_param_joins_High()
    {
        // Invariant (regression, review finding #4): a Laravel route with an OPTIONAL param inside a prefix group
        // (`Route::prefix('admin')->group(fn: Route::get('/users/{id?}', ...))`) emits effective_route_template
        // "/admin/users/{id?}" (raw '?' preserved) alongside normalized "/admin/users/:id". TryReadBackendRoute
        // prefers effective, so the matcher must treat the brace-optional "{id?}" as a dynamic segment — a client
        // GET /admin/users/42 joins at High rather than being truncated to an unmatchable literal.
        var handler = Method("sym-show", "show", "show()", string.Empty, "routes/web.php");
        var clientFn = Type("sym-client-fn", "loadUser", "function", file: "web/api.ts");
        var facts = new List<StructuralFactRecord>
        {
            Fact("sf-laravel-route", "laravel.route.v1", "php", "routes/web.php", "sym-show", 100,
                new Dictionary<string, string>
                {
                    ["effective_route_template"] = "/admin/users/{id?}",
                    ["normalized_route_template"] = "/admin/users/:id",
                    ["route_group_prefix"] = "/admin",
                    ["verb"] = "GET",
                }),
            Fact("sf-client-get", "http.client_request.v1", "typescript", "web/api.ts", "sym-client-fn", 200,
                new Dictionary<string, string>
                {
                    ["client"] = "fetch", ["target_path"] = "/admin/users/42", ["url_kind"] = "path",
                    ["verb"] = "GET", ["verb_source"] = "attested",
                }),
        };

        var graph = BridgeGraphBuilder.Build(
            [handler, clientFn], typeArguments: [], literals: [], annotations: [], dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-show"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
    }
}
