using Miller.Core.Contracts;
using Miller.Core.Graph;
using Miller.Core.Resolver;
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
    public void Build_DefaultProvider_ReportsDotnetWebCapability()
    {
        var symbols = new List<SymbolDetail> { Type("sym-appsetting", "AppSetting", "class", "Domain") };
        var dbSets = new List<DbSetProperty> { DbSet("AppSettings", "AppSetting") };

        var graph = BridgeGraphBuilder.Build(symbols, [], [], [], dbSets);

        Assert.Contains("dotnet-web", graph.CapabilityReport.ActiveProviders);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["dotnet-web.dbsets"]);
        Assert.Empty(graph.CapabilityReport.SkippedProviders);
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
                metadataJson: """{"source_kind":"RouterLink","attribute_name":"to","verb":"GET","target_path":"/todos"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [mapGet, vueNode],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-mapget"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
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
                metadataJson: """{"source_kind":"bound_attribute","attribute_name":":to","expression":"'\/todos'","verb":"GET","target_path":"/todos"}"""),
        };

        var graph = BridgeGraphBuilder.Build(
            [mapGet, vueNode],
            typeArguments: [],
            literals: [],
            annotations: [],
            dbSetProperties: [],
            structuralFacts: facts);

        var hit = Assert.Single(graph.Incident("sym-mapget"), e => e.Edge.Kind == BridgeKind.Hits);
        Assert.Equal(ConfidenceBand.High, hit.Band);
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
            StartLine: startLine,
            StartColumn: 1,
            EndLine: startLine,
            EndColumn: 1,
            Span: new SourceSpan(startLine * 10, startLine * 10 + 1),
            Confidence: 1.0,
            MetadataJson: metadataJson);
}
