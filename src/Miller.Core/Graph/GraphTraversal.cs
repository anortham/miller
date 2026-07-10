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

        // Preserve the historical Reach contract: non-positive depth/limit yield an empty result
        // without neighbour lookups. ContextTool uses maxHops=0 as a seed-only probe; probing
        // neighbours here would add wasted SQLite work and diverge from that hop-0 contract.
        if (maxDepth <= 0 || limit <= 0)
            return new GraphReachResult([], 0, TruncatedByDepth: false, TruncatedByLimit: false);

        Dictionary<string, int> hop = Explore(
            starts, maxDepth, direction, contains, neighbours,
            probeDepthTruncation: true, out bool truncatedByDepth);

        // ReachedCount is the pre-limit size, so the evidence path must materialize the whole set.
        ReachedNode[] reached = Ordered(hop).ToArray();

        return new GraphReachResult(
            reached.Take(limit).ToArray(),
            reached.Length,
            truncatedByDepth,
            reached.Length > limit);
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
