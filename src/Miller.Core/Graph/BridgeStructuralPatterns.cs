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
    ];
}
