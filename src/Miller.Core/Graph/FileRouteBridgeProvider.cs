using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// Framework file-route bridge provider: route references such as Link/NuxtLink navigation to file-route facts.
/// </summary>
public sealed class FileRouteBridgeProvider : IBridgeProvider
{
    public static FileRouteBridgeProvider NextJs { get; } = new(
        new FileRouteBridgeDescriptor(
            ProviderId: "nextjs",
            DisplayName: "Next.js",
            RouteReferencePattern: BridgeStructuralPatterns.NextJsRouteReference,
            FileRoutePattern: BridgeStructuralPatterns.NextJsFileRoute));

    public static FileRouteBridgeProvider Nuxt { get; } = new(
        new FileRouteBridgeDescriptor(
            ProviderId: "nuxt",
            DisplayName: "Nuxt",
            RouteReferencePattern: BridgeStructuralPatterns.NuxtRouteReference,
            FileRoutePattern: BridgeStructuralPatterns.NuxtFileRoute));

    public static FileRouteBridgeProvider Vue { get; } = new(
        new FileRouteBridgeDescriptor(
            ProviderId: "vue",
            DisplayName: "Vue",
            RouteReferencePattern: BridgeStructuralPatterns.VueRouteReference,
            FileRoutePattern: BridgeStructuralPatterns.VueRouteDefinition));

    public static FileRouteBridgeProvider React { get; } = new(
        new FileRouteBridgeDescriptor(
            ProviderId: "react",
            DisplayName: "React",
            RouteReferencePattern: BridgeStructuralPatterns.ReactRouteReference,
            FileRoutePattern: BridgeStructuralPatterns.ReactRouteDefinition));

    public static FileRouteBridgeProvider Blazor { get; } = new(
        new FileRouteBridgeDescriptor(
            ProviderId: "blazor",
            DisplayName: "Blazor",
            RouteReferencePattern: BridgeStructuralPatterns.RazorRouteReference,
            FileRoutePattern: BridgeStructuralPatterns.RazorPageDirective));

    private readonly FileRouteBridgeDescriptor _descriptor;

    private FileRouteBridgeProvider(FileRouteBridgeDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public string Id => _descriptor.ProviderId;

    public BridgeProviderResult BuildCandidates(BridgeProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var routeReferences = new List<StructuralRouteReference>();
        var fileRoutes = new List<StructuralFileRoute>();
        foreach (var fact in context.StructuralFacts.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Span.StartByte))
        {
            if (string.Equals(fact.PatternId, _descriptor.RouteReferencePattern, StringComparison.Ordinal) &&
                StructuralRouteFactAdapter.TryReadRouteReference(fact, context.SymbolsById, out var reference))
            {
                routeReferences.Add(reference);
                continue;
            }

            if (string.Equals(fact.PatternId, _descriptor.FileRoutePattern, StringComparison.Ordinal) &&
                StructuralRouteFactAdapter.TryReadFileRoute(fact, context.SymbolsById, out var fileRoute))
            {
                fileRoutes.Add(fileRoute);
            }
        }

        var result = FileRouteBridge.Resolve(routeReferences, fileRoutes);
        var evidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [_descriptor.EvidenceKey("routeReferences")] = routeReferences.Count,
            [_descriptor.EvidenceKey("fileRoutes")] = fileRoutes.Count,
            [_descriptor.EvidenceKey("candidates")] = result.Edges.Count,
            [_descriptor.EvidenceKey("ambiguousMatches")] = result.AmbiguousMatches,
        };

        if (routeReferences.Count == 0 && fileRoutes.Count == 0)
            return BridgeProviderResult.Skipped($"no {_descriptor.ProviderId} bridge evidence", evidenceCounts);

        return BridgeProviderResult.ActiveResult(
            result.Edges,
            evidenceCounts,
            BuildObservationNodes(routeReferences, fileRoutes));
    }

    private static IReadOnlyDictionary<string, BridgeNode> BuildObservationNodes(
        IReadOnlyList<StructuralRouteReference> routeReferences,
        IReadOnlyList<StructuralFileRoute> fileRoutes)
    {
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);

        foreach (var reference in routeReferences)
        {
            var display = RouteDisplay(reference.RoutePath);
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, display);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.TsType, display, reference.FilePath, reference.Line));
        }

        foreach (var route in fileRoutes)
        {
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.FileRoute, route.RoutePath);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.FileRoute, route.RoutePath, route.FilePath, route.Line));
        }

        return nodes;
    }

    internal static string RouteDisplay(string route)
    {
        var canonical = RouteNormalizer.FromClientCall("route", route).Route;
        if (canonical.Length == 0)
            return route.StartsWith("/", StringComparison.Ordinal) ? route : "/" + route;
        return canonical.StartsWith("/", StringComparison.Ordinal) ? canonical : "/" + canonical;
    }
}

/// <summary>
/// Verb-aware framework API bridge provider (2.6.0 HTTP boundary facts): <c>http.client_request.v1</c>
/// fetch/axios call sites to server route-handler facts (<c>nextjs.route_handler.v1</c> /
/// <c>nuxt.server_route.v1</c>), emitting <see cref="BridgeKind.Hits"/> edges. Sits beside the verb-blind
/// navigation providers above — same descriptor pattern, same <see cref="FileRouteMatcher"/> segment matching
/// and specificity — while the verb rules live in <see cref="FileRouteBridge.ResolveClientRequests"/>.
/// </summary>
public sealed class ApiRouteBridgeProvider : IBridgeProvider
{
    public static ApiRouteBridgeProvider NextJs { get; } = new(
        new ApiRouteBridgeDescriptor(
            ProviderId: "nextjs-api",
            DisplayName: "Next.js",
            ClientRequestPattern: BridgeStructuralPatterns.HttpClientRequest,
            RouteHandlerPattern: BridgeStructuralPatterns.NextJsRouteHandler,
            HandlerEvidenceName: "routeHandlers"));

    public static ApiRouteBridgeProvider Nuxt { get; } = new(
        new ApiRouteBridgeDescriptor(
            ProviderId: "nuxt-api",
            DisplayName: "Nuxt",
            ClientRequestPattern: BridgeStructuralPatterns.HttpClientRequest,
            RouteHandlerPattern: BridgeStructuralPatterns.NuxtServerRoute,
            HandlerEvidenceName: "serverRoutes"));

    private readonly ApiRouteBridgeDescriptor _descriptor;

    private ApiRouteBridgeProvider(ApiRouteBridgeDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    public string Id => _descriptor.ProviderId;

    public BridgeProviderResult BuildCandidates(BridgeProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var clientRequests = new List<StructuralClientRequest>();
        var routeHandlers = new List<StructuralRouteHandler>();
        foreach (var fact in context.StructuralFacts.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Span.StartByte))
        {
            if (string.Equals(fact.PatternId, _descriptor.ClientRequestPattern, StringComparison.Ordinal) &&
                StructuralRouteFactAdapter.TryReadClientRequest(fact, context.SymbolsById, out var request))
            {
                clientRequests.Add(request);
                continue;
            }

            if (string.Equals(fact.PatternId, _descriptor.RouteHandlerPattern, StringComparison.Ordinal) &&
                StructuralRouteFactAdapter.TryReadRouteHandler(fact, context.SymbolsById, out var handler))
            {
                routeHandlers.Add(handler);
            }
        }

        var result = FileRouteBridge.ResolveClientRequests(clientRequests, routeHandlers);
        var evidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [_descriptor.EvidenceKey("clientRequests")] = clientRequests.Count,
            [_descriptor.EvidenceKey(_descriptor.HandlerEvidenceName)] = routeHandlers.Count,
            [_descriptor.EvidenceKey("candidates")] = result.Edges.Count,
            [_descriptor.EvidenceKey("ambiguousMatches")] = result.AmbiguousMatches,
        };

        if (clientRequests.Count == 0 && routeHandlers.Count == 0)
            return BridgeProviderResult.Skipped($"no {_descriptor.ProviderId} bridge evidence", evidenceCounts);

        return BridgeProviderResult.ActiveResult(
            result.Edges,
            evidenceCounts,
            BuildObservationNodes(clientRequests, routeHandlers));
    }

    /// <summary>
    /// Route diagnostics need the unmatched sides too: every client request becomes a canonical-route
    /// <see cref="BridgeNodeKind.TsType"/> node and every handler a <see cref="BridgeNodeKind.Endpoint"/> node
    /// (matched ones collapse into the edge nodes via the builder's TryAdd).
    /// </summary>
    private static IReadOnlyDictionary<string, BridgeNode> BuildObservationNodes(
        IReadOnlyList<StructuralClientRequest> clientRequests,
        IReadOnlyList<StructuralRouteHandler> routeHandlers)
    {
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);

        foreach (var request in clientRequests)
        {
            var display = FileRouteBridgeProvider.RouteDisplay(request.RoutePath);
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, display);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.TsType, display, request.FilePath, request.Line));
        }

        foreach (var handler in routeHandlers)
        {
            var display = FileRouteBridge.HandlerDisplay(handler);
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.Endpoint, display);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.Endpoint, display, handler.FilePath, handler.Line));
        }

        return nodes;
    }
}

internal sealed record ApiRouteBridgeDescriptor(
    string ProviderId,
    string DisplayName,
    string ClientRequestPattern,
    string RouteHandlerPattern,
    string HandlerEvidenceName)
{
    public string EvidenceKey(string name) => ProviderId + "." + name;
}

internal sealed record FileRouteBridgeDescriptor(
    string ProviderId,
    string DisplayName,
    string RouteReferencePattern,
    string FileRoutePattern)
{
    public string EvidenceKey(string name) => ProviderId + "." + name;
}

public static class NextJsBridgeProvider
{
    public const string ProviderId = "nextjs";
    public static IBridgeProvider Instance => FileRouteBridgeProvider.NextJs;
}

public static class NuxtBridgeProvider
{
    public const string ProviderId = "nuxt";
    public static IBridgeProvider Instance => FileRouteBridgeProvider.Nuxt;
}

public static class VueBridgeProvider
{
    public const string ProviderId = "vue";
    public static IBridgeProvider Instance => FileRouteBridgeProvider.Vue;
}

public static class ReactBridgeProvider
{
    public const string ProviderId = "react";
    public static IBridgeProvider Instance => FileRouteBridgeProvider.React;
}

public static class NextJsApiBridgeProvider
{
    public const string ProviderId = "nextjs-api";
    public static IBridgeProvider Instance => ApiRouteBridgeProvider.NextJs;
}

public static class NuxtApiBridgeProvider
{
    public const string ProviderId = "nuxt-api";
    public static IBridgeProvider Instance => ApiRouteBridgeProvider.Nuxt;
}
