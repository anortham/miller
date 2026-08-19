using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ForbiddenEnqueueTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-forbidden-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Unavailable_impact_enqueues_nothing()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store));

        ContinuousTestDaemonEnqueueResult result = queue.Enqueue(
            EngineTestSupport.Change(
                workspace,
                completeness: ContinuousTestDeltaCompleteness.Unavailable,
                workspaceScope: true));

        Assert.Empty(result.Selection.SelectedTestCaseIds);
        Assert.False(queue.HasReadyWork(DateTimeOffset.UtcNow));
        Assert.Empty(store.ListTestRuns(EngineTestSupport.WorkspaceId));
    }

    [Fact]
    public async Task Poller_unavailable_delta_does_not_call_enqueuer()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(new ContinuousTestRevisionObservation(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey("gen-1", 2),
            IndexFresh: true,
            Status: "fresh",
            ObservedAt: DateTimeOffset.UtcNow));
        var impact = new ScriptedImpactSource
        {
            Result = new ContinuousTestImpactResult(
                EngineTestSupport.WorkspaceId,
                [],
                [],
                [])
            {
                Outcome = ContinuousTestImpactOutcome.Unavailable,
                Reason = "no_capability",
            },
        };
        var enqueuer = new RecordingEnqueuer();
        var poller = new ContinuousTestRevisionPoller(source, impact);

        ContinuousTestRevisionPollResult first = await poller.PollAsync(
            PollRequest(workspace, enqueuer, enqueueArmed: false),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, first.EnqueuedProjects);
        Assert.Empty(enqueuer.Changes);

        source.Observations.Enqueue(new ContinuousTestRevisionObservation(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey("gen-1", 3),
            IndexFresh: true,
            Status: "fresh",
            ObservedAt: DateTimeOffset.UtcNow));
        ContinuousTestRevisionPollResult second = await poller.PollAsync(
            PollRequest(workspace, enqueuer, enqueueArmed: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, second.EnqueuedProjects);
        Assert.Equal("unavailable_delta", second.Reason);
        Assert.Empty(enqueuer.Changes);
        Assert.Equal("no_capability", second.DeltaReason);
    }

    [Fact]
    public async Task Poller_bridge_error_never_falls_back_to_workspace_scope()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(2));
        var impact = new ScriptedImpactSource { Throw = new InvalidOperationException("bridge down") };
        var enqueuer = new RecordingEnqueuer();
        var poller = new ContinuousTestRevisionPoller(source, impact);
        await poller.PollAsync(PollRequest(workspace, enqueuer, enqueueArmed: false), TestContext.Current.CancellationToken);

        source.Observations.Enqueue(Observation(3));
        ContinuousTestRevisionPollResult result = await poller.PollAsync(
            PollRequest(workspace, enqueuer, enqueueArmed: true),
            TestContext.Current.CancellationToken);

        Assert.Empty(enqueuer.Changes);
        Assert.Equal(0, result.EnqueuedProjects);
        Assert.Equal("unavailable_delta", result.Reason);
        Assert.Equal("bridge_error", result.DeltaReason);
        Assert.DoesNotContain(enqueuer.Changes, change => change.WorkspaceScope);
    }

    [Fact]
    public async Task Start_executes_nothing_until_a_new_change_or_explicit_run()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider
        {
            RunResult = Passed("test:app", "2"),
        };
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(provider, store));
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(Observation(2));
        var impact = new ScriptedImpactSource
        {
            Result = Changed(3),
        };
        var poller = new ContinuousTestRevisionPoller(source, impact);
        var host = new ContinuousTestDaemonHost(
            workspace.WorkspaceRoot,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                Store = store,
                Queue = queue,
                Poller = poller,
                Projects = [Project(workspace)],
                Budget = CtExecutionBudget.Disabled(),
                AcquireLease = false,
                Clock = () => DateTimeOffset.UtcNow,
                Delay = (_, token) => Task.Delay(Timeout.Infinite, token),
            });

        using var cancellation = new CancellationTokenSource();
        Task run = host.RunAsync(cancellation.Token);
        await WaitUntil(() => host.LastSnapshot is not null, TestContext.Current.CancellationToken);

        Assert.Empty(provider.RunRequests);
        Assert.Equal(CtDaemonLifecycleState.Running, host.LastSnapshot!.State);
        Assert.NotEqual("executing", host.LastSnapshot.Reason);
        Assert.False(queue.HasReadyWork(DateTimeOffset.UtcNow));

        cancellation.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Host_kill_switch_constructs_zero_ct_machinery()
    {
        var snapshots = new List<ContinuousTestDaemonSnapshot>();
        ContinuousTestDaemonSnapshot snapshot = await ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                KillSwitch = "off",
                Enabled = true,
                StatusSink = snapshots.Add,
            },
            TestContext.Current.CancellationToken);

        Assert.False(snapshot.Enabled);
        Assert.Equal("disabled", snapshot.Reason);
        Assert.Equal(ContinuousTestVerdict.Unknown, snapshot.Verdict);
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
        Assert.Equal(snapshot, Assert.Single(snapshots));
    }

    [Fact]
    public void Status_read_does_not_start_the_host()
    {
        ContinuousTestDaemonSnapshot snapshot = ContinuousTestDaemonHost.ReadStatus(_root);
        Assert.False(snapshot.Enabled);
        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
        Assert.Null(CtDaemonLease.TryRead(_root));
    }

    [Fact]
    public async Task Second_workspace_is_paused_only_while_the_first_executes()
    {
        var budget = CtExecutionBudget.ForMillerHome(
            Directory.CreateTempSubdirectory("miller-ct-budget-pause-").FullName);
        using CtExecutionBudgetLease? held = budget.TryAcquire(
            new CtExecutionBudgetRequest(_root, "run"),
            TimeSpan.Zero,
            CancellationToken.None);
        Assert.NotNull(held);

        var other = Directory.CreateTempSubdirectory("miller-ct-ws-b-").FullName;
        try
        {
            var workspace = EngineTestSupport.Workspace(other);
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(other));
            store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
            var provider = new FakeContinuousTestProvider
            {
                RunResult = Passed("test:app", "3"),
            };
            var queue = new ContinuousTestDaemonQueue(
                store,
                EngineTestSupport.Selector(store),
                new ContinuousTestCoordinator(provider, store));
            queue.Enqueue(EngineTestSupport.Change(workspace, revision: "3", from: 2, to: 3));
            var host = new ContinuousTestDaemonHost(
                other,
                new ContinuousTestDaemonHostOptions
                {
                    Enabled = true,
                    WorkspaceId = EngineTestSupport.WorkspaceId,
                    Store = store,
                    Queue = queue,
                    Budget = budget,
                    AcquireLease = false,
                    Clock = () => DateTimeOffset.UtcNow,
                    Delay = (_, token) => Task.Delay(Timeout.Infinite, token),
                });

            using var cancellation = new CancellationTokenSource();
            Task run = host.RunAsync(cancellation.Token);
            await WaitUntil(() => host.LastSnapshot?.State == CtDaemonLifecycleState.Paused, TestContext.Current.CancellationToken);

            Assert.Empty(provider.RunRequests);
            Assert.Equal("execution budget held", host.LastSnapshot!.Reason);

            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
        finally
        {
            try { Directory.Delete(other, recursive: true); } catch (IOException) { }
        }
    }

    private static ContinuousTestRevisionPollRequest PollRequest(
        ContinuousTestWorkspace workspace,
        IContinuousTestDaemonEnqueuer enqueuer,
        bool enqueueArmed) =>
        new(
            EngineTestSupport.WorkspaceId,
            workspace.WorkspaceRoot,
            [Project(workspace)],
            enqueuer,
            EnqueueArmed: enqueueArmed);

    private static ContinuousTestProject Project(ContinuousTestWorkspace workspace) =>
        new("proj:1", EngineTestSupport.WorkspaceId, workspace.ProjectPath, Framework: "xunit");

    private static ContinuousTestRevisionObservation Observation(long revision) =>
        new(
            EngineTestSupport.WorkspaceId,
            new CtFreshnessKey("gen-1", revision),
            IndexFresh: true,
            Status: "fresh",
            ObservedAt: DateTimeOffset.UtcNow);

    private static ContinuousTestImpactResult Changed(long to) =>
        new(
            EngineTestSupport.WorkspaceId,
            ["src/App.cs"],
            [],
            [])
        {
            Outcome = ContinuousTestImpactOutcome.Changed,
            FromRevision = to - 1,
            ToRevision = to,
        };

    private static ProviderRunResult Passed(string testCaseId, string revision) =>
        new(
            "run:1",
            "passed",
            CaseResults:
            [
                new ProviderCaseResult("r1", testCaseId, "passed", revision, EngineTestSupport.Identity),
            ]);

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
}
