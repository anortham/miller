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

            var bestMatch = BestMatch(matches, route => route.RoutePath);
            if (bestMatch is null)
            {
                ambiguousMatches++;
                continue;
            }

            edges.Add(BuildEdge(reference, bestMatch));
        }

        return new FileRouteBridgeResult(edges, ambiguousMatches);
    }

    /// <summary>
    /// The verb-aware API arm (2.6.0 HTTP boundary facts): resolve <c>http.client_request.v1</c> fetch/axios
    /// call sites against server route-handler definitions (<c>nextjs.route_handler.v1</c> /
    /// <c>nuxt.server_route.v1</c>), emitting <see cref="BridgeKind.Hits"/> candidates. Route matching and
    /// specificity reuse the navigation arm (<see cref="FileRouteMatcher.Matches"/> + <see cref="BestMatch{T}"/>;
    /// exact-specificity ties are counted ambiguous and yield no edge). The verb rules follow the verb-honesty
    /// doctrine — the client verb is ALWAYS known (2.6.0 contract):
    /// <list type="bullet">
    /// <item>handler verb known and equal ⇒ candidate; the edge carries <see cref="SignalRule.RouteVerbMatch"/>
    ///   (High-eligible);</item>
    /// <item>handler verb known and different ⇒ NOT a candidate (a real verb distinction — no edge);</item>
    /// <item>handler verb NULL (suffix-less Nuxt server route answering every method) ⇒ candidate; the edge
    ///   carries <see cref="SignalRule.RouteOnlyMatch"/> (Medium, flagged verb-unknown) — the handler's accepted
    ///   verb set is not source-attested, so the match stays honest-Medium, never assumed.</item>
    /// </list>
    /// </summary>
    public static FileRouteBridgeResult ResolveClientRequests(
        IReadOnlyList<StructuralClientRequest> requests,
        IReadOnlyList<StructuralRouteHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(handlers);

        var edges = new List<CandidateEdge>();
        var ambiguousMatches = 0;
        foreach (var request in requests)
        {
            var matches = handlers
                .Where(handler => VerbEligible(request, handler) &&
                                  FileRouteMatcher.Matches(request.RoutePath, handler.RoutePath))
                .ToArray();

            if (matches.Length == 0)
                continue;

            var bestMatch = BestMatch(matches, handler => handler.RoutePath);
            if (bestMatch is null)
            {
                ambiguousMatches++;
                continue;
            }

            edges.Add(BuildClientRequestEdge(request, bestMatch));
        }

        return new FileRouteBridgeResult(edges, ambiguousMatches);
    }

    /// <summary>
    /// Verb candidacy: a handler with a known verb different from the client's is not a candidate; a
    /// verb-less handler (accepted set unattested) stays eligible for a route-only match.
    /// </summary>
    private static bool VerbEligible(StructuralClientRequest request, StructuralRouteHandler handler) =>
        handler.Verb is null || string.Equals(handler.Verb, request.Verb, StringComparison.Ordinal);

    private static T? BestMatch<T>(IReadOnlyList<T> matches, Func<T, string> routeOf)
        where T : class
    {
        var best = matches[0];
        var ambiguous = false;

        for (var index = 1; index < matches.Count; index++)
        {
            var candidate = matches[index];
            var comparison = CompareSpecificity(routeOf(candidate), routeOf(best));
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

    private static int CompareSpecificity(string left, string right)
    {
        var leftSegments = FileRouteMatcher.RouteSegments(left);
        var rightSegments = FileRouteMatcher.RouteSegments(right);
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

    private static CandidateEdge BuildClientRequestEdge(StructuralClientRequest request, StructuralRouteHandler handler)
    {
        var requestEvidence = new Evidence(request.FilePath, request.Line);
        var handlerEvidence = new Evidence(handler.FilePath, handler.Line);

        // The client side is the containing frontend symbol when the fact supplied one, falling back to a
        // route node — mirroring the navigation edge source handling.
        var sourceSymbolId = string.IsNullOrWhiteSpace(request.ContainingSymbolId)
            ? null
            : request.ContainingSymbolId;
        var sourceRef = new EdgeRef(
            request.RoutePath,
            sourceSymbolId,
            request.FilePath,
            new NameResolution(ResolutionStatus.Resolved, sourceSymbolId, 1));

        // The handler side binds to the exported handler symbol when the fact carries one (the Next.js
        // navigation payoff); a whole-file fact without a containing symbol (Nuxt server routes) falls back
        // to a synthesized endpoint node.
        var targetSymbolId = string.IsNullOrWhiteSpace(handler.ContainingSymbolId)
            ? null
            : handler.ContainingSymbolId;
        var targetRef = new EdgeRef(
            targetSymbolId is null ? HandlerDisplay(handler) : handler.RoutePath,
            targetSymbolId,
            handler.FilePath,
            new NameResolution(ResolutionStatus.Resolved, targetSymbolId, 1));

        var rule = handler.Verb is null ? SignalRule.RouteOnlyMatch : SignalRule.RouteVerbMatch;

        return new CandidateEdge(
            BridgeKind.Hits,
            sourceRef,
            targetRef,
            [requestEvidence, handlerEvidence],
            [new StructuralSignal(rule, Present: true, handlerEvidence)]);
    }

    /// <summary>
    /// The synthesized endpoint display for a handler, in the dotnet-web synthetic <c>"VERB /route"</c> shape
    /// (<c>GET /api/messages</c>, canonical route). A verb-less handler (suffix-less Nuxt) shows the route
    /// alone — its accepted verb set is not source-attested, so no verb is displayed.
    /// </summary>
    internal static string HandlerDisplay(StructuralRouteHandler handler)
    {
        var canonical = RouteNormalizer.FromClientCall("route", handler.RoutePath).Route;
        var route = canonical.Length == 0 ? handler.RoutePath : canonical;
        if (!route.StartsWith('/'))
            route = "/" + route;
        return handler.Verb is null ? route : handler.Verb + " " + route;
    }
}
