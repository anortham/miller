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

        // One edge per (client, endpoint) pair — and the HIGHER band survives the collapse.
        var hit = Assert.Single(graph.Incident("sym-list"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
        Assert.False(hit.IsVerbUnknown);
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
}
