using Miller.Core.Contracts;
using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Resolver;

/// <summary>
/// Pins design §4 Leg 1 (<see cref="RouteBridge"/>): TS client HTTP call ⇄ C# controller endpoint
/// (<see cref="BridgeKind.Hits"/>), plus the endpoint's response DTO (<see cref="BridgeKind.Responds"/>) and
/// request DTO (<see cref="BridgeKind.Consumes"/>). Every fixture is hand-built in-memory (NO julie, NO I/O), so
/// these are fast-suite tests. The leg only builds candidates and delegates ALL confidence to
/// <see cref="BridgeScorer"/>; the tests assert the resulting band/score and the load-bearing traps:
/// <list type="bullet">
/// <item>an <c>axios.&lt;verb&gt;</c> client + a matching endpoint => a <see cref="SignalRule.RouteVerbMatch"/> Hits
///   edge at High (after <c>[controller]</c>/<c>[action]</c> token expansion, design §8 risk 3);</item>
/// <item>a verb-less carrier (<c>fetch</c>) that matches on route alone => a <see cref="SignalRule.RouteOnlyMatch"/>
///   Hits edge at Medium, flagged verb-unknown, with the verb NEVER assumed GET;</item>
/// <item>two controllers' <c>{id}</c> templates normalize to distinct routes — no false merge into one Hits edge;</item>
/// <item>an endpoint return type unwrapping to a named DTO => a <see cref="SignalRule.ReturnTypeDto"/> Responds edge;
///   a bare <c>ActionResult</c> => NO Responds edge;</item>
/// <item>a <c>[FromBody]</c> request DTO => a <see cref="SignalRule.FromBodyDto"/> Consumes edge;</item>
/// <item>a C# test HttpClient url literal is excluded from the TS side.</item>
/// </list>
/// </summary>
public sealed class RouteBridgeTests
{
    // SymbolDetail ctor order: (Id, Name, Kind, FilePath, Signature, Namespace, TestRole, ParentClassName).
    private static SymbolDetail Dto(string id, string name, string? ns = "Dtos", string file = "Api/Dtos/Dtos.cs") =>
        new(id, name, "class", file, $"public class {name}", ns, null, null);

    // A TS client call. Defaults to the real-data shape: a typescript url literal at arg 0, axios.<verb> carrier.
    private static TsClientCall Call(
        string carrier,
        string route,
        string language = "typescript",
        TestRole? testRole = null,
        string file = "web/src/api/client.ts",
        int line = 12,
        string containingSymbolId = "ts.fn") =>
        new(
            new LiteralRecord(route, "url", carrier, 0, language, containingSymbolId, new SourceSpan(0, route.Length)),
            testRole,
            file,
            line);

    // A C# controller endpoint. Defaults to the real-data shape: Route("api/[controller]") on the class + an HttpVerb
    // annotation on the method, a concrete return type, no request body.
    private static ControllerEndpoint Endpoint(
        string verbKey,
        string parentClassName,
        string methodName,
        string? classRoute = "api/[controller]",
        string? methodRoute = null,
        string returnType = "Task<ActionResult>",
        string? requestBodyType = null,
        string file = "Api/Controllers/Controller.cs",
        int line = 30,
        string symbolId = "cs.endpoint") =>
        new(symbolId, verbKey, classRoute, methodRoute, parentClassName, methodName, returnType, requestBodyType, file, line);

    // ---- Hits: verb-known (axios.<verb>) -----------------------------------------------------------------------

    [Fact]
    public void Resolve_AxiosGet_MatchingEndpoint_EmitsRouteVerbMatchHits_High()
    {
        // axios.get /api/appsettings/${id}  ⇄  HttpGet("{id}") on AppSettingsController under Route("api/[controller]")
        // Both normalize to (GET, api/appsettings/{}) => a RouteVerbMatch Hits edge at High.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [Call("axios.get", "/api/appsettings/${id}")],
            [Endpoint("httpget", "AppSettingsController", "GetById", methodRoute: "{id}")]);

        var edges = RouteBridge.Resolve(input, resolver);

        var hits = Assert.Single(edges, e => e.Kind == BridgeKind.Hits);
        Assert.Contains(hits.Signals, s => s is StructuralSignal { Rule: SignalRule.RouteVerbMatch, Present: true });
        Assert.DoesNotContain(hits.Signals, s => s is StructuralSignal { Rule: SignalRule.RouteOnlyMatch, Present: true });
        Assert.Equal("api/appsettings/{}", hits.TargetRef.Display);

        var scored = BridgeScorer.Score(hits);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
        Assert.True(scored.Score >= 0.90);
        Assert.False(scored.IsVerbUnknown);
        Assert.False(scored.HasAmbiguousName);
    }

    [Fact]
    public void Resolve_VerbMismatch_NoHitsEdge()
    {
        // Same route, but the client POSTs and the endpoint is GET => the (verb, route) pair does not match, so no
        // Hits edge (a verb-known mismatch is a real distinction, not a route-only fallback).
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [Call("axios.post", "/api/appsettings/${id}")],
            [Endpoint("httpget", "AppSettingsController", "GetById", methodRoute: "{id}")]);

        var edges = RouteBridge.Resolve(input, resolver);

        Assert.DoesNotContain(edges, e => e.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void Resolve_RouteMismatch_NoHitsEdge()
    {
        // A near-miss route ("/api/appsettings" vs the endpoint's "{id}" tail) must NOT match.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [Call("axios.get", "/api/appsettings")],
            [Endpoint("httpget", "AppSettingsController", "GetById", methodRoute: "{id}")]);

        var edges = RouteBridge.Resolve(input, resolver);

        Assert.DoesNotContain(edges, e => e.Kind == BridgeKind.Hits);
    }

    // ---- Hits: verb-unknown (fetch / verb-less carriers) -------------------------------------------------------

    [Fact]
    public void Resolve_FetchVerbUnknown_RouteMatches_EmitsRouteOnlyMatch_Medium_NeverAssumesGet()
    {
        // A verb-less carrier (fetch): the route lines up but the verb is unknown. The edge matches on route ALONE =>
        // a RouteOnlyMatch Hits edge at Medium, flagged IsVerbUnknown, and the verb is NEVER assumed to be GET — even
        // though the endpoint it matched IS a GET.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [Call("fetch", "/api/appsettings")],
            [Endpoint("httpget", "AppSettingsController", "List")]);

        var hits = Assert.Single(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Hits);

        Assert.Contains(hits.Signals, s => s is StructuralSignal { Rule: SignalRule.RouteOnlyMatch, Present: true });
        Assert.DoesNotContain(hits.Signals, s => s is StructuralSignal { Rule: SignalRule.RouteVerbMatch, Present: true });
        // The TS side display is the canonical route; it carries no fabricated verb.
        Assert.Equal("api/appsettings", hits.SourceRef.Display);

        var scored = BridgeScorer.Score(hits);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.Medium, scored!.Band);
        Assert.True(scored.IsVerbUnknown);
        Assert.InRange(scored.Score, 0.70, 0.85);
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("$fetch")]
    [InlineData("ofetch")]
    [InlineData("axios")]
    [InlineData("ky")]
    [InlineData("got")]
    public void Resolve_VerbLessCarriers_AllRouteOnly(string carrier)
    {
        // Every julie verb-less carrier yields a route-only (verb-unknown) match, never a RouteVerbMatch.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [Call(carrier, "/api/appsettings")],
            [Endpoint("httpget", "AppSettingsController", "List")]);

        var hits = Assert.Single(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Hits);
        Assert.Contains(hits.Signals, s => s is StructuralSignal { Rule: SignalRule.RouteOnlyMatch, Present: true });
        Assert.DoesNotContain(hits.Signals, s => s is StructuralSignal { Rule: SignalRule.RouteVerbMatch, Present: true });
    }

    // ---- collision guard ---------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_TwoControllersSameMethodTemplate_NoFalseMerge_EachMatchesOnlyItsClient()
    {
        // THE collision guard (design §8 risk 3). Two controllers each expose HttpGet("{id}") under
        // Route("api/[controller]"); two clients hit each one. The {id} templates must normalize to DISTINCT routes,
        // so each client matches ONLY its own controller — never a cross-merge.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [
                Call("axios.get", "/api/appsettings/${id}"),
                Call("axios.get", "/api/objectcodemappings/${id}"),
            ],
            [
                Endpoint("httpget", "AppSettingsController", "GetById", methodRoute: "{id}", symbolId: "ep.appsettings"),
                Endpoint("httpget", "ObjectCodeMappingsController", "GetById", methodRoute: "{id}", symbolId: "ep.ocm"),
            ]);

        var hits = RouteBridge.Resolve(input, resolver).Where(e => e.Kind == BridgeKind.Hits).ToList();

        // Exactly two Hits edges (one per client→endpoint pair), each on a distinct route.
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, e => e.TargetRef.Display == "api/appsettings/{}" && e.TargetRef.SymbolId == "ep.appsettings");
        Assert.Contains(hits, e => e.TargetRef.Display == "api/objectcodemappings/{}" && e.TargetRef.SymbolId == "ep.ocm");
        // No edge crosses the two routes (the merge that token-dropping would cause).
        Assert.DoesNotContain(hits, e => e.SourceRef.Display == "api/appsettings/{}" && e.TargetRef.SymbolId == "ep.ocm");
        Assert.DoesNotContain(hits, e => e.SourceRef.Display == "api/objectcodemappings/{}" && e.TargetRef.SymbolId == "ep.appsettings");
    }

    // ---- TS side filtering: language + test_role ---------------------------------------------------------------

    [Fact]
    public void Resolve_CsharpTestHttpClientLiteral_Excluded_FromTsSide()
    {
        // A C# test HttpClient url literal (language=csharp, a test_role) must NOT be treated as a TS client call.
        // On real data the 57 csharp literals are all test HttpClient; only the 39 typescript ones are real calls.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [Call("client.GetAsync", "/api/appsettings", language: "csharp", testRole: new TestRole("test_case"))],
            [Endpoint("httpget", "AppSettingsController", "List")]);

        var edges = RouteBridge.Resolve(input, resolver);

        Assert.DoesNotContain(edges, e => e.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void Resolve_NonUrlLiteral_Excluded()
    {
        // A non-url literal (e.g. a sql literal) is not a client call and must be ignored by the route bridge.
        var resolver = new SymbolResolver([]);
        var sql = new LiteralRecord("SELECT * FROM X", "sql", "QueryAsync", 0, "csharp", "m", new SourceSpan(0, 1));
        var input = new RouteBridgeInput(
            [new TsClientCall(sql, null, "Data/Repo.cs", 5)],
            [Endpoint("httpget", "AppSettingsController", "List")]);

        Assert.DoesNotContain(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Hits);
    }

    [Fact]
    public void Resolve_TypescriptNonTestLiteral_Included()
    {
        // The positive of the filter: a typescript url literal with no test_role IS a real client call.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [Call("axios.get", "/api/appsettings", language: "typescript", testRole: null)],
            [Endpoint("httpget", "AppSettingsController", "List")]);

        Assert.Contains(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Hits);
    }

    // ---- Responds: return-type unwrap to a named DTO ----------------------------------------------------------

    [Fact]
    public void Resolve_ReturnTypeUnwrapsToNamedDto_EmitsRespondsEdge()
    {
        // Task<ActionResult<AppSetting>> unwraps (balanced bracket depth) to the named DTO AppSetting => a Responds
        // edge endpoint—responds→ AppSetting, anchored by ReturnTypeDto and resolved by name.
        var resolver = new SymbolResolver([Dto("d1", "AppSetting")]);
        var input = new RouteBridgeInput(
            [],
            [Endpoint("httpget", "AppSettingsController", "GetById", methodRoute: "{id}",
                returnType: "Task<ActionResult<AppSetting>>")]);

        var responds = Assert.Single(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Responds);

        Assert.Equal("AppSetting", responds.TargetRef.Display);
        Assert.Equal("d1", responds.TargetRef.SymbolId);
        Assert.Contains(responds.Signals, s => s is StructuralSignal { Rule: SignalRule.ReturnTypeDto, Present: true });

        var scored = BridgeScorer.Score(responds);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
    }

    [Fact]
    public void Resolve_ReturnTypeUnwrapsToCollectionElement_EmitsRespondsEdge()
    {
        // Task<IEnumerable<IProject>> unwraps to the collection element type IProject (an interface is a valid named
        // user type per design §4) => a Responds edge.
        var resolver = new SymbolResolver(
            [new SymbolDetail("i1", "IProject", "interface", "Api/Models/IProject.cs", "public interface IProject", "Models", null, null)]);
        var input = new RouteBridgeInput(
            [],
            [Endpoint("httpget", "ProjectsController", "List", returnType: "Task<IEnumerable<IProject>>")]);

        var responds = Assert.Single(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Responds);

        Assert.Equal("IProject", responds.TargetRef.Display);
        Assert.Equal("i1", responds.TargetRef.SymbolId);
    }

    [Fact]
    public void Resolve_BareActionResult_NoRespondsEdge()
    {
        // A bare Task<ActionResult> (mostly mutations) unwraps to null => NO Responds edge, no penalty to any Hits.
        var resolver = new SymbolResolver([Dto("d1", "AppSetting")]);
        var input = new RouteBridgeInput(
            [],
            [Endpoint("httppost", "AppSettingsController", "Create", returnType: "Task<ActionResult>")]);

        Assert.DoesNotContain(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Responds);
    }

    [Fact]
    public void Resolve_PrimitiveReturnType_NoRespondsEdge()
    {
        // Task<bool> unwraps to a primitive, which is NOT a named user DTO => NO Responds edge (the lone Task<bool>
        // case in the 28-2 findings is dropped, not double-counted).
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [],
            [Endpoint("httpget", "AwardsController", "HasSubawardExpenses", returnType: "Task<bool>")]);

        Assert.DoesNotContain(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Responds);
    }

    [Fact]
    public void Resolve_RespondsUnresolvedDto_NoEdgeFromScorer()
    {
        // The return type unwraps to a named type, but that type is not a symbol => unresolved => the leg emits the
        // candidate carrying Unresolved, and the scorer drops it (no symbol to point at).
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [],
            [Endpoint("httpget", "AppSettingsController", "GetById", returnType: "Task<ActionResult<GhostDto>>")]);

        var responds = Assert.Single(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Responds);
        Assert.Equal(ResolutionStatus.Unresolved, responds.TargetRef.Resolution.Status);
        Assert.Null(BridgeScorer.Score(responds));
    }

    // ---- Consumes: [FromBody] / request parameter DTO ---------------------------------------------------------

    [Fact]
    public void Resolve_FromBodyDto_EmitsConsumesEdge()
    {
        // A request body type (recovered from the method's [FromBody]/parameter type) => a Consumes edge
        // endpoint—consumes→ CreateAppSettingRequest, anchored by FromBodyDto and resolved by name.
        var resolver = new SymbolResolver([Dto("r1", "CreateAppSettingRequest", "Requests")]);
        var input = new RouteBridgeInput(
            [],
            [Endpoint("httppost", "AppSettingsController", "Create", returnType: "Task<ActionResult>",
                requestBodyType: "CreateAppSettingRequest")]);

        var consumes = Assert.Single(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Consumes);

        Assert.Equal("CreateAppSettingRequest", consumes.TargetRef.Display);
        Assert.Equal("r1", consumes.TargetRef.SymbolId);
        Assert.Contains(consumes.Signals, s => s is StructuralSignal { Rule: SignalRule.FromBodyDto, Present: true });

        var scored = BridgeScorer.Score(consumes);
        Assert.NotNull(scored);
        Assert.Equal(ConfidenceBand.High, scored!.Band);
    }

    [Fact]
    public void Resolve_NoRequestBody_NoConsumesEdge()
    {
        // No [FromBody]/request type => NO Consumes edge.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [],
            [Endpoint("httpget", "AppSettingsController", "List", requestBodyType: null)]);

        Assert.DoesNotContain(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Consumes);
    }

    [Fact]
    public void Resolve_ConsumesUnresolvedDto_NoEdgeFromScorer()
    {
        // A [FromBody] type that is not a symbol => unresolved => the scorer drops the Consumes candidate.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [],
            [Endpoint("httppost", "AppSettingsController", "Create", requestBodyType: "GhostRequest")]);

        var consumes = Assert.Single(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Consumes);
        Assert.Equal(ResolutionStatus.Unresolved, consumes.TargetRef.Resolution.Status);
        Assert.Null(BridgeScorer.Score(consumes));
    }

    // ---- combined endpoint: hits + responds + consumes --------------------------------------------------------

    [Fact]
    public void Resolve_OneEndpoint_EmitsHitsRespondsAndConsumes()
    {
        // A single POST endpoint with a concrete return DTO and a request body, hit by a matching axios.post client:
        // one Hits edge (verb-known), one Responds edge, one Consumes edge — three distinct edge kinds.
        var resolver = new SymbolResolver(
            [Dto("res", "AppSetting"), Dto("req", "CreateAppSettingRequest", "Requests")]);
        var input = new RouteBridgeInput(
            [Call("axios.post", "/api/appsettings")],
            [Endpoint("httppost", "AppSettingsController", "Create",
                returnType: "Task<ActionResult<AppSetting>>", requestBodyType: "CreateAppSettingRequest")]);

        var edges = RouteBridge.Resolve(input, resolver);

        var hits = Assert.Single(edges, e => e.Kind == BridgeKind.Hits);
        var responds = Assert.Single(edges, e => e.Kind == BridgeKind.Responds);
        var consumes = Assert.Single(edges, e => e.Kind == BridgeKind.Consumes);

        Assert.Contains(hits.Signals, s => s is StructuralSignal { Rule: SignalRule.RouteVerbMatch, Present: true });
        Assert.Equal("AppSetting", responds.TargetRef.Display);
        Assert.Equal("CreateAppSettingRequest", consumes.TargetRef.Display);

        Assert.Equal(ConfidenceBand.High, BridgeScorer.Score(hits)!.Band);
        Assert.Equal(ConfidenceBand.High, BridgeScorer.Score(responds)!.Band);
        Assert.Equal(ConfidenceBand.High, BridgeScorer.Score(consumes)!.Band);
    }

    [Fact]
    public void Resolve_RespondsAndConsumes_FireEvenWithNoClientCall()
    {
        // The responds/consumes edges describe the endpoint's shape and do NOT depend on any client matching it — an
        // endpoint with no caller still yields its Responds + Consumes edges.
        var resolver = new SymbolResolver(
            [Dto("res", "AppSetting"), Dto("req", "UpdateAppSettingRequest", "Requests")]);
        var input = new RouteBridgeInput(
            [],
            [Endpoint("httpput", "AppSettingsController", "Update",
                returnType: "Task<ActionResult<AppSetting>>", requestBodyType: "UpdateAppSettingRequest")]);

        var edges = RouteBridge.Resolve(input, resolver);

        Assert.DoesNotContain(edges, e => e.Kind == BridgeKind.Hits);
        Assert.Single(edges, e => e.Kind == BridgeKind.Responds);
        Assert.Single(edges, e => e.Kind == BridgeKind.Consumes);
    }

    // ---- route normalization details still hold through the leg ------------------------------------------------

    [Fact]
    public void Resolve_AbsoluteMethodRoute_OverridesClassPrefix_StillMatchesClient()
    {
        // A method route starting with "/" overrides the class [Route]; a client hitting that absolute route matches.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [Call("axios.get", "/health/ready")],
            [Endpoint("httpget", "DiagnosticsController", "Ready", classRoute: "api/[controller]", methodRoute: "/health/ready")]);

        var hits = Assert.Single(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Hits);
        Assert.Equal("health/ready", hits.TargetRef.Display);
    }

    [Fact]
    public void Resolve_ActionTokenExpansion_MatchesClient()
    {
        // [action] expands to the method name; a client on api/orders/submit matches the endpoint.
        var resolver = new SymbolResolver([]);
        var input = new RouteBridgeInput(
            [Call("axios.post", "/api/orders/submit")],
            [Endpoint("httppost", "OrdersController", "Submit", classRoute: "api/[controller]/[action]")]);

        Assert.Single(RouteBridge.Resolve(input, resolver), e => e.Kind == BridgeKind.Hits);
    }

    // ---- guards ------------------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_NullInput_Throws()
    {
        var resolver = new SymbolResolver([]);
        Assert.Throws<ArgumentNullException>(() => RouteBridge.Resolve(null!, resolver));
    }

    [Fact]
    public void Resolve_NullResolver_Throws()
    {
        var input = new RouteBridgeInput([], []);
        Assert.Throws<ArgumentNullException>(() => RouteBridge.Resolve(input, null!));
    }

    [Fact]
    public void Resolve_NoEndpointsNoCalls_EmptyResult()
    {
        var resolver = new SymbolResolver([]);
        Assert.Empty(RouteBridge.Resolve(new RouteBridgeInput([], []), resolver));
    }
}
