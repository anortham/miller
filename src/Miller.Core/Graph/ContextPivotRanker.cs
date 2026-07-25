namespace Miller.Core.Graph;

/// <summary>One retrieval or task-anchor signal contributing to a context pivot.</summary>
public sealed record ContextPivotSignal(
    string SymbolId,
    int RetrievalRank,
    double RetrievalScore,
    int AnchorStrength,
    int AnchorOrder,
    int? LineDistance = null,
    string? DiversityKey = null,
    string? FilePath = null,
    bool IsTest = false,
    bool IsPinned = false);

/// <summary>A merged context pivot with the evidence that determined its position.</summary>
public sealed record ContextPivot(
    string SymbolId,
    int RetrievalRank,
    double RetrievalScore,
    int AnchorStrength,
    int AnchorOrder,
    int? LineDistance,
    string DiversityKey,
    string? FilePath,
    bool IsTest,
    bool IsPinned);

/// <summary>Pure deterministic selection of the small symbol set that anchors a context bundle.</summary>
public static class ContextPivotRanker
{
    /// <summary>Merge repeated signals and rank explicit task evidence ahead of retrieval-only evidence.</summary>
    public static IReadOnlyList<ContextPivot> Rank(
        IReadOnlyList<ContextPivotSignal> signals,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (limit <= 0 || signals.Count == 0)
            return [];

        ContextPivot[] ordered = signals
            .GroupBy(static signal => signal.SymbolId, StringComparer.Ordinal)
            .Select(static group => new ContextPivot(
                group.Key,
                group.Min(static signal => signal.RetrievalRank),
                group.Max(static signal => signal.RetrievalScore),
                group.Max(static signal => signal.AnchorStrength),
                group.Min(static signal => signal.AnchorOrder),
                group.Where(static signal => signal.LineDistance is not null)
                    .Select(static signal => signal.LineDistance)
                    .Min(),
                group.Select(static signal => signal.DiversityKey)
                    .FirstOrDefault(static key => !string.IsNullOrEmpty(key)) ?? group.Key,
                group.Select(static signal => signal.FilePath)
                    .FirstOrDefault(static path => !string.IsNullOrEmpty(path)),
                group.Any(static signal => signal.IsTest),
                group.Any(static signal => signal.IsPinned)))
            .OrderByDescending(static pivot => pivot.AnchorStrength)
            .ThenBy(static pivot => pivot.LineDistance ?? int.MaxValue)
            .ThenBy(static pivot => pivot.RetrievalRank)
            .ThenByDescending(static pivot => pivot.RetrievalScore)
            .ThenBy(static pivot => pivot.AnchorOrder)
            .ThenBy(static pivot => pivot.SymbolId, StringComparer.Ordinal)
            .ToArray();

        var selected = new List<ContextPivot>(limit);
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var diversityKeys = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        var diversityKeyFiles = new HashSet<(string DiversityKey, string FilePath)>();
        var fileCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int diverseFileTarget = Math.Max(1, (limit + 1) / 2);
        AddWhere(static (pivot, _) => pivot.IsPinned, limit);
        AddWhere(static (pivot, state) =>
            !state.Keys.Contains(pivot.DiversityKey) &&
            (pivot.FilePath is null || !state.Files.Contains(pivot.FilePath)) &&
            (!pivot.IsTest || !state.HasTest),
            diverseFileTarget);
        AddWhere(static (pivot, state) =>
            !state.Keys.Contains(pivot.DiversityKey) &&
            (!pivot.IsTest || !state.HasTest) &&
            (pivot.FilePath is null || state.FileCounts.GetValueOrDefault(pivot.FilePath) < 2),
            limit);
        AddWhere(static (pivot, state) =>
            (!pivot.IsTest || !state.HasTest) &&
            (pivot.FilePath is null ||
             !state.KeyFiles.Contains((pivot.DiversityKey, pivot.FilePath))) &&
            (pivot.FilePath is null || state.FileCounts.GetValueOrDefault(pivot.FilePath) < 2),
            limit);
        AddWhere(static (_, _) => true, limit);
        return selected;

        void AddWhere(
            Func<ContextPivot, (
                HashSet<string> Keys,
                HashSet<string> Files,
                HashSet<(string DiversityKey, string FilePath)> KeyFiles,
                Dictionary<string, int> FileCounts,
                bool HasTest), bool> predicate,
            int phaseLimit)
        {
            foreach (ContextPivot pivot in ordered)
            {
                if (selected.Count >= phaseLimit)
                    return;
                if (selectedIds.Contains(pivot.SymbolId) ||
                    !predicate(
                        pivot,
                        (diversityKeys,
                            files,
                            diversityKeyFiles,
                            fileCounts,
                            selected.Any(static item => item.IsTest))))
                {
                    continue;
                }
                selected.Add(pivot);
                selectedIds.Add(pivot.SymbolId);
                diversityKeys.Add(pivot.DiversityKey);
                if (pivot.FilePath is not null)
                {
                    files.Add(pivot.FilePath);
                    diversityKeyFiles.Add((pivot.DiversityKey, pivot.FilePath));
                    fileCounts[pivot.FilePath] = fileCounts.GetValueOrDefault(pivot.FilePath) + 1;
                }
            }
        }
    }
}
