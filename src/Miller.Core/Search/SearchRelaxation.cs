namespace Miller.Core.Search;

/// <summary>The strict search mode and optional relaxed fallback selected for one query.</summary>
public sealed record SearchRelaxationDecision(
    SearchMode PrimaryMode,
    SearchMode? FallbackMode,
    bool Relaxed);

/// <summary>Pure strict-first search relaxation and stable result merging.</summary>
public static class SearchRelaxation
{
    /// <summary>Count the distinct code-aware terms that strict search must match.</summary>
    public static int DistinctTermCount(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query
            .Split(
                [' ', '\t', '\r', '\n', '-', '.', ':'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    /// <summary>Relax multi-term queries only when strict results cannot fill the requested page.</summary>
    public static SearchRelaxationDecision Decide(
        int distinctQueryTerms,
        int strictVisibleResults,
        int requestedLimit)
    {
        bool multiTerm = distinctQueryTerms > 1;
        bool relax =
            multiTerm &&
            strictVisibleResults < Math.Max(1, requestedLimit);
        return new SearchRelaxationDecision(
            multiTerm ? SearchMode.And : SearchMode.Or,
            relax ? SearchMode.Or : null,
            relax);
    }

    /// <summary>Keep strict order, then append unique relaxed rows until the requested limit is filled.</summary>
    public static IReadOnlyList<SymbolCandidate> Merge(
        IReadOnlyList<SymbolCandidate> strict,
        IReadOnlyList<SymbolCandidate> relaxed,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(strict);
        ArgumentNullException.ThrowIfNull(relaxed);
        if (limit <= 0)
            return [];

        var merged = new List<SymbolCandidate>(Math.Min(limit, strict.Count + relaxed.Count));
        var seen = new HashSet<int>();
        foreach (SymbolCandidate candidate in strict)
        {
            if (seen.Add(candidate.DocId))
                merged.Add(candidate);
            if (merged.Count == limit)
                return merged;
        }
        foreach (SymbolCandidate candidate in relaxed)
        {
            if (seen.Add(candidate.DocId))
                merged.Add(candidate);
            if (merged.Count == limit)
                break;
        }
        return merged;
    }
}
