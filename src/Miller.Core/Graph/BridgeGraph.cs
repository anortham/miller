using Miller.Core.Resolver;

namespace Miller.Core.Graph;

/// <summary>
/// The kind of a node in the provider-scored <see cref="BridgeGraph"/>. Current names reflect the first provider
/// (<see cref="DotnetWebBridgeProvider"/>), where nodes are client types/calls, .NET DTOs/entities, database tables,
/// or controller endpoints. The kind is carried for rendering and to keep the synthetic id of a non-symbol side
/// namespaced by kind so two different-kind nodes with the same display never collide.
/// </summary>
public enum BridgeNodeKind
{
    /// <summary>A dotnet-web client type or client-call endpoint.</summary>
    TsType,

    /// <summary>A dotnet-web .NET DTO (a data-transfer / request / response shape).</summary>
    CsDto,

    /// <summary>A dotnet-web .NET entity (a persistence / domain shape, typically a DbSet element).</summary>
    CsEntity,

    /// <summary>A database table (named by EF convention or SQL text — has no code symbol).</summary>
    DbTable,

    /// <summary>A dotnet-web controller action endpoint (a route target — the <c>—hits→</c> destination).</summary>
    Endpoint,

    /// <summary>A framework file route target.</summary>
    FileRoute,
}

/// <summary>
/// One vertex of the <see cref="BridgeGraph"/>: a stable <see cref="Id"/>, its <see cref="Kind"/>, a human
/// <see cref="Display"/>, and optional locating <see cref="FilePath"/>/<see cref="Line"/>. The graph never invents node
/// identity — for a symbol-backed side the id IS the resolved <c>SymbolId</c>; for a non-symbol side (DB table, route
/// endpoint) the id is a stable synthesis of kind + display (see <see cref="BridgeGraph.SynthesizeId"/>) so the two
/// endpoints of an edge connect deterministically across legs.
/// </summary>
/// <param name="Id">The stable node key (a symbol id, or a kind+display synthesis for a non-symbol side).</param>
/// <param name="Kind">The node's bridge kind (for rendering + non-symbol id namespacing).</param>
/// <param name="Display">The human-readable label (leaf type name, table name, or normalized route).</param>
/// <param name="FilePath">The workspace-relative file the node lives in, or null for a pure route/table node.</param>
/// <param name="Line">The 1-based declaration line, or 0 when there is no single site.</param>
public sealed record BridgeNode(string Id, BridgeNodeKind Kind, string Display, string? FilePath, int Line);

/// <summary>
/// The pure, immutable cross-language bridge graph. Built once per index from the surviving <see cref="ScoredEdge"/>s
/// and a node lookup, it keeps an undirected adjacency over <see cref="BridgeNode"/> ids so a bounded BFS
/// (<see cref="Walk"/>) follows provider-scored threads across layers. The current dotnet-web provider can produce
/// chains like client call → endpoint → DTO/entity → table. Zero I/O; every method is deterministic. Mirrors
/// <see cref="SymbolGraph"/>'s immutable, id-sorted-adjacency, atomic-build discipline.
///
/// <para><b>Adjacency is undirected for the walk.</b> A bridge edge is a correspondence (a DTO maps-to an entity, an
/// entity is stored-in a table); a trace can start at any endpoint and follow the chain either way, so each scored edge
/// is registered on BOTH of its node ids. The original directed <see cref="ScoredEdge"/> (with its source/target,
/// score, band, and flags) is preserved verbatim on every adjacency entry for rendering — the graph never re-scores.</para>
///
/// <para><b>Determinism.</b> Neighbour edges per node are ordered by (neighbour id, then edge kind, then a stable edge
/// signature) so <see cref="Walk"/> visits in a stable order across runs. An edge whose endpoints are not both present
/// in the node lookup is dropped at build time (bounding the graph to known nodes), and an exact duplicate scored edge
/// on a node is collapsed.</para>
/// </summary>
public sealed class BridgeGraph
{
    // node id -> the scored edges incident on it, pre-sorted for deterministic traversal.
    private readonly IReadOnlyDictionary<string, ScoredEdge[]> _adjacency;
    private readonly IReadOnlyDictionary<string, BridgeNode> _nodes;
    private readonly IReadOnlyList<ScoredEdge> _edges;

    private static readonly ScoredEdge[] NoEdges = [];

    private BridgeGraph(
        IReadOnlyDictionary<string, ScoredEdge[]> adjacency,
        IReadOnlyDictionary<string, BridgeNode> nodes,
        IReadOnlyList<ScoredEdge> edges,
        BridgeCapabilityReport capabilityReport)
    {
        _adjacency = adjacency;
        _nodes = nodes;
        _edges = edges;
        CapabilityReport = capabilityReport;
    }

    public BridgeCapabilityReport CapabilityReport { get; }

    /// <summary>The unique scored bridge edges admitted to the graph, in deterministic order.</summary>
    public IReadOnlyList<ScoredEdge> Edges => _edges;

    /// <summary>The bridge node lookup admitted to the graph, including edge-less structural fact nodes.</summary>
    public IReadOnlyDictionary<string, BridgeNode> Nodes => _nodes;

    /// <summary>
    /// Build a bridge graph from <paramref name="scoredEdges"/> and a <paramref name="nodes"/> lookup. Each edge is
    /// registered on both of its endpoint node ids (resolved via <see cref="NodeIdOf"/>); an edge whose endpoints are
    /// not both present in <paramref name="nodes"/> is dropped (bounding the graph to known nodes), self-loops are
    /// dropped, and an exact-duplicate scored edge on a node is collapsed. Neighbour edge lists are sorted for stable
    /// traversal. An empty edge/node set yields an empty graph.
    /// </summary>
    /// <param name="scoredEdges">The surviving scored bridge edges (post-scorer; nulls already dropped by the caller).</param>
    /// <param name="nodes">The node lookup: node id → <see cref="BridgeNode"/> (built by the caller for every endpoint).</param>
    /// <exception cref="ArgumentNullException"><paramref name="scoredEdges"/> or <paramref name="nodes"/> is null.</exception>
    public static BridgeGraph Build(
        IReadOnlyList<ScoredEdge> scoredEdges,
        IReadOnlyDictionary<string, BridgeNode> nodes,
        BridgeCapabilityReport? capabilityReport = null)
    {
        ArgumentNullException.ThrowIfNull(scoredEdges);
        ArgumentNullException.ThrowIfNull(nodes);

        // Per-node deduped edge set; dedupe by a stable edge signature so the same correspondence is not double-counted.
        var byNode = new Dictionary<string, Dictionary<string, ScoredEdge>>(StringComparer.Ordinal);
        var uniqueEdges = new Dictionary<string, ScoredEdge>(StringComparer.Ordinal);

        foreach (var edge in scoredEdges)
        {
            var sourceId = NodeIdOf(edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source);
            var targetId = NodeIdOf(edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target);

            if (sourceId is null || targetId is null)
                continue; // an endpoint we cannot identify (blank display, no symbol) — cannot connect it
            if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
                continue; // self-loop: a node never bridges to itself
            if (!nodes.ContainsKey(sourceId) || !nodes.ContainsKey(targetId))
                continue; // endpoint not in the node lookup — bound the graph to known nodes

            var signature = EdgeSignature(edge, sourceId, targetId);
            uniqueEdges.TryAdd(signature, edge);
            AddIncident(byNode, sourceId, signature, edge);
            AddIncident(byNode, targetId, signature, edge);
        }

        var adjacency = new Dictionary<string, ScoredEdge[]>(byNode.Count, StringComparer.Ordinal);
        foreach (var (nodeId, edgeSet) in byNode)
            adjacency[nodeId] = SortIncident(nodeId, edgeSet.Values);

        return new BridgeGraph(
            adjacency,
            new Dictionary<string, BridgeNode>(nodes, StringComparer.Ordinal),
            uniqueEdges.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToArray(),
            capabilityReport ?? BridgeCapabilityReport.Empty);
    }

    /// <summary>
    /// Walk the bridge graph breadth-first from <paramref name="startId"/>, returning the scored edges traversed in
    /// reach order (BFS layer by layer, deterministic id-sorted neighbours), de-duplicated, and bounded by
    /// <paramref name="maxDepth"/> hops. An edge appears at most once even when both its endpoints are reached. An
    /// unknown start id or <paramref name="maxDepth"/> ≤ 0 yields an empty result.
    /// </summary>
    /// <param name="startId">The node id to start the walk from (a symbol id or a synthesized id).</param>
    /// <param name="maxDepth">Maximum number of bridge hops to follow; ≤ 0 yields an empty result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="startId"/> is null.</exception>
    public IReadOnlyList<ScoredEdge> Walk(string startId, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(startId);

        if (maxDepth <= 0 || !_adjacency.ContainsKey(startId))
            return [];

        var depth = new Dictionary<string, int>(StringComparer.Ordinal) { [startId] = 0 };
        var frontier = new Queue<string>();
        frontier.Enqueue(startId);

        var emittedEdges = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ScoredEdge>();

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            var currentDepth = depth[current];
            if (currentDepth >= maxDepth)
                continue; // its edges would exceed the depth cap

            foreach (var edge in Incident(current))
            {
                var sourceId = NodeIdOf(edge.Edge.SourceRef, edge.Edge.Kind, EndpointSide.Source);
                var targetId = NodeIdOf(edge.Edge.TargetRef, edge.Edge.Kind, EndpointSide.Target);
                var other = string.Equals(sourceId, current, StringComparison.Ordinal) ? targetId : sourceId;
                if (other is null)
                    continue;

                // Emit the traversed edge once, in reach order.
                if (emittedEdges.Add(EdgeSignature(edge, sourceId!, targetId!)))
                    result.Add(edge);

                // Discover the neighbour at the next layer (BFS keeps the minimum depth).
                if (!depth.ContainsKey(other))
                {
                    depth[other] = currentDepth + 1;
                    frontier.Enqueue(other);
                }
            }
        }

        return result;
    }

    /// <summary>The node for <paramref name="id"/>, or null when the id is not a vertex of this graph.</summary>
    public BridgeNode? Node(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _nodes.TryGetValue(id, out var node) ? node : null;
    }

    /// <summary>True when <paramref name="id"/> is a vertex of this graph.</summary>
    public bool Contains(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _nodes.ContainsKey(id);
    }

    /// <summary>The scored edges directly incident on <paramref name="id"/>, in deterministic order; empty if unknown.</summary>
    public IReadOnlyList<ScoredEdge> Incident(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _adjacency.TryGetValue(id, out var edges) ? edges : NoEdges;
    }

    /// <summary>
    /// The stable node id for an <see cref="EdgeRef"/>: a symbol-backed side uses its resolved <c>SymbolId</c>; a
    /// non-symbol side (DB table, route endpoint) synthesizes a stable id from its node kind + display so the two ends
    /// of an edge connect consistently. Returns null when the side has neither a symbol id nor a usable display.
    /// </summary>
    public static string? NodeIdOf(EdgeRef edgeRef, BridgeKind edgeKind, EndpointSide side)
    {
        if (!string.IsNullOrEmpty(edgeRef.SymbolId))
            return edgeRef.SymbolId;
        if (string.IsNullOrWhiteSpace(edgeRef.Display))
            return null;
        return SynthesizeId(NodeKindFor(edgeKind, side), edgeRef.Display);
    }

    /// <summary>
    /// A stable synthetic id for a non-symbol node, namespaced by kind so a table and a route with the same display
    /// never collide: <c>"&lt;kind&gt;:&lt;display&gt;"</c> (display lowercased for case-stable matching of route/table text).
    /// </summary>
    public static string SynthesizeId(BridgeNodeKind kind, string display) =>
        $"{kind}:{display.ToLowerInvariant()}";

    /// <summary>
    /// Map a bridge edge kind + endpoint side to the node kind of that endpoint. Current labels match dotnet-web
    /// provider output: StoredIn is entity→table; Hits is client route→endpoint; NavigatesTo is client route→file route;
    /// MapsTo is DTO/entity; Responds/Consumes are endpoint→DTO. The source-vs-target side disambiguates the two ends.
    /// </summary>
    public static BridgeNodeKind NodeKindFor(BridgeKind edgeKind, EndpointSide side) => edgeKind switch
    {
        BridgeKind.StoredIn => side == EndpointSide.Source ? BridgeNodeKind.CsEntity : BridgeNodeKind.DbTable,
        BridgeKind.Hits => side == EndpointSide.Source ? BridgeNodeKind.TsType : BridgeNodeKind.Endpoint,
        BridgeKind.NavigatesTo => side == EndpointSide.Source ? BridgeNodeKind.TsType : BridgeNodeKind.FileRoute,
        BridgeKind.Responds => side == EndpointSide.Source ? BridgeNodeKind.Endpoint : BridgeNodeKind.CsDto,
        BridgeKind.Consumes => side == EndpointSide.Source ? BridgeNodeKind.Endpoint : BridgeNodeKind.CsDto,
        BridgeKind.MapsTo => BridgeNodeKind.CsDto,
        BridgeKind.NameMatch => side == EndpointSide.Source ? BridgeNodeKind.TsType : BridgeNodeKind.CsDto,
        _ => BridgeNodeKind.CsDto,
    };

    private static void AddIncident(
        Dictionary<string, Dictionary<string, ScoredEdge>> byNode, string nodeId, string signature, ScoredEdge edge)
    {
        if (!byNode.TryGetValue(nodeId, out var set))
        {
            set = new Dictionary<string, ScoredEdge>(StringComparer.Ordinal);
            byNode[nodeId] = set;
        }
        // First write wins for a given signature (an exact-duplicate correspondence is collapsed).
        set.TryAdd(signature, edge);
    }

    /// <summary>
    /// A stable, order-independent signature for an edge between two node ids: the kind plus the two ids sorted, so the
    /// same correspondence reached from either endpoint dedupes to one entry, and the same pair via two edge kinds
    /// (e.g. a Responds and a Consumes between an endpoint and a DTO) stays distinct.
    /// </summary>
    private static string EdgeSignature(ScoredEdge edge, string sourceId, string targetId)
    {
        var (a, b) = string.CompareOrdinal(sourceId, targetId) <= 0 ? (sourceId, targetId) : (targetId, sourceId);
        return $"{edge.Edge.Kind}|{a}|{b}";
    }

    /// <summary>
    /// Sort a node's incident edges for deterministic traversal: by the neighbour id, then the edge kind, then the
    /// full edge signature (so two edges of the same kind to the same neighbour still order stably).
    /// </summary>
    private static ScoredEdge[] SortIncident(string nodeId, IEnumerable<ScoredEdge> edges)
    {
        var array = edges.ToArray();
        Array.Sort(array, (x, y) =>
        {
            var xs = NodeIdOf(x.Edge.SourceRef, x.Edge.Kind, EndpointSide.Source) ?? string.Empty;
            var xt = NodeIdOf(x.Edge.TargetRef, x.Edge.Kind, EndpointSide.Target) ?? string.Empty;
            var ys = NodeIdOf(y.Edge.SourceRef, y.Edge.Kind, EndpointSide.Source) ?? string.Empty;
            var yt = NodeIdOf(y.Edge.TargetRef, y.Edge.Kind, EndpointSide.Target) ?? string.Empty;

            var xOther = string.Equals(xs, nodeId, StringComparison.Ordinal) ? xt : xs;
            var yOther = string.Equals(ys, nodeId, StringComparison.Ordinal) ? yt : ys;

            int byNeighbour = string.CompareOrdinal(xOther, yOther);
            if (byNeighbour != 0)
                return byNeighbour;

            int byKind = x.Edge.Kind.CompareTo(y.Edge.Kind);
            if (byKind != 0)
                return byKind;

            return string.CompareOrdinal(
                EdgeSignature(x, xs, xt),
                EdgeSignature(y, ys, yt));
        });
        return array;
    }
}
