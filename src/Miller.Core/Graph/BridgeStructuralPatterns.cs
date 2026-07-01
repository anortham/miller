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
    ];
}
