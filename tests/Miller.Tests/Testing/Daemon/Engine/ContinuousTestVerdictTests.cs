using System.Reflection;
using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestVerdictTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-verdict-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Green_requires_complete_results_at_the_selected_composite_key()
    {
        var key = new CtFreshnessKey("gen-1", 4);
        var statuses = new[]
        {
            Status("test:a", ContinuousTestState.Green, key),
            Status("test:b", ContinuousTestState.Green, key),
        };
        Assert.Equal(ContinuousTestVerdict.Green, ContinuousTestFreshness.Evaluate(statuses, key, watchHealthy: true));
    }

    [Fact]
    public void Known_staleness_is_partial()
    {
        var selected = new CtFreshnessKey("gen-1", 5);
        var proven = new CtFreshnessKey("gen-1", 4);
        var statuses = new[]
        {
            Status("test:a", ContinuousTestState.Stale, selected, proven: null),
        };
        Assert.Equal(ContinuousTestVerdict.Partial, ContinuousTestFreshness.Evaluate(statuses, selected, watchHealthy: true));
        Assert.Equal(
            ContinuousTestVerdict.Partial,
            ContinuousTestFreshness.Evaluate(
                [Status("test:a", ContinuousTestState.Green, proven)],
                selected,
                watchHealthy: true));
    }

    [Fact]
    public void Unknown_watch_or_running_case_is_unknown()
    {
        var key = new CtFreshnessKey("gen-1", 4);
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestFreshness.Evaluate(
                [Status("test:a", ContinuousTestState.Green, key)],
                key,
                watchHealthy: false));
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestFreshness.Evaluate(
                [Status("test:a", ContinuousTestState.Running, key)],
                key,
                watchHealthy: true));
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestFreshness.Evaluate([], key, watchHealthy: true));
    }

    [Fact]
    public void Rebuild_new_index_identity_demotes_prior_green()
    {
        var prior = new CtFreshnessKey("gen-old", 4);
        var rebuilt = new CtFreshnessKey("gen-new", 1);
        var statuses = new[]
        {
            Status("test:a", ContinuousTestState.Green, prior),
        };
        Assert.Equal(ContinuousTestVerdict.Green, ContinuousTestFreshness.Evaluate(statuses, prior, watchHealthy: true));
        Assert.Equal(ContinuousTestVerdict.Partial, ContinuousTestFreshness.Evaluate(statuses, rebuilt, watchHealthy: true));
    }

    [Fact]
    public void Daemon_evaluation_calls_only_the_aggregate_status_path()
    {
        MethodInfo evaluate = typeof(ContinuousTestDaemonHost).GetMethod(
            "Evaluate",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException();
        MethodInfo aggregate = typeof(ContinuousTestStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(ContinuousTestStore.AggregateContinuousTestStatuses));
        MethodInfo detailed = typeof(ContinuousTestStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(ContinuousTestStore.ListContinuousTestStatuses));
        MethodInfo watermarks = typeof(ContinuousTestStore)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method =>
                method.Name == nameof(ContinuousTestStore.ListContinuousTestFreshWatermarks)
                && method.GetParameters().Length == 2);

        Assert.True(ContainsMetadataToken(evaluate, aggregate));
        Assert.False(ContainsMetadataToken(evaluate, detailed));
        Assert.False(ContainsMetadataToken(evaluate, watermarks));
    }

    [Fact]
    public async Task Policy_blocked_run_never_reports_green()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider
        {
            RunException = new ContinuousTestProviderException(
                "Application Control blocked the test host (0x800711C7)."),
        };
        var coordinator = new ContinuousTestCoordinator(provider, store);
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            coordinator);
        queue.Enqueue(EngineTestSupport.Change(workspace));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var statuses = store.ListContinuousTestStatuses(EngineTestSupport.WorkspaceId);
        var selected = new CtFreshnessKey(EngineTestSupport.Identity, 2);
        ContinuousTestVerdict verdict = ContinuousTestFreshness.Evaluate(statuses, selected, watchHealthy: true);
        Assert.NotEqual(ContinuousTestVerdict.Green, verdict);
        Assert.True(verdict is ContinuousTestVerdict.Partial or ContinuousTestVerdict.Unknown);
        Assert.All(statuses, row => Assert.NotEqual(ContinuousTestState.Green, row.State));
    }

    [Fact]
    public void Watch_health_unknown_forces_unknown_verdict()
    {
        var health = new CtWatchHealth();
        health.RecordError("watch_overflow");
        Assert.False(health.IsHealthy);
        Assert.Equal("degraded", health.Snapshot(EngineTestSupport.WorkspaceId).State);
        var key = new CtFreshnessKey("gen-1", 1);
        Assert.Equal(
            ContinuousTestVerdict.Unknown,
            ContinuousTestFreshness.Evaluate(
                [Status("test:a", ContinuousTestState.Green, key)],
                key,
                watchHealthy: health.IsHealthy));
    }

    /// <summary>
    /// The daemon snapshot consumes watermark freshness through the SAME projection foreground
    /// status uses: a green committed at an older revision whose watermark covers the live key
    /// reads green with stale 0, not partial.
    /// </summary>
    [Fact]
    public async Task Daemon_snapshot_treats_watermark_fresh_greens_as_green()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        CommitGreen(store, "test:app", EngineTestSupport.Identity, 2);
        store.ApplyRevisionAdvance(
            EngineTestSupport.WorkspaceId,
            workspace.ProjectPath,
            new CtFreshnessKey(EngineTestSupport.Identity, 2),
            new CtFreshnessKey(EngineTestSupport.Identity, 3),
            [],
            ContinuousTestSelectionOutcome.KnownEmpty);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(new ContinuousTestRevisionObservation(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey(EngineTestSupport.Identity, 3),
            IndexFresh: true,
            Status: "fresh",
            ObservedAt: DateTimeOffset.UtcNow));
        var host = new ContinuousTestDaemonHost(
            workspace.WorkspaceRoot,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                Store = store,
                Queue = QueueFor(store),
                Poller = new ContinuousTestRevisionPoller(source),
                Projects = [new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath, Framework: "xunit")],
                Budget = CtExecutionBudget.Disabled(),
                AcquireLease = false,
                Clock = () => DateTimeOffset.UtcNow,
                Delay = (_, token) => Task.Delay(5, token),
            });

        using var cancellation = new CancellationTokenSource();
        Task run = host.RunAsync(cancellation.Token);
        await WaitUntil(
            () => host.LastSnapshot is { Verdict: ContinuousTestVerdict.Green } snapshot
                && snapshot.Selected == new CtFreshnessKey(EngineTestSupport.Identity, 3),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, host.LastSnapshot!.StaleCount);

        cancellation.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// The daemon judges at the LATEST observed cursor, not the key it started at. A green
    /// committed at the newer revision must read green, exactly as foreground status reads it.
    /// </summary>
    [Fact]
    public async Task Daemon_snapshot_judges_at_the_latest_observed_key()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));

        // Committed at revision 4 with no watermark: green at the latest key (4), stale at the
        // started key (3). The snapshot must read green.
        CommitGreen(store, "test:app", EngineTestSupport.Identity, 4);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(3));
        source.Observations.Enqueue(Observation(4));
        var delay = new ManualDelay();
        var host = new ContinuousTestDaemonHost(
            workspace.WorkspaceRoot,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                Store = store,
                Queue = QueueFor(store),
                Poller = new ContinuousTestRevisionPoller(source),
                Projects = [new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath, Framework: "xunit")],
                Budget = CtExecutionBudget.Disabled(),
                AcquireLease = false,
                Clock = () => DateTimeOffset.UtcNow,
                Delay = delay.DelayAsync,
            });

        using var cancellation = new CancellationTokenSource();
        Task run = host.RunAsync(cancellation.Token);
        await delay.WaitForDelayCountAsync(1, TestContext.Current.CancellationToken);
        delay.CompleteNext();
        await delay.WaitForDelayCountAsync(2, TestContext.Current.CancellationToken);
        delay.CompleteNext();
        await WaitUntil(
            () => host.LastSnapshot is { Verdict: ContinuousTestVerdict.Green } snapshot
                && snapshot.Selected == new CtFreshnessKey(EngineTestSupport.Identity, 4),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, host.LastSnapshot!.StaleCount);

        cancellation.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private static bool ContainsMetadataToken(MethodInfo caller, MethodInfo callee)
    {
        byte[] il = caller.GetMethodBody()?.GetILAsByteArray() ?? [];
        return il.AsSpan().IndexOf(BitConverter.GetBytes(callee.MetadataToken)) >= 0;
    }

    private static ContinuousTestDaemonQueue QueueFor(ContinuousTestStore store) =>
        new(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store));

    private static ContinuousTestRevisionObservation Observation(long revision) =>
        new(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey(EngineTestSupport.Identity, revision),
            IndexFresh: true,
            Status: "fresh",
            ObservedAt: DateTimeOffset.UtcNow);

    private static void CommitGreen(
        ContinuousTestStore store,
        string testCaseId,
        string identity,
        long revision)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string runId = "seed-run:" + testCaseId;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: EngineTestSupport.WorkspaceId,
                Status: "running",
                SelectedRevision: revisionText,
                IndexIdentity: identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: EngineTestSupport.WorkspaceId,
            TestRunId: runId,
            SelectedRevision: revisionText,
            CurrentRevision: revisionText,
            IndexIdentity: identity,
            Revision: revision,
            Status: "passed",
            Results:
            [
                new ContinuousTestResult(
                    Id: runId + ":result",
                    WorkspaceId: EngineTestSupport.WorkspaceId,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: "passed",
                    ResultRevision: revisionText,
                    IndexIdentity: identity,
                    Revision: revision),
            ]));
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("condition was not met");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ContinuousTestStatus Status(
        string id,
        ContinuousTestState state,
        CtFreshnessKey key,
        CtFreshnessKey? proven = null) =>
        new(
            EngineTestSupport.WorkspaceId,
            id,
            state,
            key.IndexIdentity,
            key.Revision,
            ProvenFreshKey: proven ?? (state is ContinuousTestState.Green or ContinuousTestState.Red or ContinuousTestState.Skipped
                ? key
                : null));
}
