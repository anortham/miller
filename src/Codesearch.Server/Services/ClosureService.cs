namespace Codesearch.Server.Services;

/// <summary>
/// Service for computing transitive closure (reachability) for impact analysis.
/// </summary>
internal class ClosureService
{
    private readonly SearchService _searchService;

    public ClosureService(SearchService searchService)
    {
        _searchService = searchService;
    }

    /// <summary>
    /// Compute transitive closure from all relationships.
    /// Enables O(1) impact analysis queries.
    /// </summary>
    /// <param name="maxDepth">Maximum path length to compute</param>
    /// <param name="relationships">Pre-fetched relationships to use (if available)</param>
    /// <returns>Number of reachability entries created</returns>
    public int ComputeTransitiveClosure(
        int maxDepth = 10,
        List<(string FromId, string ToId)>? relationships = null)
    {
        // Clear existing reachability
        _searchService.ClearReachability();

        if (relationships == null || relationships.Count == 0)
        {
            // No relationships provided - can't compute
            return 0;
        }

        // Build adjacency list (who calls whom)
        var downstream = new Dictionary<string, HashSet<string>>();
        var allSymbols = new HashSet<string>();

        foreach (var (fromId, toId) in relationships)
        {
            if (!downstream.TryGetValue(fromId, out var targets))
            {
                targets = new HashSet<string>();
                downstream[fromId] = targets;
            }
            targets.Add(toId);
            allSymbols.Add(fromId);
            allSymbols.Add(toId);
        }

        // BFS from each symbol to compute reachability
        var entries = new List<uniffi.codesearch_ffi.ReachabilityEntry>();

        foreach (var startSymbol in allSymbols)
        {
            if (!downstream.ContainsKey(startSymbol))
            {
                continue;  // No outgoing edges
            }

            var visited = new Dictionary<string, int> { { startSymbol, 0 } };
            var queue = new Queue<(string symbol, int depth)>();
            queue.Enqueue((startSymbol, 0));

            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();

                if (depth >= maxDepth)
                {
                    continue;
                }

                if (!downstream.TryGetValue(current, out var neighbors))
                {
                    continue;
                }

                foreach (var neighbor in neighbors)
                {
                    if (!visited.ContainsKey(neighbor))
                    {
                        var newDepth = depth + 1;
                        visited[neighbor] = newDepth;
                        queue.Enqueue((neighbor, newDepth));
                        entries.Add(new uniffi.codesearch_ffi.ReachabilityEntry(
                            sourceId: startSymbol,
                            targetId: neighbor,
                            minDistance: (uint)newDepth
                        ));
                    }
                }
            }
        }

        // Bulk insert
        if (entries.Count > 0)
        {
            _searchService.AddReachabilityBatch(entries);
        }

        return entries.Count;
    }
}
