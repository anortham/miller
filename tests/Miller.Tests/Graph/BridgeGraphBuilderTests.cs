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
        var skipped = Assert.Single(graph.CapabilityReport.SkippedProviders);
        Assert.Equal("nextjs", skipped.ProviderId);
        Assert.Contains("no nextjs bridge evidence", skipped.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.routeReferences"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.fileRoutes"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.ambiguousMatches"]);
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
        Assert.Equal(ConfidenceBand.High, hits.Band);
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
    [InlineData("vue.route_definition.v1", "vue", "web/router.ts", "vue.router", "target_path", "/calendar")]
    [InlineData("react.route_reference.v1", "tsx", "web/App.tsx", "react.link", "target_path", "/calendar")]
    [InlineData("react.route_definition.v1", "tsx", "web/routes.tsx", "react.routes", "route_path", "/calendar")]
    [InlineData("nextjs.route_reference.v1", "tsx", "web/nav.tsx", "next.link", "target_path", "/calendar")]
    [InlineData("nextjs.file_route.v1", "tsx", "web/app/calendar/page.tsx", "next.page", "route_path", "/calendar")]
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
        Assert.Equal(ConfidenceBand.High, hits.Band);
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
    public void StructuralRouteFactAdapter_TryReadRouteReference_ReadsRouteAndDefaultVerb()
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
        Assert.Equal("GET", reference.Verb);
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
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs.routeReferences"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs.fileRoutes"]);
        Assert.Equal(1, graph.CapabilityReport.EvidenceCounts["nextjs.candidates"]);
        Assert.Equal(0, graph.CapabilityReport.EvidenceCounts["nextjs.ambiguousMatches"]);
        Assert.Contains(graph.Nodes.Values, node =>
            node.Kind == BridgeNodeKind.NextRoute &&
            node.Display == "/settings" &&
            node.FilePath == "web/app/settings/page.tsx" &&
            node.Line == 1);
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
            node.Kind == BridgeNodeKind.NextRoute &&
            node.Display == "/settings" &&
            node.FilePath == "web/app/settings/page.tsx" &&
            node.Line == 1);
    }

    [Fact]
    public void NextRouteBridge_StaticReference_YieldsNavigatesToEdge()
    {
        var edges = NextRouteBridge.Resolve(
            [NextRouteReference("/settings")],
            [NextFileRoute("/settings", "web/app/settings/page.tsx")]);

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
    public void NextRouteBridge_DynamicReference_YieldsHighConfidenceEdge(string fileRoute)
    {
        var edge = Assert.Single(NextRouteBridge.Resolve(
            [NextRouteReference("/users/123")],
            [NextFileRoute(fileRoute, "web/app/users/[id]/page.tsx")]));

        var scored = BridgeScorer.Score(edge);

        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.Equal(fileRoute, edge.TargetRef.Display);
    }

    [Fact]
    public void NextRouteBridge_CatchAllRequiresAtLeastOneTrailingSegment()
    {
        var edges = NextRouteBridge.Resolve(
            [
                NextRouteReference("/docs", "next.docs.index"),
                NextRouteReference("/docs/a/b", "next.docs.deep"),
            ],
            [NextFileRoute("/docs/[...slug]", "web/app/docs/[...slug]/page.tsx")]);

        var edge = Assert.Single(edges);
        Assert.Equal("/docs/a/b", edge.SourceRef.Display);
        Assert.Equal("/docs/[...slug]", edge.TargetRef.Display);
    }

    [Fact]
    public void NextRouteBridge_OptionalCatchAllMatchesZeroOrMoreTrailingSegments()
    {
        var edges = NextRouteBridge.Resolve(
            [
                NextRouteReference("/docs", "next.docs.index"),
                NextRouteReference("/docs/a/b", "next.docs.deep"),
            ],
            [NextFileRoute("/docs/[[...slug]]", "web/app/docs/[[...slug]]/page.tsx")]);

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

    [Fact]
    public void NextRouteBridge_RouteGroupSegmentsDoNotParticipateInMatching()
    {
        var edge = Assert.Single(NextRouteBridge.Resolve(
            [NextRouteReference("/settings")],
            [NextFileRoute("/(admin)/settings", "web/app/(admin)/settings/page.tsx")]));

        Assert.Equal("/settings", edge.SourceRef.Display);
        Assert.Equal("/(admin)/settings", edge.TargetRef.Display);
    }

    [Fact]
    public void NextRouteBridge_AmbiguousFileRouteMatchesEmitNoEdge()
    {
        var edges = NextRouteBridge.Resolve(
            [NextRouteReference("/settings")],
            [
                NextFileRoute("/settings", "web/app/settings/page.tsx", "sf-next-settings-app"),
                NextFileRoute("/(admin)/settings", "web/app/(admin)/settings/page.tsx", "sf-next-settings-admin"),
            ]);

        Assert.Empty(edges);
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
        Assert.Equal(ConfidenceBand.High, hits.Band);
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
}
