using Miller.Indexing;

namespace Miller.Server.Tools;

/// <summary>
/// One context call's lexical symbol retrievals, memoized by (query, limit, excludeTests).
/// </summary>
/// <remarks>
/// The semantic-seed gate and the pivot ranker ask the same question of the same index. They used to ask it
/// twice with different limits, and <c>ContextSearchCacheLookupIndex</c> keys on (query, limit, mode), so the
/// second ask missed that cache and ran the whole retrieval again. One memo above both call sites removes the
/// duplicate. The key carries every argument the retrieval reads, so two callers share a result only when the
/// retrieval they asked for is the same retrieval.
/// </remarks>
internal sealed class ContextQueryRetrieval
{
    private readonly Dictionary<RetrievalKey, SymbolCandidateSet> _retrievals = [];

    /// <summary>Distinct retrievals actually run — the seam that proves a call site shares rather than repeats.</summary>
    internal int RetrievalCount { get; private set; }

    /// <summary>Retrieve symbol candidates, reusing an identical retrieval already made for this call.</summary>
    internal SymbolCandidateSet Collect(
        ISymbolLookupIndex index,
        string query,
        int limit,
        bool excludeTests)
    {
        var key = new RetrievalKey(query, limit, excludeTests);
        if (_retrievals.TryGetValue(key, out SymbolCandidateSet? cached))
            return cached;

        SymbolCandidateSet collected = SearchTool.CollectSymbolCandidates(
            index,
            query,
            SearchToolMode.Symbol,
            limit,
            excludeTests);
        RetrievalCount++;
        _retrievals[key] = collected;
        return collected;
    }

    private readonly record struct RetrievalKey(string Query, int Limit, bool ExcludeTests);
}
