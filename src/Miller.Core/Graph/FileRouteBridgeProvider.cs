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

    private static string RouteDisplay(string route)
    {
        var canonical = RouteNormalizer.FromClientCall("route", route).Route;
        if (canonical.Length == 0)
            return route.StartsWith("/", StringComparison.Ordinal) ? route : "/" + route;
        return canonical.StartsWith("/", StringComparison.Ordinal) ? canonical : "/" + canonical;
    }
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
