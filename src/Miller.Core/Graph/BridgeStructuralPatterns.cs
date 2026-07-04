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

    // Backend HTTP boundary families, wave 1 (julie-extractors 2.7.0). Route-template families (10) join
    // client requests to server handlers via normalized_route_template; mount/include families (4)
    // are cross-file prefix-join inputs; Rails resource_route expands to routes and rails.mount is
    // evidence-only. Wave 2 (2.8.0) adds twelve more just below.
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

    // Backend HTTP boundary families, wave 2 (julie-extractors 2.8.0 "finish the HTTP boundary lane"): six more
    // mainstream stacks. Route-template families (NestJS/Laravel/Phoenix/axum/actix) join client requests on
    // normalized_route_template exactly like the 2.7.0 families; the resource_route families are aggregate
    // declarations expanded to concrete routes on Miller's side (as with rails.resource_route); the prefix/mount
    // families are cross-file prefix-join inputs. Kotlin+Spring routes reuse SpringRequestMapping and the four new
    // client languages reuse HttpClientRequest, so neither needs a new id here.
    public const string NestJsRoute = "nestjs.route.v1";
    public const string LaravelRoute = "laravel.route.v1";
    public const string LaravelResourceRoute = "laravel.resource_route.v1";
    public const string LaravelRoutePrefix = "laravel.route_prefix.v1";
    public const string PhoenixRoute = "phoenix.route.v1";
    public const string PhoenixResourceRoute = "phoenix.resource_route.v1";
    public const string PhoenixForward = "phoenix.forward.v1";
    public const string AxumRoute = "axum.route.v1";
    public const string AxumNest = "axum.nest.v1";
    public const string ActixAttributeRoute = "actix.attribute_route.v1";
    public const string ActixScopeRoute = "actix.scope_route.v1";
    public const string ActixMount = "actix.mount.v1";

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
        // julie-extractors 2.8.0 wave 2.
        NestJsRoute,
        LaravelRoute,
        LaravelResourceRoute,
        LaravelRoutePrefix,
        PhoenixRoute,
        PhoenixResourceRoute,
        PhoenixForward,
        AxumRoute,
        AxumNest,
        ActixAttributeRoute,
        ActixScopeRoute,
        ActixMount,
    ];

    /// <summary>
    /// The 16 backend route-template families the <c>backend-http</c> provider joins against
    /// <c>normalized_route_template</c> (2.7.0: Express/Fastify/FastAPI/Flask/Django/Spring/Go net-http/gin/echo/
    /// Rails; 2.8.0: NestJS/Laravel/Phoenix/axum + both actix provenances). Excludes the prefix/mount families
    /// (cross-file prefix-join inputs), the <see cref="RailsResourceRoute"/>/<see cref="LaravelResourceRoute"/>/
    /// <see cref="PhoenixResourceRoute"/> aggregate declarations (expanded to concrete routes on Miller's side),
    /// and <see cref="RailsMount"/> (evidence-only). Consumed by
    /// <c>StructuralRouteFactAdapter.TryReadBackendRoute</c> as the family gate.
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
        // julie-extractors 2.8.0: plain route-template families (each carries normalized_route_template, so
        // TryReadBackendRoute joins them with no family-specific read change). actix has two route provenances
        // (attribute macros + scope call routing) mirroring the shipped ASP.NET attribute-vs-call split.
        NestJsRoute,
        LaravelRoute,
        PhoenixRoute,
        AxumRoute,
        ActixAttributeRoute,
        ActixScopeRoute,
    ];
}
