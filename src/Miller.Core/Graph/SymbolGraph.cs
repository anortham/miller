namespace Miller.Core.Graph;

/// <summary>One vertex in the dependency graph: a symbol id plus julie's cross-language test flag.</summary>
/// <param name="Id">The symbol's resolved id (the graph never invents ids; these come from the index).</param>
/// <param name="IsTest">True when <c>symbols.metadata.is_test</c> marked this symbol a test (verified fact #5).</param>
public sealed record GraphNode(string Id, bool IsTest, string? Visibility = null);

/// <summary>
/// One directed dependency edge <c>From → To</c> meaning "<c>From</c> depends on <c>To</c>" (From calls/uses To;
/// M5 decision D2). <see cref="Kind"/> carries julie's edge label (<c>calls</c>/<c>uses</c>/<c>type_usage</c>/...)
/// for provenance; it does not affect traversal.
/// </summary>
/// <param name="From">The dependent symbol id (source of the edge).</param>
/// <param name="To">The depended-upon symbol id (target of the edge).</param>
/// <param name="Kind">The relationship label, for display only.</param>
public sealed record GraphEdge(
    string From,
    string To,
    string Kind,
    double Confidence = 1.0,
    string Source = "relationship");

public sealed record GraphNeighbour(
    string Id,
    string EdgeKind,
    double EdgeConfidence,
    string EdgeSource,
    int Centrality,
    string? Visibility);

/// <summary>A symbol reached by <see cref="SymbolGraph.Reach"/>, tagged with its minimum hop distance.</summary>
/// <param name="Id">The reached symbol's id (never one of the BFS start ids).</param>
/// <param name="Hop">The shortest number of edges from the nearest start to this node (≥ 1).</param>
public sealed record ReachedNode(
    string Id,
    int Hop,
    string? ReachedVia = null,
    string? EdgeKind = null,
    double? EdgeConfidence = null,
    string? EdgeSource = null,
    int Centrality = 0,
    string? Visibility = null);

public sealed record GraphReachResult(
    IReadOnlyList<ReachedNode> Nodes,
    int ReachedCount,
    bool TruncatedByDepth,
    bool TruncatedByLimit)
{
    public bool Exhausted => !TruncatedByDepth && !TruncatedByLimit;
}

/// <summary>Which adjacency a <see cref="SymbolGraph.Reach"/> traversal follows.</summary>
public enum Direction
{
    /// <summary>Follow <see cref="SymbolGraph.Dependencies"/> (what the node depends on — callees).</summary>
    Forward,

    /// <summary>Follow <see cref="SymbolGraph.Dependents"/> (what depends on the node — callers / blast radius).</summary>
    Reverse,

    /// <summary>Follow both directions at once (the <c>context</c> neighbour expansion).</summary>
    Both,
}

public interface ISymbolGraphReachability
{
    GraphReachResult ReachWithEvidence(IEnumerable<string> starts, int maxDepth, int limit, Direction dir);

    /// <summary>
    /// The reached nodes without the traversal evidence. The default discards the evidence after computing it;
    /// an implementation whose neighbour lookup costs a query should override this to skip that work entirely
    /// (both in-tree implementations do).
    /// </summary>
    IReadOnlyList<ReachedNode> Reach(IEnumerable<string> starts, int maxDepth, int limit, Direction dir) =>
        ReachWithEvidence(starts, maxDepth, limit, dir).Nodes;

    IReadOnlyList<string>? ShortestPath(string from, string to, int maxDepth);
}

/// <summary>
/// The pure, immutable symbol dependency graph (M5 decisions D3/D4). Built once from a node set and a resolved
/// edge list, it keeps forward adjacency (<see cref="Dependencies"/>: <c>from → [to]</c>) and reverse adjacency
/// (<see cref="Dependents"/>: <c>to → [from]</c>) so a bounded in-memory BFS (<see cref="Reach"/>) answers
/// <c>context</c> neighbour-expansion and <c>impact</c> reverse-reachability without touching the DB — the
/// latency win over julie's per-hop DB walk. Zero I/O; every method is deterministic.
///
/// <para>Edge hygiene applied at build time: edges whose endpoints are not both in the node set are dropped
/// (bounding the graph to indexed symbols), self-loops are dropped, and duplicate <c>(from, to)</c> pairs are
/// collapsed per direction. Neighbour lists are sorted by id so traversal order is stable across runs.</para>
/// </summary>
public sealed class SymbolGraph : ISymbolGraphReachability
{
    private readonly IReadOnlyDictionary<string, GraphNeighbour[]> _dependencies;
    private readonly IReadOnlyDictionary<string, GraphNeighbour[]> _dependents;
    private readonly IReadOnlyDictionary<string, string[]> _dependencyIds;
    private readonly IReadOnlyDictionary<string, string[]> _dependentIds;
    private readonly IReadOnlyDictionary<string, bool> _isTest;

    private static readonly string[] None = [];

    private SymbolGraph(
        IReadOnlyDictionary<string, GraphNeighbour[]> dependencies,
        IReadOnlyDictionary<string, GraphNeighbour[]> dependents,
        IReadOnlyDictionary<string, bool> isTest)
    {
        _dependencies = dependencies;
        _dependents = dependents;
        _dependencyIds = dependencies.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Select(static neighbour => neighbour.Id).ToArray(),
            StringComparer.Ordinal);
        _dependentIds = dependents.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Select(static neighbour => neighbour.Id).ToArray(),
            StringComparer.Ordinal);
        _isTest = isTest;
    }

    /// <summary>
    /// Build the graph from <paramref name="nodes"/> and <paramref name="edges"/>, applying the edge hygiene
    /// described on the type. An empty node/edge set yields an empty graph (no neighbours, empty reach).
    /// </summary>
    /// <param name="nodes">The vertices; the id of each becomes a known node. Duplicate ids keep the last flag.</param>
    /// <param name="edges">Directed dependency edges; endpoints not in <paramref name="nodes"/> are ignored.</param>
    /// <exception cref="ArgumentNullException"><paramref name="nodes"/> or <paramref name="edges"/> is null.</exception>
    public static SymbolGraph Build(IReadOnlyCollection<GraphNode> nodes, IReadOnlyCollection<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var isTest = new Dictionary<string, bool>(StringComparer.Ordinal);
        var visibility = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            isTest[node.Id] = node.IsTest; // last write wins for a duplicated id
            visibility[node.Id] = node.Visibility;
        }

        // Collect deduped neighbour sets per direction, considering only edges whose BOTH endpoints are known
        // nodes and that are not self-loops.
        var forward = new Dictionary<string, Dictionary<string, GraphEdge>>(StringComparer.Ordinal);
        var reverse = new Dictionary<string, Dictionary<string, GraphEdge>>(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (string.Equals(edge.From, edge.To, StringComparison.Ordinal))
                continue; // self-loop: a symbol is never its own dependency
            if (!isTest.ContainsKey(edge.From) || !isTest.ContainsKey(edge.To))
                continue; // endpoint not in the node set

            AddNeighbour(forward, edge.From, edge.To, edge);
            AddNeighbour(reverse, edge.To, edge.From, edge);
        }

        return new SymbolGraph(
            Freeze(forward, forward, reverse, visibility),
            Freeze(reverse, forward, reverse, visibility),
            isTest);
    }

    private static void AddNeighbour(
        Dictionary<string, Dictionary<string, GraphEdge>> adjacency,
        string key,
        string neighbour,
        GraphEdge edge)
    {
        if (!adjacency.TryGetValue(key, out var neighbours))
        {
            neighbours = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
            adjacency[key] = neighbours;
        }
        if (!neighbours.TryGetValue(neighbour, out GraphEdge? current) || CompareEdge(edge, current) < 0)
            neighbours[neighbour] = edge;
    }

    private static Dictionary<string, GraphNeighbour[]> Freeze(
        Dictionary<string, Dictionary<string, GraphEdge>> adjacency,
        Dictionary<string, Dictionary<string, GraphEdge>> forward,
        Dictionary<string, Dictionary<string, GraphEdge>> reverse,
        IReadOnlyDictionary<string, string?> visibility)
    {
        var frozen = new Dictionary<string, GraphNeighbour[]>(adjacency.Count, StringComparer.Ordinal);
        foreach (var (key, neighbours) in adjacency)
        {
            frozen[key] = neighbours
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new GraphNeighbour(
                    pair.Key,
                    pair.Value.Kind,
                    pair.Value.Confidence,
                    pair.Value.Source,
                    Degree(pair.Key, forward, reverse),
                    visibility.GetValueOrDefault(pair.Key)))
                .ToArray();
        }
        return frozen;
    }

    private static int Degree(
        string id,
        IReadOnlyDictionary<string, Dictionary<string, GraphEdge>> forward,
        IReadOnlyDictionary<string, Dictionary<string, GraphEdge>> reverse) =>
        (forward.TryGetValue(id, out var dependencies) ? dependencies.Count : 0) +
        (reverse.TryGetValue(id, out var dependents) ? dependents.Count : 0);

    private static int CompareEdge(GraphEdge left, GraphEdge right)
    {
        int kind = ImpactRanker.RelationshipPriority(left.Kind).CompareTo(
            ImpactRanker.RelationshipPriority(right.Kind));
        if (kind != 0)
            return kind;
        int source = ImpactRanker.SourcePriority(left.Source).CompareTo(
            ImpactRanker.SourcePriority(right.Source));
        if (source != 0)
            return source;
        int confidence = right.Confidence.CompareTo(left.Confidence);
        if (confidence != 0)
            return confidence;
        int sourceName = StringComparer.Ordinal.Compare(left.Source, right.Source);
        return sourceName != 0
            ? sourceName
            : StringComparer.Ordinal.Compare(left.Kind, right.Kind);
    }

    /// <summary>
    /// The symbols <paramref name="id"/> directly depends on (its callees / used types), sorted by id. Empty
    /// when the id has no outgoing edges or is unknown.
    /// </summary>
    public IReadOnlyList<string> Dependencies(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _dependencyIds.TryGetValue(id, out var neighbours)
            ? neighbours
            : None;
    }

    /// <summary>
    /// The symbols that directly depend on <paramref name="id"/> (its callers), sorted by id. Empty when nothing
    /// depends on the id or it is unknown. This is the <c>impact</c> blast-radius adjacency.
    /// </summary>
    public IReadOnlyList<string> Dependents(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _dependentIds.TryGetValue(id, out var neighbours)
            ? neighbours
            : None;
    }

    /// <summary>True when <paramref name="id"/> is a known node flagged as a test; false otherwise (incl. unknown).</summary>
    public bool IsTest(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _isTest.TryGetValue(id, out var flag) && flag;
    }

    /// <summary>True when <paramref name="id"/> is a vertex of this graph.</summary>
    public bool Contains(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _isTest.ContainsKey(id);
    }

    /// <summary>
    /// Bounded breadth-first reachability from <paramref name="starts"/>, returning the reached nodes —
    /// <b>excluding the starts themselves</b> — each with its minimum hop distance from the nearest start.
    /// Visited tracking terminates on cycles; diamonds report the shortest path; unknown start ids are skipped.
    /// Results are ordered by (hop ascending, then id) and truncated to <paramref name="limit"/>.
    /// </summary>
    /// <param name="starts">The seed ids; ids not in the graph are skipped (the rest still traverse).</param>
    /// <param name="maxDepth">Maximum hop distance to explore; <c>0</c> (or negative) yields an empty result.</param>
    /// <param name="limit">Maximum number of reached nodes to return; <c>≤ 0</c> yields an empty result.</param>
    /// <param name="dir">Which adjacency to follow (<see cref="Direction"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="starts"/> is null.</exception>
    public IReadOnlyList<ReachedNode> Reach(IEnumerable<string> starts, int maxDepth, int limit, Direction dir) =>
        GraphTraversal.Reach(starts, maxDepth, limit, dir, Contains, Neighbours);

    public GraphReachResult ReachWithEvidence(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction dir) =>
        GraphTraversal.ReachWithEvidence(starts, maxDepth, limit, dir, Contains, NeighbourEvidence);

    /// <summary>
    /// The shortest dependency path from <paramref name="from"/> to <paramref name="to"/> as an ordered id list
    /// (<c>from … to</c>, inclusive of both endpoints), or null when <paramref name="to"/> is unreachable from
    /// <paramref name="from"/> within <paramref name="maxDepth"/> hops or either endpoint is not a vertex of the graph.
    /// Follows the forward (<see cref="Dependencies"/>) adjacency — "what does <c>from</c> reach". Breadth-first with
    /// parent reconstruction; ties are broken by visiting neighbours in id order (consistent with <see cref="Reach"/>),
    /// so the returned path is deterministic across runs. <c>from == to</c> (a known vertex) yields the single-node
    /// path <c>[from]</c>.
    /// </summary>
    /// <param name="from">The start vertex id; null/unknown yields null.</param>
    /// <param name="to">The goal vertex id; null/unknown yields null.</param>
    /// <param name="maxDepth">Maximum hop distance to explore; <c>≤ 0</c> yields null unless <c>from == to</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="from"/> or <paramref name="to"/> is null.</exception>
    public IReadOnlyList<string>? ShortestPath(string from, string to, int maxDepth) =>
        GraphTraversal.ShortestPath(from, to, maxDepth, Contains, Dependencies);

    /// <summary>The neighbour ids of <paramref name="id"/> in the requested direction (Both = forward ∪ reverse).</summary>
    private IEnumerable<string> Neighbours(string id, Direction dir) => dir switch
    {
        Direction.Forward => Dependencies(id),
        Direction.Reverse => Dependents(id),
        Direction.Both => Dependencies(id).Concat(Dependents(id)),
        _ => None,
    };

    private IEnumerable<GraphNeighbour> NeighbourEvidence(string id, Direction dir) => dir switch
    {
        Direction.Forward => _dependencies.GetValueOrDefault(id) ?? [],
        Direction.Reverse => _dependents.GetValueOrDefault(id) ?? [],
        Direction.Both => (_dependencies.GetValueOrDefault(id) ?? [])
            .Concat(_dependents.GetValueOrDefault(id) ?? [])
            .GroupBy(static neighbour => neighbour.Id, StringComparer.Ordinal)
            .Select(static group => group.OrderBy(
                neighbour => ImpactRanker.RelationshipPriority(neighbour.EdgeKind)).First()),
        _ => [],
    };
}
