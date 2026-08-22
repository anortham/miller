using Miller.Indexing;

namespace Miller.Server.Tools;

/// <summary>
/// One context call's lexical symbol retrievals against ONE index, memoized by (query, limit, excludeTests).
/// </summary>
/// <remarks>
/// Several call sites in a single context call ask the same index the same question — the term-rescue loop and
/// the promotion scan repeat every query term, and <c>ContextSearchCacheLookupIndex</c> keys on
/// (query, limit, mode), so a retrieval that differs only in its test policy misses that cache and runs again.
/// One memo above those call sites removes the duplicates. The key carries every argument the retrieval reads,
/// so two callers share a result only when the retrieval they asked for is the same retrieval. The INDEX is not
/// part of the key: it is fixed at construction, so a memo can never serve one index's result for another's
/// question. Use <see cref="For"/> to reuse a caller's memo only when it belongs to the same index.
/// </remarks>
internal sealed class ContextQueryRetrieval
{
    private readonly Dictionary<RetrievalKey, SymbolCandidateSet> _retrievals = [];

    internal ContextQueryRetrieval(ISymbolLookupIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        Index = index;
    }

    /// <summary>The one index every memoized retrieval was made against.</summary>
    internal ISymbolLookupIndex Index { get; }

    /// <summary>Distinct retrievals actually run — the seam that proves a call site shares rather than repeats.</summary>
    internal int RetrievalCount { get; private set; }

    /// <summary>
    /// Reuse <paramref name="existing"/> when it was built over <paramref name="index"/>, otherwise start a memo
    /// for this index. A caller that passes a memo for a different index gets a fresh one instead of another
    /// index's answers.
    /// </summary>
    internal static ContextQueryRetrieval For(ISymbolLookupIndex index, ContextQueryRetrieval? existing) =>
        existing is not null && ReferenceEquals(existing.Index, index) ? existing : new ContextQueryRetrieval(index);

    /// <summary>Retrieve symbol candidates, reusing an identical retrieval already made for this call.</summary>
    internal SymbolCandidateSet Collect(string query, int limit, bool excludeTests)
    {
        var key = new RetrievalKey(query, limit, excludeTests);
        if (_retrievals.TryGetValue(key, out SymbolCandidateSet? cached))
            return cached;

        SymbolCandidateSet collected = SearchTool.CollectSymbolCandidates(
            Index,
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
