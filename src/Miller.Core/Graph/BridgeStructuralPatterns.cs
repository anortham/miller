namespace Miller.Core.Graph;

public static class BridgeStructuralPatterns
{
    public const string AspNetMinimalApiRoute = "aspnet.minimal_api.route.v1";
    public const string HtmxAttribute = "htmx.attribute.v1";
    public const string VueRouteReference = "vue.route_reference.v1";
    public const string VueRouteDefinition = "vue.route_definition.v1";
    public const string ReactRouteReference = "react.route_reference.v1";
    public const string ReactRouteDefinition = "react.route_definition.v1";
    public const string NextJsRouteReference = "nextjs.route_reference.v1";
    public const string NextJsFileRoute = "nextjs.file_route.v1";
    public const string NuxtRouteReference = "nuxt.route_reference.v1";
    public const string NuxtFileRoute = "nuxt.file_route.v1";
    public const string HttpClientRequest = "http.client_request.v1";
    public const string NextJsRouteHandler = "nextjs.route_handler.v1";
    public const string NuxtServerRoute = "nuxt.server_route.v1";
    public const string AspNetAttributeRoute = "aspnet.attribute_route.v1";

    // Backend HTTP boundary families (julie-extractors 2.7.0). Route-template families (10) join
    // client requests to server handlers via normalized_route_template; mount/include families (4)
    // are cross-file prefix-join inputs; Rails resource_route expands to routes and rails.mount is
    // evidence-only.
    public const string ExpressRoute = "express.route.v1";
    public const string ExpressRouterMount = "express.router_mount.v1";
    public const string FastifyRoute = "fastify.route.v1";
    public const string FastApiRoute = "fastapi.route.v1";
    public const string FastApiIncludeRouter = "fastapi.include_router.v1";
    public const string FlaskRoute = "flask.route.v1";
    public const string FlaskBlueprintRegistration = "flask.blueprint_registration.v1";
    public const string DjangoUrlPattern = "django.url_pattern.v1";
    public const string DjangoUrlInclude = "django.url_include.v1";
    public const string SpringRequestMapping = "spring.request_mapping.v1";
    public const string GoNetHttpRoute = "go.net_http.route.v1";
    public const string GinRoute = "gin.route.v1";
    public const string EchoRoute = "echo.route.v1";
    public const string RailsRoute = "rails.route.v1";
    public const string RailsResourceRoute = "rails.resource_route.v1";
    public const string RailsMount = "rails.mount.v1";

    /// <summary>
    /// The <c>SqliteBridgeReader.ReadStructuralFacts</c> SQL load whitelist: a pattern id absent here never
    /// reaches any bridge provider (silent no-op). Append new bridge fact families here first.
    /// </summary>
    public static readonly IReadOnlyList<string> BridgeFactPatternIds =
    [
        AspNetMinimalApiRoute,
        HtmxAttribute,
        VueRouteReference,
        VueRouteDefinition,
        ReactRouteReference,
        ReactRouteDefinition,
        NextJsRouteReference,
        NextJsFileRoute,
        NuxtRouteReference,
        NuxtFileRoute,
        HttpClientRequest,
        NextJsRouteHandler,
        NuxtServerRoute,
        AspNetAttributeRoute,
        ExpressRoute,
        ExpressRouterMount,
        FastifyRoute,
        FastApiRoute,
        FastApiIncludeRouter,
        FlaskRoute,
        FlaskBlueprintRegistration,
        DjangoUrlPattern,
        DjangoUrlInclude,
        SpringRequestMapping,
        GoNetHttpRoute,
        GinRoute,
        EchoRoute,
        RailsRoute,
        RailsResourceRoute,
        RailsMount,
    ];

    /// <summary>
    /// The 10 backend route-template families the <c>backend-http</c> provider joins against
    /// <c>normalized_route_template</c>. Excludes the four mount/include families (prefix-join inputs),
    /// <see cref="RailsResourceRoute"/> (expanded to routes on Miller's side), and <see cref="RailsMount"/>
    /// (evidence-only). Consumed by <c>StructuralRouteFactAdapter.TryReadBackendRoute</c> as the family gate.
    /// </summary>
    public static readonly IReadOnlyList<string> BackendRoutePatternIds =
    [
        ExpressRoute,
        FastifyRoute,
        FastApiRoute,
        FlaskRoute,
        DjangoUrlPattern,
        SpringRequestMapping,
        GoNetHttpRoute,
        GinRoute,
        EchoRoute,
        RailsRoute,
    ];
}
