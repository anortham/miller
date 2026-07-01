using Miller.Core.Contracts;
using Miller.Core.Graph;

namespace Miller.Core.Resolver;

internal static class FileRouteBridge
{
    public static IReadOnlyList<CandidateEdge> Resolve(
        IReadOnlyList<StructuralRouteReference> references,
        IReadOnlyList<StructuralFileRoute> fileRoutes)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(fileRoutes);

        var edges = new List<CandidateEdge>();
        foreach (var reference in references)
        {
            var matches = fileRoutes
                .Where(route => FileRouteMatcher.Matches(reference.RoutePath, route.RoutePath))
                .Take(2)
                .ToArray();

            if (matches.Length == 1)
                edges.Add(BuildEdge(reference, matches[0]));
        }

        return edges;
    }

    private static CandidateEdge BuildEdge(StructuralRouteReference reference, StructuralFileRoute fileRoute)
    {
        var referenceEvidence = new Evidence(reference.FilePath, reference.Line);
        var routeEvidence = new Evidence(fileRoute.FilePath, fileRoute.Line);

        var sourceSymbolId = string.IsNullOrWhiteSpace(reference.ContainingSymbolId)
            ? null
            : reference.ContainingSymbolId;

        var sourceRef = new EdgeRef(
            reference.RoutePath,
            sourceSymbolId,
            reference.FilePath,
            new NameResolution(ResolutionStatus.Resolved, sourceSymbolId, 1));

        var targetRef = new EdgeRef(
            fileRoute.RoutePath,
            SymbolId: null,
            fileRoute.FilePath,
            new NameResolution(ResolutionStatus.Resolved, null, 1));

        return new CandidateEdge(
            BridgeKind.NavigatesTo,
            sourceRef,
            targetRef,
            [referenceEvidence, routeEvidence],
            [new StructuralSignal(SignalRule.RouteReferenceMatch, Present: true, referenceEvidence)]);
    }
}
