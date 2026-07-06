using Miller.Indexing;
using Miller.Server.Git;

namespace Miller.Server.Tools;

/// <summary>
/// Deterministic churn×complexity risk ranking: a risk row exists only where churn evidence and
/// complexity evidence intersect. Churn-only rows stay in `metrics churn`; complexity-only rows
/// stay in `metrics complexity`. Churn and complexity are joined over their FULL sets before the
/// final limit is applied, so a low-churn/high-complexity symbol can outrank a high-churn/trivial one.
/// </summary>
public static class RiskRanking
{
    public const string ScoreFormula = "commit_count * (decision_count + loop_count + max_nesting_depth)";

    public static RiskReport Read(
        string symbolsDbPath,
        string workspaceRoot,
        string range,
        int limit,
        bool includeTests,
        IGitHistoryReader historyReader)
    {
        ChurnReport churn = GitChurnAnalyzer.Read(
            symbolsDbPath,
            workspaceRoot,
            range,
            limit: int.MaxValue,
            includeCommits: false,
            historyReader);

        string[] paths = churn.Rows.Select(static row => row.Path).Distinct(StringComparer.Ordinal).ToArray();
        IReadOnlyList<ComplexityHotspot> complexity = paths.Length == 0
            ? []
            : ComplexityRankingReader.ReadForPaths(symbolsDbPath, paths, includeTests);

        return new RiskReport(range, Join(churn.Rows, complexity, limit));
    }

    internal static IReadOnlyList<RiskRow> Join(
        IReadOnlyList<ChurnRow> churnRows,
        IReadOnlyList<ComplexityHotspot> complexityRows,
        int limit)
    {
        if (limit < 1)
            limit = 1;

        Dictionary<string, ComplexityAggregate> bySymbol = Aggregate(
            complexityRows.Where(static row => row.SymbolId is not null),
            static row => row.SymbolId!);
        Dictionary<string, ComplexityAggregate> byPath = Aggregate(complexityRows, static row => row.Path);

        var rows = new List<RiskRow>();
        foreach (ChurnRow churn in churnRows)
        {
            ComplexityAggregate aggregate;
            string basis;
            if (churn.SymbolId is not null && bySymbol.TryGetValue(churn.SymbolId, out aggregate))
                basis = "symbol";
            else if (churn.SymbolId is null && byPath.TryGetValue(churn.Path, out aggregate))
                basis = "file";
            else
                continue;

            long score = churn.CommitCount
                * ((long)aggregate.DecisionCount + aggregate.LoopCount + aggregate.MaxNestingDepth);
            rows.Add(new RiskRow(
                basis,
                churn.SymbolId,
                churn.SymbolName,
                churn.SymbolKind,
                churn.Path,
                churn.Line,
                churn.CommitCount,
                churn.ChangedLines,
                churn.LastCommitAtUtc,
                aggregate.DecisionCount,
                aggregate.LoopCount,
                aggregate.MaxNestingDepth,
                ComplexityRankingReader.Classify(aggregate.DecisionCount, aggregate.MaxNestingDepth),
                aggregate.IsTest,
                score));
        }

        return rows
            .OrderByDescending(static row => row.Score)
            .ThenByDescending(static row => row.ChangedLines)
            .ThenByDescending(static row => row.CommitCount)
            .ThenBy(static row => row.Path, StringComparer.Ordinal)
            .ThenBy(static row => row.SymbolName, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static Dictionary<string, ComplexityAggregate> Aggregate(
        IEnumerable<ComplexityHotspot> rows,
        Func<ComplexityHotspot, string> keySelector)
    {
        var aggregates = new Dictionary<string, ComplexityAggregate>(StringComparer.Ordinal);
        foreach (ComplexityHotspot row in rows)
        {
            string key = keySelector(row);
            aggregates[key] = aggregates.TryGetValue(key, out ComplexityAggregate existing)
                ? new ComplexityAggregate(
                    existing.DecisionCount + row.DecisionCount,
                    existing.LoopCount + row.LoopCount,
                    Math.Max(existing.MaxNestingDepth, row.MaxNestingDepth),
                    existing.IsTest || row.IsTest)
                : new ComplexityAggregate(row.DecisionCount, row.LoopCount, row.MaxNestingDepth, row.IsTest);
        }
        return aggregates;
    }

    private readonly record struct ComplexityAggregate(
        int DecisionCount,
        int LoopCount,
        int MaxNestingDepth,
        bool IsTest);
}

public sealed record RiskReport(string Range, IReadOnlyList<RiskRow> Rows);

public sealed record RiskRow(
    string Basis,
    string? SymbolId,
    string? SymbolName,
    string? SymbolKind,
    string Path,
    int? Line,
    int CommitCount,
    int ChangedLines,
    DateTimeOffset LastCommitAtUtc,
    int DecisionCount,
    int LoopCount,
    int MaxNestingDepth,
    ComplexitySeverity Severity,
    bool IsTest,
    long Score);
