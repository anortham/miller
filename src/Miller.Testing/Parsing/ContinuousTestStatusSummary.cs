namespace Miller.Testing.Parsing;

public sealed record ContinuousTestStatusSummary(
    string WorkspaceId,
    ContinuousTestStatusCounts Counts,
    ContinuousTestRevisionWatermarks RevisionWatermarks,
    IReadOnlyList<ContinuousTestRedStatus> Reds,
    IReadOnlyList<ContinuousTestRunningStatus> Running,
    ContinuousTestStaleSummary Stale,
    ContinuousTestVerdict Verdict,
    CtFreshnessKey? CompleteAtKey,
    string FreshnessBasis,
    ContinuousTestWatchHealthSnapshot Watch)
{
    public long? CompleteAtRevision => CompleteAtKey?.Revision;
}

public sealed record ContinuousTestAggregateStatus(
    ContinuousTestVerdict Verdict,
    CtFreshnessKey? CompleteAtKey,
    string FreshnessBasis,
    ContinuousTestWatchHealthSnapshot Watch)
{
    public long? CompleteAtRevision => CompleteAtKey?.Revision;
}

public sealed record ContinuousTestStatusCounts(
    int Unknown,
    int Green,
    int Red,
    int Skipped,
    int Running,
    int Stale,
    int RunningLastFailed)
{
    public int Total => Unknown + Green + Red + Skipped + Running + Stale;

    public static ContinuousTestStatusCounts Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

public sealed record ContinuousTestRevisionWatermarks(
    string? LastRunRevision,
    string? RunningRevision,
    string? StaleSinceRevision);

public sealed record ContinuousTestRedStatus(
    string TestCaseId,
    string? LastRunRevision,
    string? LastResultStatus,
    string? FailureSummary,
    double FlakinessScore);

public sealed record ContinuousTestRunningStatus(
    string TestCaseId,
    string? RunningRunId,
    string? RunningRevision);

public sealed record ContinuousTestStaleSummary(
    int Count,
    IReadOnlyList<ContinuousTestStaleStatus> Samples);

public sealed record ContinuousTestStaleStatus(
    string TestCaseId,
    string? StaleSinceRevision);

public static class ContinuousTestStatusSummarizer
{
    public const string MillerWatchedFilesFreshnessBasis = "miller_watched_files";
    private const int DefaultMaxItems = 5;
    private const int MaxAllowedItems = 20;
    private const int FailureSummaryMaxChars = 160;

    /// <summary>
    /// Builds one workspace's summary. <paramref name="liveKey"/> is the LIVE index cursor's
    /// freshness key; the verdict is judged against it through
    /// <see cref="ContinuousTestStatusProjection"/> — never against a key derived from the stored
    /// rows themselves. No live key means the verdict is honest
    /// <see cref="ContinuousTestVerdict.Unknown"/>.
    /// </summary>
    public static ContinuousTestStatusSummary Build(
        string workspaceId,
        IReadOnlyList<ContinuousTestStatus> statuses,
        CtFreshnessKey? liveKey = null,
        int maxItems = DefaultMaxItems,
        ContinuousTestWatchHealthSnapshot? watchHealth = null,
        IReadOnlyDictionary<string, CtFreshnessKey>? watermarks = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(statuses);

        var boundedMaxItems = Math.Clamp(maxItems, 1, MaxAllowedItems);
        var workspaceStatuses = statuses
            .Where(row => string.Equals(row.WorkspaceId, workspaceId, StringComparison.Ordinal))
            .OrderBy(row => row.TestCaseId, StringComparer.Ordinal)
            .ToArray();
        var counts = Counts(workspaceStatuses);
        var watch = watchHealth ?? UnknownWatch();
        var completeAtKey = ContinuousTestFreshness.CompleteAt(workspaceStatuses);
        var projected = ContinuousTestStatusProjection.Project(
            liveKey,
            workspaceStatuses,
            watermarks,
            watchHealthy: string.Equals(watch.State, "healthy", StringComparison.Ordinal));

        return new ContinuousTestStatusSummary(
            WorkspaceId: workspaceId,
            Counts: counts,
            RevisionWatermarks: new ContinuousTestRevisionWatermarks(
                LastRunRevision: LatestRevision(workspaceStatuses.Select(row => row.LastRunRevision)),
                RunningRevision: LatestRevision(workspaceStatuses.Select(row => row.RunningRevision)),
                StaleSinceRevision: LatestRevision(workspaceStatuses.Select(row => row.StaleSinceRevision))),
            Reds: workspaceStatuses
                .Where(row => row.State == ContinuousTestState.Red)
                .Take(boundedMaxItems)
                .Select(row => new ContinuousTestRedStatus(
                    TestCaseId: row.TestCaseId,
                    LastRunRevision: row.LastRunRevision,
                    LastResultStatus: row.LastResultStatus,
                    FailureSummary: FirstLine(row.FailureSummary),
                    FlakinessScore: row.FlakinessScore))
                .ToArray(),
            Running: workspaceStatuses
                .Where(row => row.State == ContinuousTestState.Running)
                .Take(boundedMaxItems)
                .Select(row => new ContinuousTestRunningStatus(
                    TestCaseId: row.TestCaseId,
                    RunningRunId: row.RunningRunId,
                    RunningRevision: row.RunningRevision))
                .ToArray(),
            Stale: new ContinuousTestStaleSummary(
                Count: workspaceStatuses.Count(row => row.State == ContinuousTestState.Stale),
                Samples: workspaceStatuses
                    .Where(row => row.State == ContinuousTestState.Stale)
                    .Take(boundedMaxItems)
                    .Select(row => new ContinuousTestStaleStatus(
                        TestCaseId: row.TestCaseId,
                        StaleSinceRevision: row.StaleSinceRevision))
                    .ToArray()),
            Verdict: projected.Verdict,
            CompleteAtKey: completeAtKey,
            FreshnessBasis: MillerWatchedFilesFreshnessBasis,
            Watch: watch);
    }

    public static ContinuousTestAggregateStatus Aggregate(IEnumerable<ContinuousTestStatusSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var rows = summaries.ToArray();
        var watch = AggregateWatch(rows);
        CtFreshnessKey? completeAtKey = rows.Length == 1 ? rows[0].CompleteAtKey : null;
        var verdict = rows.Length == 0
            ? ContinuousTestVerdict.Unknown
            : rows.Select(row => row.Verdict).OrderByDescending(VerdictPrecedence).First();
        return new ContinuousTestAggregateStatus(
            verdict,
            completeAtKey,
            MillerWatchedFilesFreshnessBasis,
            watch);
    }

    public static ContinuousTestStatusCounts SumCounts(IEnumerable<ContinuousTestStatusSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var rows = summaries.ToArray();
        if (rows.Length == 0)
            return ContinuousTestStatusCounts.Empty;

        return new ContinuousTestStatusCounts(
            Unknown: rows.Sum(row => row.Counts.Unknown),
            Green: rows.Sum(row => row.Counts.Green),
            Red: rows.Sum(row => row.Counts.Red),
            Skipped: rows.Sum(row => row.Counts.Skipped),
            Running: rows.Sum(row => row.Counts.Running),
            Stale: rows.Sum(row => row.Counts.Stale),
            RunningLastFailed: rows.Sum(row => row.Counts.RunningLastFailed));
    }

    private static ContinuousTestStatusCounts Counts(IReadOnlyList<ContinuousTestStatus> statuses) =>
        new(
            Unknown: statuses.Count(row => row.State == ContinuousTestState.Unknown),
            Green: statuses.Count(row => row.State == ContinuousTestState.Green),
            Red: statuses.Count(row => row.State == ContinuousTestState.Red),
            Skipped: statuses.Count(row => row.State == ContinuousTestState.Skipped),
            Running: statuses.Count(row => row.State == ContinuousTestState.Running),
            Stale: statuses.Count(row => row.State == ContinuousTestState.Stale),
            RunningLastFailed: statuses.Count(row =>
                row.State == ContinuousTestState.Running
                && string.Equals(row.LastResultStatus, "failed", StringComparison.Ordinal)));

    private static ContinuousTestWatchHealthSnapshot AggregateWatch(
        IReadOnlyList<ContinuousTestStatusSummary> summaries)
    {
        if (summaries.Count == 0)
            return UnknownWatch();

        var state = summaries.Any(row => string.Equals(row.Watch.State, "degraded", StringComparison.Ordinal))
            ? "degraded"
            : summaries.All(row => string.Equals(row.Watch.State, "healthy", StringComparison.Ordinal))
                ? "healthy"
                : "unknown";
        var observed = summaries
            .Select(row => row.Watch.ObservedRevision)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new ContinuousTestWatchHealthSnapshot(
            State: state,
            ObservedRevision: observed.Length == 1 ? observed[0] : null,
            LastSuccessAt: summaries.Max(row => row.Watch.LastSuccessAt),
            LastErrorAt: summaries.Max(row => row.Watch.LastErrorAt),
            ErrorCode: summaries
                .Where(row => row.Watch.LastErrorAt is not null)
                .OrderByDescending(row => row.Watch.LastErrorAt)
                .Select(row => row.Watch.ErrorCode)
                .FirstOrDefault());
    }

    private static int VerdictPrecedence(ContinuousTestVerdict verdict) => verdict switch
    {
        ContinuousTestVerdict.Red => 4,
        ContinuousTestVerdict.Unknown => 3,
        ContinuousTestVerdict.Partial => 2,
        ContinuousTestVerdict.Green => 1,
        _ => 0,
    };

    private static ContinuousTestWatchHealthSnapshot UnknownWatch() =>
        new("unknown", null, null, null, null);

    private static string? LatestRevision(IEnumerable<string?> revisions) =>
        revisions
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .LastOrDefault();

    private static string? FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var line = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', 2)[0]
            .Trim();
        return line.Length <= FailureSummaryMaxChars ? line : line[..FailureSummaryMaxChars];
    }
}
