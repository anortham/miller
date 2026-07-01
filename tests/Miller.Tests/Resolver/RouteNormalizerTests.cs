using Miller.Core.Resolver;
using Xunit;

namespace Miller.Tests.Resolver;

/// <summary>
/// Pins <see cref="RouteNormalizer"/> — where the route-bridge precision lives (design §4 Leg 1, §8 risk 3). Two
/// surfaces: the TS client side (<see cref="RouteNormalizer.FromClientCall"/>: verb from carrier or verb-unknown,
/// param folding) and the C# endpoint side (<see cref="RouteNormalizer.FromEndpoint"/>: <c>[controller]</c>/
/// <c>[action]</c>/<c>[area]</c> token expansion BEFORE prefix concat, absolute override, param folding). Every row
/// asserts the exact <c>(verb, route)</c>. The load-bearing negatives: two controllers' <c>{id}</c> templates must
/// normalize to DISTINCT routes (collision guard), and a verb-less carrier must be verb-unknown, NOT assumed GET.
/// </summary>
public sealed class RouteNormalizerTests
{
    // ---- TS client side: verb derivation + route folding -------------------------------------------------------

    public static TheoryData<string, string, string?, string> ClientCallTable() => new()
    {
        // carrier, literalText, expectedVerb (null = verb-unknown), expectedRoute
        // axios.<verb> carriers: verb known from the carrier tail.
        { "axios.post", "/api/appsettings", "POST", "api/appsettings" },
        { "axios.get", "/api/appsettings", "GET", "api/appsettings" },
        { "axios.delete", "/api/objectcodemappings/{}", "DELETE", "api/objectcodemappings/{}" },
        { "axios.patch", "/api/messages/{}/dismiss", "PATCH", "api/messages/{}/dismiss" },
        // <Verb>Async carriers: verb known.
        { "PostAsync", "/api/orders", "POST", "api/orders" },
        { "GetAsync", "/api/orders", "GET", "api/orders" },
        // Param shapes all fold to {}: ${id}, :id, {id}.
        { "axios.get", "/api/users/${userId}", "GET", "api/users/{}" },
        { "axios.get", "/api/users/:userId", "GET", "api/users/{}" },
        { "axios.get", "/api/users/{userId}", "GET", "api/users/{}" },
        // A param followed by a literal extension keeps the extension (the :param fold is identifier-bounded, not
        // "everything to /"), so all three param syntaxes match the C# "{id}.json" endpoint side.
        { "axios.get", "/api/files/:id.json", "GET", "api/files/{}.json" },
        { "axios.get", "/api/files/${id}.json", "GET", "api/files/{}.json" },
        { "axios.get", "/api/files/{id}.json", "GET", "api/files/{}.json" },
        // Trailing slash dropped, case folded, query string stripped.
        { "axios.get", "/API/Users/", "GET", "api/users" },
        { "axios.get", "/api/users?active=true", "GET", "api/users" },
        // Leading slash normalized away (routes compared without it).
        { "axios.get", "api/users", "GET", "api/users" },
    };

    [Theory]
    [MemberData(nameof(ClientCallTable))]
    public void FromClientCall_DerivesVerbAndNormalizesRoute(
        string carrier, string literalText, string? expectedVerb, string expectedRoute)
    {
        var result = RouteNormalizer.FromClientCall(carrier, literalText);

        Assert.Equal(expectedVerb, result.Verb);
        Assert.Equal(expectedRoute, result.Route);
        Assert.Equal(expectedVerb is not null, result.VerbKnown);
    }

    [Theory]
    // Verb-less carriers: NEVER assume GET. Verb-unknown, route still normalized.
    [InlineData("fetch")]
    [InlineData("$fetch")]
    [InlineData("ofetch")]
    [InlineData("axios")]
    [InlineData("request")]
    [InlineData("ky")]
    [InlineData("got")]
    [InlineData("sendasync")]
    public void FromClientCall_VerbLessCarrier_IsVerbUnknown_NeverAssumesGet(string carrier)
    {
        var result = RouteNormalizer.FromClientCall(carrier, "/api/users");

        Assert.Null(result.Verb);
        Assert.False(result.VerbKnown);
        Assert.NotEqual("GET", result.Verb); // explicit: the never-assume-GET invariant
        Assert.Equal("api/users", result.Route); // route still recovered
    }

    [Theory]
    [InlineData("/api/users/[id]")]
    [InlineData("/api/users/[...id]")]
    [InlineData("/api/users/[[...id]]")]
    [InlineData("/api/users/{id}")]
    [InlineData("/api/users/:id")]
    [InlineData("/api/users/${id}")]
    public void FromClientCall_DynamicSegments_CanonicalizeToSamePlaceholder(string route)
    {
        var result = RouteNormalizer.FromClientCall("axios.get", route);

        Assert.Equal("api/users/{}", result.Route);
    }

    // ---- C# endpoint side: token expansion + prefix concat -----------------------------------------------------

    [Fact]
    public void FromEndpoint_ExpandsControllerToken_BeforePrefixConcat()
    {
        // Route("api/[controller]") on AppSettingsController + HttpGet("{id}") => api/appsettings/{}
        var result = RouteNormalizer.FromEndpoint(
            verbKey: "httpget",
            classRoute: "api/[controller]",
            methodRoute: "{id}",
            parentClassName: "AppSettingsController",
            methodName: "GetById");

        Assert.Equal("GET", result.Verb);
        Assert.True(result.VerbKnown);
        Assert.Equal("api/appsettings/{}", result.Route);
    }

    [Fact]
    public void FromEndpoint_ExpandsActionToken_FromMethodName()
    {
        // [action] expands to the method name lowercased.
        var result = RouteNormalizer.FromEndpoint(
            verbKey: "httppost",
            classRoute: "api/[controller]/[action]",
            methodRoute: null,
            parentClassName: "OrdersController",
            methodName: "Submit");

        Assert.Equal("POST", result.Verb);
        Assert.Equal("api/orders/submit", result.Route);
    }

    [Fact]
    public void FromEndpoint_ExpandsAreaToken()
    {
        var result = RouteNormalizer.FromEndpoint(
            verbKey: "httpget",
            classRoute: "[area]/[controller]",
            methodRoute: "{id}",
            parentClassName: "AdminController",
            methodName: "Get",
            area: "Admin");

        Assert.Equal("admin/admin/{}", result.Route);
    }

    [Fact]
    public void FromEndpoint_AbsoluteMethodRoute_OverridesClassPrefix()
    {
        // A method route starting with "/" is absolute: it ignores the class [Route] prefix.
        var result = RouteNormalizer.FromEndpoint(
            verbKey: "httpget",
            classRoute: "api/[controller]",
            methodRoute: "/health/ready",
            parentClassName: "DiagnosticsController",
            methodName: "Ready");

        Assert.Equal("health/ready", result.Route);
    }

    [Fact]
    public void FromEndpoint_NoMethodRoute_UsesClassRouteAlone()
    {
        var result = RouteNormalizer.FromEndpoint(
            verbKey: "httpget",
            classRoute: "api/[controller]",
            methodRoute: null,
            parentClassName: "AppSettingsController",
            methodName: "List");

        Assert.Equal("api/appsettings", result.Route);
    }

    [Fact]
    public void FromEndpoint_ControllerSuffixStrippedCaseInsensitively()
    {
        // Only the trailing "Controller" is removed; the rest is lowercased verbatim.
        var result = RouteNormalizer.FromEndpoint(
            verbKey: "httpget",
            classRoute: "api/[controller]",
            methodRoute: null,
            parentClassName: "ObjectCodeMappingsController",
            methodName: "List");

        Assert.Equal("api/objectcodemappings", result.Route);
    }

    /// <summary>
    /// THE collision guard (design §8 risk 3). Two controllers each expose <c>HttpGet("{id}")</c> under
    /// <c>Route("api/[controller]")</c>. If <c>[controller]</c> expansion dropped the controller name they would both
    /// normalize to <c>api/{}</c> and merge into one High-confidence WRONG link. They MUST stay distinct.
    /// </summary>
    [Fact]
    public void FromEndpoint_TwoControllersWithSameMethodTemplate_NormalizeToDistinctRoutes()
    {
        var a = RouteNormalizer.FromEndpoint(
            verbKey: "httpget", classRoute: "api/[controller]", methodRoute: "{id}",
            parentClassName: "AppSettingsController", methodName: "GetById");
        var b = RouteNormalizer.FromEndpoint(
            verbKey: "httpget", classRoute: "api/[controller]", methodRoute: "{id}",
            parentClassName: "ObjectCodeMappingsController", methodName: "GetById");

        Assert.Equal("api/appsettings/{}", a.Route);
        Assert.Equal("api/objectcodemappings/{}", b.Route);
        Assert.NotEqual(a.Route, b.Route); // the collision guard, stated as an assertion
    }

    [Fact]
    public void FromEndpoint_ClientAndEndpoint_NormalizeToTheSameRoute_ForAMatch()
    {
        // The end-to-end point: a TS axios.get and the C# endpoint it hits produce an identical (verb, route).
        var client = RouteNormalizer.FromClientCall("axios.get", "/api/appsettings/${id}");
        var endpoint = RouteNormalizer.FromEndpoint(
            verbKey: "httpget", classRoute: "api/[controller]", methodRoute: "{id}",
            parentClassName: "AppSettingsController", methodName: "GetById");

        Assert.Equal(client.Verb, endpoint.Verb);
        Assert.Equal(client.Route, endpoint.Route);
    }

    [Fact]
    public void ParamWithExtension_ClientAndEndpoint_NormalizeToTheSameRoute()
    {
        // The :param fold must stop at the identifier so a "/files/:id.json" client call and the C# "{id}.json"
        // endpoint produce the SAME route — the over-consuming ":[^/]+" form folded the extension away on one side only.
        var client = RouteNormalizer.FromClientCall("axios.get", "/api/files/:id.json");
        var endpoint = RouteNormalizer.FromEndpoint(
            verbKey: "httpget", classRoute: "api/[controller]", methodRoute: "{id}.json",
            parentClassName: "FilesController", methodName: "GetById");

        Assert.Equal("api/files/{}.json", client.Route);
        Assert.Equal(client.Route, endpoint.Route);
    }

    [Theory]
    // Verb key mapping: the annotation_key (lowercased) maps to the canonical HTTP verb.
    [InlineData("httpget", "GET")]
    [InlineData("httppost", "POST")]
    [InlineData("httpput", "PUT")]
    [InlineData("httpdelete", "DELETE")]
    [InlineData("httppatch", "PATCH")]
    [InlineData("httphead", "HEAD")]
    [InlineData("httpoptions", "OPTIONS")]
    public void FromEndpoint_MapsVerbKeyToHttpVerb(string verbKey, string expectedVerb)
    {
        var result = RouteNormalizer.FromEndpoint(
            verbKey: verbKey, classRoute: "api/[controller]", methodRoute: null,
            parentClassName: "XController", methodName: "M");

        Assert.Equal(expectedVerb, result.Verb);
        Assert.True(result.VerbKnown);
    }
}
