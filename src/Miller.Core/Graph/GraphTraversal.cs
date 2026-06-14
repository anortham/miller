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
                continue;

            int nextHop = currentHop + 1;
            foreach (string neighbour in neighbours(current, direction))
            {
                if (hop.ContainsKey(neighbour))
                    continue;
                hop[neighbour] = nextHop;
                frontier.Enqueue(neighbour);
            }
        }

        return hop
            .Where(static kv => kv.Value > 0)
            .OrderBy(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(static kv => new ReachedNode(kv.Key, kv.Value))
            .ToArray();
    }

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
