using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// Verb-aware backend HTTP boundary bridge provider (julie-extractors 2.7.0): joins
/// <c>http.client_request.v1</c> fetch/axios call sites to server route-template facts from the 10
/// <see cref="BridgeStructuralPatterns.BackendRoutePatternIds"/> families (Express/Fastify/FastAPI/Flask/
/// Django/Spring/Go net-http/gin/echo/Rails), emitting <see cref="BridgeKind.Hits"/> edges. It sits beside the
/// framework-specific verb-aware API arm (<see cref="ApiRouteBridgeProvider"/>) but is standalone rather than
/// descriptor-driven: it collects a broad route-family set plus the cross-file mount/include inputs, giving the
/// later enrichment passes (mount composition, Rails resource expansion) a single place to grow.
///
/// <para>All verb rules come free from <see cref="FileRouteBridge.ResolveClientRequests"/>: handler verb equal ⇒
/// High (<see cref="SignalRule.RouteVerbMatch"/>); handler verb different ⇒ no edge; handler verb null ⇒ Medium
/// <c>verb_unknown</c> (<see cref="SignalRule.RouteOnlyMatch"/>); a specificity tie between equally-specific
/// verb-exact routes is ambiguous and yields no edge.</para>
///
/// <para><b>Enrichment seam (Tasks 3–4).</b> <c>routeHandlers</c> starts as a copy of the directly-read backend
/// routes; mount-prefix composition and Rails resource expansion APPEND their synthesized handlers to that list
/// BEFORE the resolve call, and read the collected <c>mountFacts</c>. Those passes will add
/// <c>backend-http.composedRoutes</c> / <c>.unanchoredMounts</c> / <c>.expandedResourceRoutes</c> evidence keys;
/// Task 2 emits none of those.</para>
/// </summary>
public sealed class BackendHttpBridgeProvider : IBridgeProvider
{
    public const string ProviderId = "backend-http";

    public static BackendHttpBridgeProvider Instance { get; } = new();

    private BackendHttpBridgeProvider()
    {
    }

    public string Id => ProviderId;

    public BridgeProviderResult BuildCandidates(BridgeProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var clientRequests = new List<StructuralClientRequest>();
        var backendRoutes = new List<StructuralRouteHandler>();
        var mountFacts = new List<StructuralMountFact>();
        var railsMountCount = 0;
        foreach (var fact in context.StructuralFacts.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Span.StartByte))
        {
            if (StructuralRouteFactAdapter.TryReadClientRequest(fact, context.SymbolsById, out var request))
            {
                clientRequests.Add(request);
                continue;
            }

            if (StructuralRouteFactAdapter.TryReadBackendRoute(fact, context.SymbolsById, out var handler))
            {
                backendRoutes.Add(handler);
                continue;
            }

            if (StructuralRouteFactAdapter.TryReadMountFact(fact, context.SymbolsById, out var mount))
            {
                // Collected for the Task 3 mount-prefix composition pass; Task 2 only counts them as evidence.
                mountFacts.Add(mount);
                continue;
            }

            // rails.mount mounts a Rack app whose internal routes never reach the fact stream, so it composes
            // nothing — counted as mount evidence only (never read, never a join input).
            if (string.Equals(fact.PatternId, BridgeStructuralPatterns.RailsMount, StringComparison.Ordinal))
                railsMountCount++;
        }

        // The join pool. Tasks 3 (mount-prefix composition) and 4 (Rails resource expansion) APPEND their
        // synthesized handlers here before the resolve call — keep this as a distinct local so the insertion
        // point stays obvious.
        var routeHandlers = new List<StructuralRouteHandler>(backendRoutes);

        var result = FileRouteBridge.ResolveClientRequests(clientRequests, routeHandlers);
        var evidenceCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["backend-http.clientRequests"] = clientRequests.Count,
            ["backend-http.routeFacts"] = backendRoutes.Count,
            ["backend-http.mounts"] = mountFacts.Count + railsMountCount,
            ["backend-http.candidates"] = result.Edges.Count,
            ["backend-http.ambiguousMatches"] = result.AmbiguousMatches,
        };

        if (clientRequests.Count == 0 && backendRoutes.Count == 0 && mountFacts.Count == 0 && railsMountCount == 0)
            return BridgeProviderResult.Skipped("no backend-http bridge evidence", evidenceCounts);

        return BridgeProviderResult.ActiveResult(
            result.Edges,
            evidenceCounts,
            BuildObservationNodes(clientRequests, routeHandlers));
    }

    /// <summary>
    /// Route diagnostics need the unmatched sides too: every client request becomes a canonical-route
    /// <see cref="BridgeNodeKind.TsType"/> node and every entry in <paramref name="routeHandlers"/> a
    /// <see cref="BridgeNodeKind.Endpoint"/> node (matched ones collapse into the edge nodes via the builder's
    /// TryAdd). Building over <paramref name="routeHandlers"/> rather than the directly-read routes means the
    /// Task 3/4 composed/expanded handlers get observation nodes for free.
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
