using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestIdleDrainTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-idle-drain-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_policy_fires_when_every_guard_is_met()
    {
        var policy = new CtIdleDrainPolicy(Quiet);

        Assert.True(policy.ShouldDrain(AllGuardsMet()));
    }

    [Fact]
    public void The_cooldown_is_a_five_minute_constant()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), CtIdleDrainPolicy.Cooldown);
    }

    [Fact]
    public void No_staleness_means_no_drain()
    {
        var policy = new CtIdleDrainPolicy(Quiet);

        Assert.False(policy.ShouldDrain(AllGuardsMet() with { StaleCount = 0 }));
    }

    [Fact]
    public void Pending_queue_work_blocks_the_drain()
    {
        var policy = new CtIdleDrainPolicy(Quiet);

        Assert.False(policy.ShouldDrain(AllGuardsMet() with { QueueHasPendingWork = true }));
    }

    [Fact]
    public void An_executing_run_blocks_the_drain()
    {
        var policy = new CtIdleDrainPolicy(Quiet);

        Assert.False(policy.ShouldDrain(AllGuardsMet() with { RunExecuting = true }));
    }

    [Fact]
    public void An_unsettled_poll_blocks_the_drain()
    {
        var policy = new CtIdleDrainPolicy(Quiet);

        Assert.False(policy.ShouldDrain(AllGuardsMet() with { PollSettled = false }));
    }

    [Fact]
    public void Paused_auto_runs_block_the_drain()
    {
        var policy = new CtIdleDrainPolicy(Quiet);

        Assert.False(policy.ShouldDrain(AllGuardsMet() with { AutoRunsPaused = true }));
    }

    [Fact]
    public void Activity_inside_the_quiet_window_blocks_the_drain()
    {
        var policy = new CtIdleDrainPolicy(Quiet);

        Assert.False(policy.ShouldDrain(AllGuardsMet() with
        {
            LastActivityAt = T0 - TimeSpan.FromSeconds(1),
        }));
    }

    [Fact]
    public void Unknown_activity_counts_as_quiet()
    {
        var policy = new CtIdleDrainPolicy(Quiet);

        Assert.True(policy.ShouldDrain(AllGuardsMet() with { LastActivityAt = null }));
    }

    [Fact]
    public void A_recent_drain_blocks_the_next_one_until_the_cooldown_elapses()
    {
        var policy = new CtIdleDrainPolicy(Quiet);

        Assert.False(policy.ShouldDrain(AllGuardsMet() with
        {
            LastDrainAt = T0 - TimeSpan.FromMinutes(4),
        }));
    }

    [Fact]
    public void Two_consecutive_drains_require_the_cooldown_between_them()
    {
        var policy = new CtIdleDrainPolicy(Quiet);
        CtIdleDrainObservation first = AllGuardsMet();

        Assert.True(policy.ShouldDrain(first));

        CtIdleDrainObservation justAfter = first with
        {
            Now = first.Now + TimeSpan.FromSeconds(30),
            LastDrainAt = first.Now,
        };
        Assert.False(policy.ShouldDrain(justAfter));

        CtIdleDrainObservation afterCooldown = first with
        {
            Now = first.Now + CtIdleDrainPolicy.Cooldown,
            LastDrainAt = first.Now,
        };
        Assert.True(policy.ShouldDrain(afterCooldown));
    }

    [Fact]
    public async Task An_idle_drain_executes_only_the_stale_cases_as_explicit_ids()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        store.PutTestCase(EngineTestSupport.Case("test:other", workspace.ProjectPath, "tests/OtherTests.cs"));
        CommitOutcome(store, "test:app", 3, "passed");
        CommitOutcome(store, "test:other", 2, "passed");
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:other", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);

        ContinuousTestDaemonEnqueueResult result = queue.EnqueueIdleDrain(DrainChange(workspace));

        Assert.Equal(ContinuousTestSelectionOutcome.WorkspaceScope, result.Selection.Outcome);
        Assert.Equal(["test:other"], result.Selection.SelectedTestCaseIds);

        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(T0, TestContext.Current.CancellationToken);

        Assert.Single(drained);
        ContinuousTestProviderRunRequest run = Assert.Single(provider.RunRequests);
        Assert.Equal(["test:other"], run.TestCaseIds);
        Assert.False(run.WholeSuite);
    }

    [Fact]
    public async Task An_all_stale_idle_drain_still_travels_as_an_explicit_id_list()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        store.PutTestCase(EngineTestSupport.Case("test:other", workspace.ProjectPath, "tests/OtherTests.cs"));
        CommitOutcome(store, "test:app", 2, "passed");
        CommitOutcome(store, "test:other", 2, "passed");
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);

        ContinuousTestDaemonEnqueueResult result = queue.EnqueueIdleDrain(DrainChange(workspace));

        Assert.Equal(["test:app", "test:other"], result.Selection.SelectedTestCaseIds.Order().ToArray());

        IReadOnlyList<ContinuousTestDaemonDrainResult> drained =
            await queue.DrainReadyAsync(T0, TestContext.Current.CancellationToken);

        ContinuousTestDaemonDrainResult drainResult = Assert.Single(drained);
        ContinuousTestProviderRunRequest run = Assert.Single(provider.RunRequests);
        Assert.Equal(["test:app", "test:other"], run.TestCaseIds.Order().ToArray());
        Assert.False(run.WholeSuite);
        Assert.False(drainResult.SelectionFacts.Eligible);
    }

    [Fact]
    public async Task An_idle_drain_never_reruns_an_unstamped_red()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        CommitOutcome(store, "test:app", 3, "failed");
        var provider = new FakeContinuousTestProvider();
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);

        ContinuousTestDaemonEnqueueResult result = queue.EnqueueIdleDrain(DrainChange(workspace));

        Assert.Empty(result.Selection.SelectedTestCaseIds);
        Assert.Empty(await queue.DrainReadyAsync(T0, TestContext.Current.CancellationToken));
        Assert.Empty(provider.RunRequests);
    }

    [Fact]
    public async Task An_idle_drain_executes_a_stamped_red_as_owed_backlog()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        CommitOutcome(store, "test:app", 3, "failed");
        store.MarkContinuousTestsStale(
            EngineTestSupport.WorkspaceId,
            ["test:app"],
            new CtFreshnessKey(EngineTestSupport.Identity, 3));
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);

        ContinuousTestDaemonEnqueueResult result = queue.EnqueueIdleDrain(DrainChange(workspace));

        Assert.Equal(["test:app"], result.Selection.SelectedTestCaseIds);

        Assert.Single(await queue.DrainReadyAsync(T0, TestContext.Current.CancellationToken));
        ContinuousTestProviderRunRequest run = Assert.Single(provider.RunRequests);
        Assert.Equal(["test:app"], run.TestCaseIds);
    }

    [Fact]
    public void The_queue_reports_pending_work_before_it_is_ready()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider();
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);

        Assert.False(queue.HasPendingWork());

        queue.Enqueue(EngineTestSupport.Change(
            workspace, revision: "3", from: 2, to: 3, debounce: TimeSpan.FromSeconds(2), observedAt: T0));

        Assert.True(queue.HasPendingWork());
        Assert.False(queue.HasReadyWork(T0));
    }

    [Fact]
    public async Task A_settled_idle_daemon_drains_the_stale_backlog_through_the_loop_once()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        CommitOutcome(store, "test:app", 2, "passed");
        store.MarkContinuousTestsStale(
            EngineTestSupport.WorkspaceId,
            ["test:app"],
            new CtFreshnessKey(EngineTestSupport.Identity, 3));
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "3") };
        ContinuousTestDaemonQueue queue = Queue(store, provider, revision: 3);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(3));
        var poller = new ContinuousTestRevisionPoller(source, new ScriptedImpactSource());
        var clock = new FakeClock(T0);
        var delay = new ManualDelay();
        var host = new ContinuousTestDaemonHost(
            workspace.WorkspaceRoot,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                Store = store,
                Queue = queue,
                Poller = poller,
                Projects = [new ContinuousTestProject("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath, Framework: "xunit")],
                Budget = CtExecutionBudget.Disabled(),
                AcquireLease = false,
                Clock = clock.Now,
                Delay = delay.DelayAsync,
                IdleDrainQuietPeriod = Quiet,
            });

        using var cancellation = new CancellationTokenSource();
        Task run = host.RunAsync(cancellation.Token);

        await delay.WaitForDelayCountAsync(1, TestContext.Current.CancellationToken);
        delay.CompleteNext();
        await delay.WaitForDelayCountAsync(2, TestContext.Current.CancellationToken);
        Assert.Empty(provider.RunRequests);

        clock.Advance(CtIdleDrainPolicy.Cooldown + TimeSpan.FromSeconds(1));
        delay.CompleteNext();
        await WaitUntil(() => provider.RunRequests.Count == 1, TestContext.Current.CancellationToken);

        ContinuousTestProviderRunRequest request = provider.RunRequests[0];
        Assert.Equal(["test:app"], request.TestCaseIds);
        Assert.False(request.WholeSuite);

        clock.Advance(CtIdleDrainPolicy.Cooldown + TimeSpan.FromSeconds(1));
        for (int i = 3; i <= 6; i++)
        {
            await delay.WaitForDelayCountAsync(i, TestContext.Current.CancellationToken);
            delay.CompleteNext();
        }

        await delay.WaitForDelayCountAsync(7, TestContext.Current.CancellationToken);
        Assert.Single(provider.RunRequests);

        cancellation.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private static CtIdleDrainObservation AllGuardsMet() =>
        new(
            Now: T0,
            StaleCount: 1504,
            QueueHasPendingWork: false,
            RunExecuting: false,
            PollSettled: true,
            AutoRunsPaused: false,
            LastActivityAt: T0 - Quiet,
            LastDrainAt: null);

    private ContinuousTestDaemonChange DrainChange(ContinuousTestWorkspace workspace) =>
        EngineTestSupport.Change(
            workspace,
            revision: "3",
            workspaceScope: true,
            completeness: ContinuousTestDeltaCompleteness.Unavailable,
            observedAt: T0);

    private static ContinuousTestDaemonQueue Queue(
        ContinuousTestStore store,
        IContinuousTestProvider provider,
        long revision) =>
        new(
            store,
            EngineTestSupport.Selector(store, revision: revision),
            new ContinuousTestCoordinator(provider, store));

    private static ContinuousTestRevisionObservation Observation(long revision) =>
        new(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey(EngineTestSupport.Identity, revision),
            IndexFresh: true,
            Status: "fresh",
            ObservedAt: DateTimeOffset.UtcNow);

    private static ProviderRunResult Passed(string testCaseId, string revision) =>
        new(
            "run:1",
            "passed",
            CaseResults:
            [
                new ProviderCaseResult("r1", testCaseId, "passed", revision, EngineTestSupport.Identity),
            ]);

    private static void CommitOutcome(
        ContinuousTestStore store,
        string testCaseId,
        long revision,
        string status)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string runId = "seed-run:" + testCaseId + ":" + revisionText;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: EngineTestSupport.WorkspaceId,
                Status: "running",
                SelectedRevision: revisionText,
                IndexIdentity: EngineTestSupport.Identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: EngineTestSupport.WorkspaceId,
            TestRunId: runId,
            SelectedRevision: revisionText,
            CurrentRevision: revisionText,
            IndexIdentity: EngineTestSupport.Identity,
            Revision: revision,
            Status: status,
            Results:
            [
                new ContinuousTestResult(
                    Id: runId + ":result",
                    WorkspaceId: EngineTestSupport.WorkspaceId,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: status,
                    ResultRevision: revisionText,
                    IndexIdentity: EngineTestSupport.Identity,
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

    private sealed class FakeClock(DateTimeOffset start)
    {
        private long _ticks = start.UtcTicks;

        public DateTimeOffset Now() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
