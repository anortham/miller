using Miller.Core.Contracts;
using Miller.Core.Graph;

namespace Miller.Core.Resolver;

internal sealed record FileRouteBridgeResult(IReadOnlyList<CandidateEdge> Edges, int AmbiguousMatches);

internal static class FileRouteBridge
{
    public static FileRouteBridgeResult Resolve(
        IReadOnlyList<StructuralRouteReference> references,
        IReadOnlyList<StructuralFileRoute> fileRoutes)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(fileRoutes);

        var edges = new List<CandidateEdge>();
        var ambiguousMatches = 0;
        foreach (var reference in references)
        {
            var matches = fileRoutes
                .Where(route => FileRouteMatcher.Matches(reference.RoutePath, route.RoutePath))
                .ToArray();

            if (matches.Length == 0)
                continue;

            var bestMatch = BestMatch(matches);
            if (bestMatch is null)
            {
                ambiguousMatches++;
                continue;
            }

            edges.Add(BuildEdge(reference, bestMatch));
        }

        return new FileRouteBridgeResult(edges, ambiguousMatches);
    }

    private static StructuralFileRoute? BestMatch(IReadOnlyList<StructuralFileRoute> matches)
    {
        var best = matches[0];
        var ambiguous = false;

        for (var index = 1; index < matches.Count; index++)
        {
            var candidate = matches[index];
            var comparison = CompareSpecificity(candidate, best);
            if (comparison > 0)
            {
                best = candidate;
                ambiguous = false;
            }
            else if (comparison == 0)
            {
                ambiguous = true;
            }
        }

        return ambiguous ? null : best;
    }

    private static int CompareSpecificity(StructuralFileRoute left, StructuralFileRoute right)
    {
        var leftSegments = FileRouteMatcher.RouteSegments(left.RoutePath);
        var rightSegments = FileRouteMatcher.RouteSegments(right.RoutePath);
        var commonLength = Math.Min(leftSegments.Length, rightSegments.Length);

        for (var index = 0; index < commonLength; index++)
        {
            var comparison = SegmentSpecificity(leftSegments[index]).CompareTo(SegmentSpecificity(rightSegments[index]));
            if (comparison != 0)
                return comparison;
        }

        return rightSegments.Length.CompareTo(leftSegments.Length);
    }

    private static int SegmentSpecificity(string segment)
    {
        if (FileRouteMatcher.IsOptionalCatchAllSegment(segment))
            return 0;
        if (FileRouteMatcher.IsCatchAllSegment(segment))
            return 1;
        if (FileRouteMatcher.IsDynamicSegment(segment))
            return 2;
        return 3;
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
