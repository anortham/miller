using Miller.Testing;
using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Parsing;

public sealed class ContinuousTestStatusSummaryTests
{
    [Fact]
    public void Build_empty_summary_has_zero_counts_and_empty_details()
    {
        var summary = ContinuousTestStatusSummarizer.Build("ws:1", []);

        Assert.Equal("ws:1", summary.WorkspaceId);
        Assert.Equal(0, summary.Counts.Total);
        Assert.Equal(0, summary.Counts.Green);
        Assert.Equal(0, summary.Counts.Red);
        Assert.Empty(summary.Reds);
        Assert.Empty(summary.Running);
        Assert.Equal(0, summary.Stale.Count);
        Assert.Empty(summary.Stale.Samples);
    }

    [Fact]
    public void Build_counts_and_bounds_status_details_deterministically()
    {
        var statuses = new[]
        {
            Status("ws:1", "test:stale-z", ContinuousTestState.Stale, staleSinceRevision: "rev-2"),
            Status("ws:1", "test:red-b", ContinuousTestState.Red, lastRunRevision: "rev-3",
                lastResultStatus: "failed", failureSummary: "assert failed\nfull stack trace", flakinessScore: 0.25),
            Status("ws:1", "test:running-c", ContinuousTestState.Running,
                runningRunId: "run:2", runningRevision: "rev-4"),
            Status("ws:1", "test:green-a", ContinuousTestState.Green, lastRunRevision: "rev-3"),
            Status("ws:1", "test:stale-a", ContinuousTestState.Stale, staleSinceRevision: "rev-5"),
            Status("ws:1", "test:skipped", ContinuousTestState.Skipped, lastRunRevision: "rev-1"),
        };

        var summary = ContinuousTestStatusSummarizer.Build("ws:1", statuses, maxItems: 1);

        Assert.Equal(6, summary.Counts.Total);
        Assert.Equal(1, summary.Counts.Green);
        Assert.Equal(1, summary.Counts.Red);
        Assert.Equal(1, summary.Counts.Running);
        Assert.Equal(2, summary.Counts.Stale);
        Assert.Equal(1, summary.Counts.Skipped);
        Assert.Equal("rev-3", summary.RevisionWatermarks.LastRunRevision);
        Assert.Equal("rev-4", summary.RevisionWatermarks.RunningRevision);
        Assert.Equal("rev-5", summary.RevisionWatermarks.StaleSinceRevision);

        var red = Assert.Single(summary.Reds);
        Assert.Equal("test:red-b", red.TestCaseId);
        Assert.Equal("assert failed", red.FailureSummary);
        Assert.Equal(0.25, red.FlakinessScore);

        var running = Assert.Single(summary.Running);
        Assert.Equal("test:running-c", running.TestCaseId);
        Assert.Equal("run:2", running.RunningRunId);

        Assert.Equal(2, summary.Stale.Count);
        var stale = Assert.Single(summary.Stale.Samples);
        Assert.Equal("test:stale-a", stale.TestCaseId);
        Assert.Equal("rev-5", stale.StaleSinceRevision);
    }

    [Fact]
    public void Running_last_failed_counts_running_cases_whose_last_result_failed_without_double_counting_red()
    {
        var statuses = new[]
        {
            Status("ws:1", "test:rerun-failed", ContinuousTestState.Running,
                runningRunId: "run:1", runningRevision: "rev-2", lastResultStatus: "failed"),
            Status("ws:1", "test:rerun-passed", ContinuousTestState.Running,
                runningRunId: "run:2", runningRevision: "rev-2", lastResultStatus: "passed"),
            Status("ws:1", "test:rerun-fresh", ContinuousTestState.Running,
                runningRunId: "run:3", runningRevision: "rev-2"),
            Status("ws:1", "test:red", ContinuousTestState.Red, lastRunRevision: "rev-1",
                lastResultStatus: "failed"),
        };

        var summary = ContinuousTestStatusSummarizer.Build("ws:1", statuses);

        Assert.Equal(3, summary.Counts.Running);
        Assert.Equal(1, summary.Counts.RunningLastFailed);
        Assert.Equal(1, summary.Counts.Red);
        Assert.Equal(4, summary.Counts.Total);
    }

    [Fact]
    public void Sum_counts_aggregates_running_last_failed_across_workspaces()
    {
        var first = ContinuousTestStatusSummarizer.Build("ws:1",
        [
            Status("ws:1", "test:a", ContinuousTestState.Running, runningRunId: "run:1",
                lastResultStatus: "failed"),
        ]);
        var second = ContinuousTestStatusSummarizer.Build("ws:2",
        [
            Status("ws:2", "test:b", ContinuousTestState.Running, runningRunId: "run:2",
                lastResultStatus: "failed"),
            Status("ws:2", "test:c", ContinuousTestState.Running, runningRunId: "run:3",
                lastResultStatus: "passed"),
        ]);

        var totals = ContinuousTestStatusSummarizer.SumCounts([first, second]);

        Assert.Equal(3, totals.Running);
        Assert.Equal(2, totals.RunningLastFailed);
    }

    [Fact]
    public void Verdict_precedence_is_red_unknown_partial_green()
    {
        var degraded = new ContinuousTestWatchHealthSnapshot(
            State: "degraded",
            ObservedRevision: "42",
            LastSuccessAt: null,
            LastErrorAt: DateTimeOffset.Parse("2026-07-09T12:01:00Z"),
            ErrorCode: "miller_refresh_failed");
        var healthy = new ContinuousTestWatchHealthSnapshot(
            State: "healthy",
            ObservedRevision: "42",
            LastSuccessAt: DateTimeOffset.Parse("2026-07-09T12:00:00Z"),
            LastErrorAt: null,
            ErrorCode: null);

        Assert.Equal(
            ContinuousTestVerdict.Red,
            ContinuousTestStatusSummarizer.Build(
                "ws:1",
                [Status("ws:1", "test:red", ContinuousTestState.Red)],
                watchHealth: healthy).Verdict);
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestStatusSummarizer.Build(
                "ws:1",
                [Status("ws:1", "test:cached", ContinuousTestState.Green, provenFreshRevision: 42)],
                watchHealth: degraded).Verdict);
        Assert.Equal(
            ContinuousTestVerdict.Partial,
            ContinuousTestStatusSummarizer.Build(
                "ws:1",
                [Status("ws:1", "test:stale", ContinuousTestState.Stale, provenFreshRevision: 41)],
                watchHealth: healthy).Verdict);
        Assert.Equal(
            ContinuousTestVerdict.Green,
            ContinuousTestStatusSummarizer.Build(
                "ws:1",
                [Status("ws:1", "test:green", ContinuousTestState.Green, provenFreshRevision: 42)],
                watchHealth: healthy).Verdict);
    }

    [Fact]
    public void Aggregate_complete_at_key_is_passthrough_for_one_workspace_and_null_across_workspaces()
    {
        var healthy = new ContinuousTestWatchHealthSnapshot("healthy", "5083", DateTimeOffset.UtcNow, null, null);
        var alpha = ContinuousTestStatusSummarizer.Build(
            "ws:alpha",
            [Status("ws:alpha", "test:a", ContinuousTestState.Green, provenFreshRevision: 5083)],
            watchHealth: healthy);
        var beta = ContinuousTestStatusSummarizer.Build(
            "ws:beta",
            [Status("ws:beta", "test:b", ContinuousTestState.Green, provenFreshRevision: 12)],
            watchHealth: new ContinuousTestWatchHealthSnapshot("healthy", "12", DateTimeOffset.UtcNow, null, null));

        var single = ContinuousTestStatusSummarizer.Aggregate([alpha]);
        Assert.Equal(new CtFreshnessKey("store:test", 5083), single.CompleteAtKey);
        Assert.Equal(5083, single.CompleteAtRevision);

        Assert.Null(ContinuousTestStatusSummarizer.Aggregate([alpha, beta]).CompleteAtKey);
        Assert.Null(ContinuousTestStatusSummarizer.Aggregate([alpha, beta]).CompleteAtRevision);
    }

    [Fact]
    public void Complete_at_key_requires_matching_proven_freshness()
    {
        var healthy = new ContinuousTestWatchHealthSnapshot("healthy", "42", DateTimeOffset.UtcNow, null, null);
        var complete = ContinuousTestStatusSummarizer.Build(
            "ws:1",
            [
                Status("ws:1", "test:a", ContinuousTestState.Green, provenFreshRevision: 42),
                Status("ws:1", "test:b", ContinuousTestState.Green, provenFreshRevision: 42),
            ],
            watchHealth: healthy);
        var mismatched = ContinuousTestStatusSummarizer.Build(
            "ws:1",
            [
                Status("ws:1", "test:a", ContinuousTestState.Green, provenFreshRevision: 42),
                Status("ws:1", "test:b", ContinuousTestState.Green, provenFreshRevision: 44),
            ],
            watchHealth: healthy);
        var incomplete = ContinuousTestStatusSummarizer.Build(
            "ws:1",
            [
                Status("ws:1", "test:a", ContinuousTestState.Green, provenFreshRevision: 42),
                Status("ws:1", "test:b", ContinuousTestState.Stale, provenFreshRevision: 41),
            ],
            watchHealth: healthy);

        Assert.Equal(new CtFreshnessKey("store:test", 42), complete.CompleteAtKey);
        Assert.Equal(42, complete.CompleteAtRevision);
        Assert.Null(mismatched.CompleteAtKey);
        Assert.Null(incomplete.CompleteAtKey);
        Assert.Equal("miller_watched_files", complete.FreshnessBasis);
        Assert.Equal(ContinuousTestVerdict.Partial, mismatched.Verdict);
    }

    private static ContinuousTestStatus Status(
        string workspaceId,
        string testCaseId,
        ContinuousTestState state,
        string? lastRunRevision = null,
        string? staleSinceRevision = null,
        string? runningRunId = null,
        string? runningRevision = null,
        string? lastResultStatus = null,
        string? failureSummary = null,
        double flakinessScore = 0.0,
        long? provenFreshRevision = null)
    {
        var key = provenFreshRevision is long revision
            ? new CtFreshnessKey("store:test", revision)
            : (CtFreshnessKey?)null;
        return new ContinuousTestStatus(
            WorkspaceId: workspaceId,
            TestCaseId: testCaseId,
            State: state,
            IndexIdentity: "store:test",
            Revision: provenFreshRevision ?? 0,
            LastRunRevision: lastRunRevision,
            StaleSinceRevision: staleSinceRevision,
            RunningRunId: runningRunId,
            RunningRevision: runningRevision,
            LastResultStatus: lastResultStatus,
            FailureSummary: failureSummary,
            FlakinessScore: flakinessScore,
            ProvenFreshKey: key);
    }
}
