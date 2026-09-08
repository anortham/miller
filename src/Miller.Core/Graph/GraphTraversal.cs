using Miller.Core.References;

namespace Miller.Core.Graph;

internal static class GraphTraversal
{
    public static IReadOnlyList<ReachedNode> Reach(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction direction,
        Func<string, bool> contains,
        Func<string, Direction, IEnumerable<string>> neighbours)
    {
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(contains);
        ArgumentNullException.ThrowIfNull(neighbours);

        if (maxDepth <= 0 || limit <= 0)
            return [];

        // probeDepthTruncation: false — a caller that only wants the nodes must not pay for the
        // max-depth frontier probe, which is an uncached SQL load per node over SqliteSymbolGraphIndex.
        Dictionary<string, int> hop =
            Explore(starts, maxDepth, direction, contains, neighbours, probeDepthTruncation: false, out _);

        // Take before materializing: only `limit` ReachedNode allocations, never the whole reached set.
        return Ordered(hop).Take(limit).ToArray();
    }

    public static GraphReachResult ReachWithEvidence(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction direction,
        Func<string, bool> contains,
        Func<string, Direction, IEnumerable<string>> neighbours)
    {
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(contains);
        ArgumentNullException.ThrowIfNull(neighbours);

        // Evidence must remain honest even at zero bounds: a depth-zero walk can still prove that the
        // frontier was hidden, and a limit-zero walk must still report its pre-limit reached count.
        // Ordinary Reach keeps its historical non-positive fast return above.
        int effectiveMaxDepth = Math.Max(0, maxDepth);
        int effectiveLimit = Math.Max(0, limit);

        Dictionary<string, int> hop = Explore(
            starts, effectiveMaxDepth, direction, contains, neighbours,
            probeDepthTruncation: true, out bool truncatedByDepth);

        // ReachedCount is the pre-limit size, so the evidence path must materialize the whole set.
        ReachedNode[] reached = Ordered(hop).ToArray();

        return new GraphReachResult(
            reached.Take(effectiveLimit).ToArray(),
            reached.Length,
            truncatedByDepth,
            reached.Length > effectiveLimit);
    }

    public static GraphReachResult ReachWithEvidence(
        IEnumerable<string> starts,
        int maxDepth,
        int limit,
        Direction direction,
        Func<string, bool> contains,
        Func<string, Direction, IEnumerable<GraphNeighbour>>? neighbours,
        Func<IReadOnlyList<string>, Direction, IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>>>?
            batchNeighbours = null,
        Func<IReadOnlyList<string>, IReadOnlySet<string>, Direction, bool>? hasUnseenNeighbours = null)
    {
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(contains);
        if (neighbours is null && batchNeighbours is null)
            throw new ArgumentNullException(nameof(neighbours));

        int effectiveMaxDepth = Math.Max(0, maxDepth);
        int effectiveLimit = Math.Max(0, limit);
        Dictionary<string, ReachedNode> reached = ExploreEvidence(
            starts,
            effectiveMaxDepth,
            direction,
            contains,
            neighbours,
            batchNeighbours,
            hasUnseenNeighbours,
            out bool truncatedByDepth);
        ReachedNode[] ordered = reached.Values
            .Where(static node => node.Hop > 0)
            .OrderBy(static node => node.Hop)
            .ThenBy(static node => CertaintyPriority(node.PathCertainty))
            .ThenBy(static node => ImpactRanker.RelationshipPriority(node.EdgeKind))
            .ThenBy(static node => ImpactRanker.SourcePriority(node.EdgeSource))
            .ThenByDescending(static node => node.EdgeConfidence)
            .ThenBy(static node => node.Id, StringComparer.Ordinal)
            .ToArray();
        return new GraphReachResult(
            ordered.Take(effectiveLimit).ToArray(),
            ordered.Length,
            truncatedByDepth,
            ordered.Length > effectiveLimit);
    }

    private static Dictionary<string, ReachedNode> ExploreEvidence(
        IEnumerable<string> starts,
        int maxDepth,
        Direction direction,
        Func<string, bool> contains,
        Func<string, Direction, IEnumerable<GraphNeighbour>>? neighbours,
        Func<IReadOnlyList<string>, Direction, IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>>>?
            batchNeighbours,
        Func<IReadOnlyList<string>, IReadOnlySet<string>, Direction, bool>? hasUnseenNeighbours,
        out bool truncatedByDepth)
    {
        var reached = new Dictionary<string, ReachedNode>(StringComparer.Ordinal);
        var frontier = new List<string>();
        foreach (string start in starts)
        {
            if (contains(start) && reached.TryAdd(start, new ReachedNode(start, 0)))
                frontier.Add(start);
        }

        int currentHop = 0;
        while (frontier.Count > 0 && currentHop < maxDepth)
        {
            var frontierEvidence = frontier.ToDictionary(id => id, id => reached[id], StringComparer.Ordinal);
            IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>> adjacentById =
                batchNeighbours is null
                    ? frontier.ToDictionary(
                        static id => id,
                        id => (IReadOnlyList<GraphNeighbour>)neighbours!(id, direction).ToArray(),
                        StringComparer.Ordinal)
                    : batchNeighbours(frontier, direction);
            var nextFrontier = new HashSet<string>(StringComparer.Ordinal);
            int nextHop = currentHop + 1;
            foreach (string current in frontier)
            {
                IReadOnlyList<GraphNeighbour> adjacent =
                    adjacentById.GetValueOrDefault(current, []);
                ReferenceResolutionStatus currentCertainty = frontierEvidence[current].PathCertainty;
                foreach (GraphNeighbour neighbour in adjacent)
                {
                    ReferenceResolutionStatus edgeCertainty = EdgeCertainty(neighbour.EdgeSource);
                    ReferenceResolutionStatus candidatePathCertainty = CombineCertainty(currentCertainty, edgeCertainty);

                    var candidate = new ReachedNode(
                        neighbour.Id,
                        nextHop,
                        current,
                        neighbour.EdgeKind,
                        neighbour.EdgeConfidence,
                        neighbour.EdgeSource,
                        neighbour.Centrality,
                        neighbour.Visibility,
                        candidatePathCertainty);
                    if (!reached.TryGetValue(neighbour.Id, out ReachedNode? existing))
                    {
                        reached[neighbour.Id] = candidate;
                        nextFrontier.Add(neighbour.Id);
                    }
                    else
                    {
                        bool strongerPath = CertaintyPriority(candidate.PathCertainty) < CertaintyPriority(existing.PathCertainty);
                        if (strongerPath || existing.Hop == nextHop && BetterEvidence(candidate, existing))
                        {
                            reached[neighbour.Id] = candidate;
                            if (strongerPath)
                                nextFrontier.Add(neighbour.Id);
                        }
                    }
                }
            }
            frontier = nextFrontier.ToList();
            currentHop = nextHop;
        }

        IReadOnlySet<string> reachedIds = reached.Keys.ToHashSet(StringComparer.Ordinal);
        if (hasUnseenNeighbours is not null)
        {
            truncatedByDepth = hasUnseenNeighbours(frontier, reachedIds, direction);
        }
        else if (batchNeighbours is not null)
        {
            truncatedByDepth = batchNeighbours(frontier, direction)
                .Values
                .SelectMany(static adjacent => adjacent)
                .Any(neighbour => !reachedIds.Contains(neighbour.Id));
        }
        else
        {
            truncatedByDepth = frontier.Any(current =>
                neighbours!(current, direction).Any(neighbour => !reachedIds.Contains(neighbour.Id)));
        }

        return reached;
    }

    private static ReferenceResolutionStatus EdgeCertainty(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return ReferenceResolutionStatus.Heuristic;

        if (source.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
            return ReferenceResolutionStatus.Ambiguous;

        if (ImpactRanker.IsExactSource(source))
            return ReferenceResolutionStatus.Exact;

        return ReferenceResolutionStatus.Heuristic;
    }

    private static ReferenceResolutionStatus CombineCertainty(ReferenceResolutionStatus current, ReferenceResolutionStatus edge)
    {
        if (current == ReferenceResolutionStatus.Ambiguous || edge == ReferenceResolutionStatus.Ambiguous)
            return ReferenceResolutionStatus.Ambiguous;
        if (current == ReferenceResolutionStatus.Heuristic || edge == ReferenceResolutionStatus.Heuristic)
            return ReferenceResolutionStatus.Heuristic;
        return ReferenceResolutionStatus.Exact;
    }

    private static int CertaintyPriority(ReferenceResolutionStatus certainty) => certainty switch
    {
        ReferenceResolutionStatus.Exact => 0,
        ReferenceResolutionStatus.Heuristic => 1,
        ReferenceResolutionStatus.Ambiguous => 2,
        _ => 3,
    };

    private static bool BetterEvidence(ReachedNode candidate, ReachedNode current)
    {
        int certainty = CertaintyPriority(candidate.PathCertainty).CompareTo(
            CertaintyPriority(current.PathCertainty));
        if (certainty != 0)
            return certainty < 0;
        int kind = ImpactRanker.RelationshipPriority(candidate.EdgeKind).CompareTo(
            ImpactRanker.RelationshipPriority(current.EdgeKind));
        if (kind != 0)
            return kind < 0;
        int source = ImpactRanker.SourcePriority(candidate.EdgeSource).CompareTo(
            ImpactRanker.SourcePriority(current.EdgeSource));
        if (source != 0)
            return source < 0;
        int confidence = Nullable.Compare(current.EdgeConfidence, candidate.EdgeConfidence);
        if (confidence != 0)
            return confidence < 0;
        return StringComparer.Ordinal.Compare(candidate.ReachedVia, current.ReachedVia) < 0;
    }

    /// <summary>
    /// Bounded BFS from <paramref name="starts"/>, returning every visited id keyed to its minimum hop
    /// (the starts themselves at hop 0). <paramref name="probeDepthTruncation"/> asks the walk to also report
    /// whether any node sat beyond <paramref name="maxDepth"/>; that costs one extra neighbour lookup per
    /// max-depth frontier node, so only an evidence-consuming caller should request it.
    /// </summary>
    private static Dictionary<string, int> Explore(
        IEnumerable<string> starts,
        int maxDepth,
        Direction direction,
        Func<string, bool> contains,
        Func<string, Direction, IEnumerable<string>> neighbours,
        bool probeDepthTruncation,
        out bool truncatedByDepth)
    {
        truncatedByDepth = false;
        var hop = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new Queue<string>();

        foreach (string start in starts)
        {
            if (!contains(start))
                continue;
            if (hop.TryAdd(start, 0))
                frontier.Enqueue(start);
        }

        while (frontier.Count > 0)
        {
            string current = frontier.Dequeue();
            int currentHop = hop[current];
            if (currentHop >= maxDepth)
            {
                // BFS dequeues in hop order, so by now every node at hop <= maxDepth is already in `hop`:
                // an unseen neighbour here can only sit beyond the depth bound. Stop probing once the flag
                // is set — it is monotonic, and each probe is a real query on the SQLite-backed graph.
                if (probeDepthTruncation && !truncatedByDepth && currentHop == maxDepth &&
                    neighbours(current, direction).Any(neighbour => !hop.ContainsKey(neighbour)))
                {
                    truncatedByDepth = true;
                }
                continue;
            }

            int nextHop = currentHop + 1;
            foreach (string neighbour in neighbours(current, direction))
            {
                if (hop.ContainsKey(neighbour))
                    continue;
                hop[neighbour] = nextHop;
                frontier.Enqueue(neighbour);
            }
        }

        return hop;
    }

    /// <summary>The reached nodes (starts excluded) in the stable (hop asc, id asc) order both Reach paths promise.</summary>
    private static IEnumerable<ReachedNode> Ordered(Dictionary<string, int> hop) =>
        hop.Where(static kv => kv.Value > 0)
            .OrderBy(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .Select(static kv => new ReachedNode(kv.Key, kv.Value));

    public static IReadOnlyList<string>? ShortestPath(
        string from,
        string to,
        int maxDepth,
        Func<string, bool> contains,
        Func<string, IEnumerable<string>> dependencies)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(contains);
        ArgumentNullException.ThrowIfNull(dependencies);

        if (!contains(from) || !contains(to))
            return null;

        if (string.Equals(from, to, StringComparison.Ordinal))
            return [from];

        if (maxDepth <= 0)
            return null;

        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal) { [from] = 0 };
        var frontier = new Queue<string>();
        frontier.Enqueue(from);

        while (frontier.Count > 0)
        {
            string current = frontier.Dequeue();
            int currentDepth = depth[current];
            if (currentDepth >= maxDepth)
                continue;

            foreach (string neighbour in dependencies(current))
            {
                if (depth.ContainsKey(neighbour))
                    continue;

                depth[neighbour] = currentDepth + 1;
                parent[neighbour] = current;

                if (string.Equals(neighbour, to, StringComparison.Ordinal))
                    return Reconstruct(parent, from, to);

                frontier.Enqueue(neighbour);
            }
        }

        return null;
    }

    public static GraphPath? ShortestPathWithEvidence(
        string from,
        string to,
        int maxDepth,
        Func<string, bool> contains,
        Func<string, IEnumerable<GraphNeighbour>> dependencies,
        Func<GraphNeighbour, bool> edgeFilter) =>
        ShortestPathWithEvidence([from], to, maxDepth, contains, dependencies, edgeFilter);

    public static GraphPath? ShortestPathWithEvidence(
        IEnumerable<string> fromNodes,
        string to,
        int maxDepth,
        Func<string, bool> contains,
        Func<string, IEnumerable<GraphNeighbour>> dependencies,
        Func<GraphNeighbour, bool> edgeFilter)
    {
        ArgumentNullException.ThrowIfNull(fromNodes);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(contains);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(edgeFilter);

        if (!contains(to))
            return null;

        var fromList = new List<string>();
        var fromSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (string node in fromNodes)
        {
            if (node is not null && contains(node) && fromSet.Add(node))
                fromList.Add(node);
        }

        if (fromList.Count == 0)
            return null;

        if (fromSet.Contains(to))
            return new GraphPath([to], []);

        if (maxDepth <= 0)
            return null;

        var parent = new Dictionary<string, (string Parent, GraphNeighbour Edge)>(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new Queue<string>();

        foreach (string from in fromList)
        {
            depth[from] = 0;
            frontier.Enqueue(from);
        }

        while (frontier.Count > 0)
        {
            string current = frontier.Dequeue();
            int currentDepth = depth[current];
            if (currentDepth >= maxDepth)
                continue;

            foreach (GraphNeighbour neighbour in dependencies(current).Where(edgeFilter))
            {
                if (depth.ContainsKey(neighbour.Id))
                    continue;

                depth[neighbour.Id] = currentDepth + 1;
                parent[neighbour.Id] = (current, neighbour);
                if (string.Equals(neighbour.Id, to, StringComparison.Ordinal))
                    return ReconstructWithEvidence(parent, fromSet, to);
                frontier.Enqueue(neighbour.Id);
            }
        }

        return null;
    }

    public static GraphPath? ShortestPathWithEvidenceBatched(
        string from,
        string to,
        int maxDepth,
        Func<string, bool> contains,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>>> batchDependencies,
        Func<GraphNeighbour, bool> edgeFilter) =>
        ShortestPathWithEvidenceBatched([from], to, maxDepth, contains, batchDependencies, edgeFilter);

    public static GraphPath? ShortestPathWithEvidenceBatched(
        IEnumerable<string> fromNodes,
        string to,
        int maxDepth,
        Func<string, bool> contains,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>>> batchDependencies,
        Func<GraphNeighbour, bool> edgeFilter)
    {
        ArgumentNullException.ThrowIfNull(fromNodes);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(contains);
        ArgumentNullException.ThrowIfNull(batchDependencies);
        ArgumentNullException.ThrowIfNull(edgeFilter);

        if (!contains(to))
            return null;

        var fromList = new List<string>();
        var fromSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (string node in fromNodes)
        {
            if (node is not null && contains(node) && fromSet.Add(node))
                fromList.Add(node);
        }

        if (fromList.Count == 0)
            return null;

        if (fromSet.Contains(to))
            return new GraphPath([to], []);

        if (maxDepth <= 0)
            return null;

        var parent = new Dictionary<string, (string Parent, GraphNeighbour Edge)>(StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string from in fromList)
            depth[from] = 0;

        var frontier = new List<string>(fromList);

        while (frontier.Count > 0)
        {
            int currentDepth = depth[frontier[0]];
            if (currentDepth >= maxDepth)
                break;

            IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbour>> adjacentById =
                batchDependencies(frontier);
            var nextFrontier = new List<string>();
            foreach (string current in frontier)
            {
                foreach (GraphNeighbour neighbour in adjacentById.GetValueOrDefault(current, []))
                {
                    if (!edgeFilter(neighbour) || depth.ContainsKey(neighbour.Id))
                        continue;

                    depth[neighbour.Id] = currentDepth + 1;
                    parent[neighbour.Id] = (current, neighbour);
                    if (string.Equals(neighbour.Id, to, StringComparison.Ordinal))
                        return ReconstructWithEvidence(parent, fromSet, to);
                    nextFrontier.Add(neighbour.Id);
                }
            }
            frontier = nextFrontier;
        }

        return null;
    }

    private static GraphPath ReconstructWithEvidence(
        IReadOnlyDictionary<string, (string Parent, GraphNeighbour Edge)> parent,
        HashSet<string> fromSet,
        string to)
    {
        var reversedNodes = new List<string> { to };
        var reversedEdges = new List<GraphPathEdge>();
        string node = to;
        while (!fromSet.Contains(node))
        {
            (string previous, GraphNeighbour edge) = parent[node];
            reversedEdges.Add(new GraphPathEdge(
                previous,
                node,
                edge.EdgeKind,
                edge.EdgeConfidence,
                edge.EdgeSource));
            node = previous;
            reversedNodes.Add(node);
        }
        reversedNodes.Reverse();
        reversedEdges.Reverse();
        return new GraphPath(reversedNodes, reversedEdges);
    }

    private static GraphPath ReconstructWithEvidence(
        IReadOnlyDictionary<string, (string Parent, GraphNeighbour Edge)> parent,
        string from,
        string to) =>
        ReconstructWithEvidence(parent, new HashSet<string>(StringComparer.Ordinal) { from }, to);

    private static IReadOnlyList<string> Reconstruct(
        IReadOnlyDictionary<string, string> parent,
        string from,
        string to)
    {
        var reversed = new List<string> { to };
        string node = to;
        while (!string.Equals(node, from, StringComparison.Ordinal))
        {
            node = parent[node];
            reversed.Add(node);
        }
        reversed.Reverse();
        return reversed;
    }
}
