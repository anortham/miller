using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// Next.js bridge provider: route references such as Link/router navigation to file-route facts.
/// </summary>
public sealed class NextJsBridgeProvider : IBridgeProvider
{
    public const string ProviderId = "nextjs";
    private const string NextJsRouteReferencePattern = "nextjs.route_reference.v1";
    private const string NextJsFileRoutePattern = "nextjs.file_route.v1";

    public static NextJsBridgeProvider Instance { get; } = new();

    private NextJsBridgeProvider()
    {
    }

    public string Id => ProviderId;

    public BridgeProviderResult BuildCandidates(BridgeProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var routeReferences = new List<StructuralRouteReference>();
        var fileRoutes = new List<StructuralFileRoute>();
        foreach (var fact in context.StructuralFacts.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Span.StartByte))
        {
            if (string.Equals(fact.PatternId, NextJsRouteReferencePattern, StringComparison.Ordinal) &&
                StructuralRouteFactAdapter.TryReadRouteReference(fact, context.SymbolsById, out var reference))
            {
                routeReferences.Add(reference);
                continue;
            }

            if (string.Equals(fact.PatternId, NextJsFileRoutePattern, StringComparison.Ordinal) &&
                StructuralRouteFactAdapter.TryReadFileRoute(fact, context.SymbolsById, out var fileRoute))
            {
                fileRoutes.Add(fileRoute);
            }
        }

        var candidates = NextRouteBridge.Resolve(routeReferences, fileRoutes);
        var evidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["nextjs.routeReferences"] = routeReferences.Count,
            ["nextjs.fileRoutes"] = fileRoutes.Count,
            ["nextjs.candidates"] = candidates.Count,
            ["nextjs.ambiguousMatches"] = CountAmbiguousMatches(routeReferences, fileRoutes),
        };

        if (routeReferences.Count == 0 && fileRoutes.Count == 0)
            return BridgeProviderResult.Skipped("no nextjs bridge evidence", evidenceCounts);

        return BridgeProviderResult.ActiveResult(
            candidates,
            evidenceCounts,
            BuildObservationNodes(routeReferences, fileRoutes));
    }

    private static int CountAmbiguousMatches(
        IReadOnlyList<StructuralRouteReference> routeReferences,
        IReadOnlyList<StructuralFileRoute> fileRoutes)
    {
        var count = 0;
        foreach (var reference in routeReferences)
        {
            var matches = fileRoutes
                .Where(route => NextRouteMatcher.Matches(reference.RoutePath, route.RoutePath))
                .Take(2)
                .Count();
            if (matches == 2)
                count++;
        }

        return count;
    }

    private static IReadOnlyDictionary<string, BridgeNode> BuildObservationNodes(
        IReadOnlyList<StructuralRouteReference> routeReferences,
        IReadOnlyList<StructuralFileRoute> fileRoutes)
    {
        var nodes = new Dictionary<string, BridgeNode>(StringComparer.Ordinal);

        foreach (var reference in routeReferences)
        {
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.TsType, reference.RoutePath);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.TsType, reference.RoutePath, reference.FilePath, reference.Line));
        }

        foreach (var route in fileRoutes)
        {
            var id = BridgeGraph.SynthesizeId(BridgeNodeKind.NextRoute, route.RoutePath);
            nodes.TryAdd(id, new BridgeNode(id, BridgeNodeKind.NextRoute, route.RoutePath, route.FilePath, route.Line));
        }

        return nodes;
    }
}
