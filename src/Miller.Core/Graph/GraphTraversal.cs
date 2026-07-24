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
        Func<string, Direction, IEnumerable<GraphNeighbour>> neighbours)
    {
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(contains);
        ArgumentNullException.ThrowIfNull(neighbours);

        int effectiveMaxDepth = Math.Max(0, maxDepth);
        int effectiveLimit = Math.Max(0, limit);
        Dictionary<string, ReachedNode> reached = ExploreEvidence(
            starts, effectiveMaxDepth, direction, contains, neighbours, out bool truncatedByDepth);
        ReachedNode[] ordered = reached.Values
            .Where(static node => node.Hop > 0)
            .OrderBy(static node => node.Hop)
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
        Func<string, Direction, IEnumerable<GraphNeighbour>> neighbours,
        out bool truncatedByDepth)
    {
        truncatedByDepth = false;
        var reached = new Dictionary<string, ReachedNode>(StringComparer.Ordinal);
        var frontier = new Queue<string>();
        foreach (string start in starts)
        {
            if (contains(start) && reached.TryAdd(start, new ReachedNode(start, 0)))
                frontier.Enqueue(start);
        }

        while (frontier.Count > 0)
        {
            string current = frontier.Dequeue();
            int currentHop = reached[current].Hop;
            if (currentHop >= maxDepth)
            {
                if (!truncatedByDepth && currentHop == maxDepth &&
                    neighbours(current, direction).Any(neighbour => !reached.ContainsKey(neighbour.Id)))
                    truncatedByDepth = true;
                continue;
            }

            GraphNeighbour[] adjacent = neighbours(current, direction).ToArray();
            int nextHop = currentHop + 1;
            foreach (GraphNeighbour neighbour in adjacent)
            {
                var candidate = new ReachedNode(
                    neighbour.Id,
                    nextHop,
                    current,
                    neighbour.EdgeKind,
                    neighbour.EdgeConfidence,
                    neighbour.EdgeSource,
                    neighbour.Centrality,
                    neighbour.Visibility);
                if (!reached.TryGetValue(neighbour.Id, out ReachedNode? existing))
                {
                    reached[neighbour.Id] = candidate;
                    frontier.Enqueue(neighbour.Id);
                }
                else if (existing.Hop == nextHop && BetterEvidence(candidate, existing))
                {
                    reached[neighbour.Id] = candidate;
                }
            }
        }
        return reached;
    }

    private static bool BetterEvidence(ReachedNode candidate, ReachedNode current)
    {
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
