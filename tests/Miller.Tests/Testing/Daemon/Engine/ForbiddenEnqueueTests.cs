using Microsoft.Data.Sqlite;
using Miller.Server.Tools;
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

    /// <summary>
    /// The kill switch binds every VERB, not only the daemon. <c>tests disable</c> opened the store before
    /// it checked the switch, and opening the store CREATES <c>ct.db</c> — so a disable request under
    /// MILLER_CT=off created and wrote the one file the switch promises Miller never touches. A seeded file
    /// proves the stronger property: not one byte changes.
    /// </summary>
    [Fact]
    public void Disable_under_the_kill_switch_writes_nothing_and_creates_nothing()
    {
        // A workspace with NO ct.db at all: the verb must not bring one into existence.
        TestsMutationResult refused = TestsCore.Disable(new TestsCoreRequest(_root)
        {
            KillSwitch = "off",
        });

        Assert.Equal(3, refused.ExitCode);
        Assert.Equal("continuous testing is disabled (MILLER_CT=off)", refused.Error);
        Assert.False(File.Exists(CtSchema.DbPathFor(_root)));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));

        // And a workspace that ALREADY has a ct.db: the file must be byte-for-byte unchanged.
        Directory.CreateDirectory(Path.Combine(_root, ".miller"));
        string dbPath = CtSchema.DbPathFor(_root);
        using (var seed = new ContinuousTestStore(dbPath))
        {
            seed.PutContinuousTestProject(new ContinuousTestProject(
                Id: "project:seed",
                WorkspaceId: "ws:1",
                ProjectPath: Path.Combine(_root, "Some.csproj"),
                Framework: "xunit",
                Command: null,
                Enabled: true));
        }

        SqliteConnection.ClearAllPools();
        byte[] before = File.ReadAllBytes(dbPath);

        TestsMutationResult refusedAgain = TestsCore.Disable(new TestsCoreRequest(_root)
        {
            KillSwitch = "off",
        });

        SqliteConnection.ClearAllPools();
        Assert.Equal(3, refusedAgain.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(dbPath));
    }

    /// <summary>
    /// The zero-work guarantee survives a WIRED sink. Production hands the disabled branch the same
    /// live <see cref="CtDaemonLog"/> sink the running daemon gets, so the guarantee cannot rest on
    /// the caller remembering to leave it null - it has to rest on the branch returning first.
    /// </summary>
    [Fact]
    public async Task A_disabled_daemon_writes_no_log_line_and_creates_no_logs_directory()
    {
        var diagnostics = new List<string>();
        ContinuousTestDaemonSnapshot snapshot = await ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                KillSwitch = "off",
                Enabled = true,
                Diagnostic = message =>
                {
                    diagnostics.Add(message);
                    CtDaemonLog.Write(_root, message);
                },
            },
            TestContext.Current.CancellationToken);

        Assert.False(snapshot.Enabled);
        Assert.Equal("disabled", snapshot.Reason);
        Assert.Empty(diagnostics);
        Assert.False(Directory.Exists(CtDaemonLog.LogsDirectory(_root)));
        Assert.False(Directory.Exists(Path.Combine(_root, ".miller")));
    }

    /// <summary>
    /// The database column keeps the first line of the message only. The log line must not inherit that
    /// truncation, so it carries the exception type, the WHOLE message, and a flattened stack.
    /// </summary>
    [Fact]
    public async Task A_discovery_failure_reports_its_type_full_message_and_stack_to_the_lifecycle_log()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        var lifecycle = new List<string>();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new ThrowingDiscoveryProvider(), store),
            lifecycleLog: lifecycle.Add);

        queue.EnqueueExplicit(EngineTestSupport.Change(
            workspace,
            workspaceScope: true,
            completeness: ContinuousTestDeltaCompleteness.Unavailable,
            observedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        string line = Assert.Single(
            lifecycle,
            entry => entry.StartsWith("ct discovery failed", StringComparison.Ordinal));

        Assert.Contains(EngineTestSupport.WorkspaceId, line, StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, line, StringComparison.Ordinal);
        Assert.Contains(ThrowingDiscoveryProvider.FirstMessageLine, line, StringComparison.Ordinal);

        // The anti-truncation proof: FailureSummary keeps only the first line, this line keeps both.
        Assert.Contains(ThrowingDiscoveryProvider.SecondMessageLine, line, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowingDiscoveryProvider), line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    /// <summary>
    /// The poll-error catch discarded its exception. A mismatched workspace id is the one poll failure the
    /// poller itself does not absorb, so it reaches that catch the way a real one does.
    /// </summary>
    [Fact]
    public async Task A_poll_error_reports_its_type_and_stack_through_the_diagnostic_sink()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store));
        var source = new ScriptedRevisionSource();
        source.Observations.Enqueue(new ContinuousTestRevisionObservation(
            "ws:a-different-workspace",
            new CtFreshnessKey("gen-1", 2),
            IndexFresh: true,
            Status: "fresh",
            ObservedAt: DateTimeOffset.UtcNow));
        var diagnostics = new List<string>();
        var host = new ContinuousTestDaemonHost(
            workspace.WorkspaceRoot,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                Store = store,
                Queue = queue,
                Poller = new ContinuousTestRevisionPoller(source),
                Projects = [Project(workspace)],
                Budget = CtExecutionBudget.Disabled(),
                AcquireLease = false,
                Clock = () => DateTimeOffset.UtcNow,
                Delay = (_, token) => Task.Delay(Timeout.Infinite, token),
                Diagnostic = diagnostics.Add,
            });

        using var cancellation = new CancellationTokenSource();
        Task run = host.RunAsync(cancellation.Token);
        await WaitUntil(() => diagnostics.Count > 0, TestContext.Current.CancellationToken);
        cancellation.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        string line = Assert.Single(diagnostics);
        Assert.StartsWith("ct poll error", line, StringComparison.Ordinal);
        Assert.Contains(EngineTestSupport.WorkspaceId, line, StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, line, StringComparison.Ordinal);
        Assert.Contains("ws:a-different-workspace", line, StringComparison.Ordinal);
        Assert.Contains(nameof(ContinuousTestRevisionPoller), line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
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

    /// <summary>
    /// Fails discovery with a TWO-LINE message. The second line is what proves the log line carries the
    /// full message: the <c>ct.db</c> column keeps the first line only.
    /// </summary>
    private sealed class ThrowingDiscoveryProvider : IContinuousTestProvider
    {
        internal const string FirstMessageLine = "project discovery blew up";
        internal const string SecondMessageLine = "second line names the missing sdk";

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(FirstMessageLine + Environment.NewLine + SecondMessageLine);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("discovery fails first, so no run may start");
    }
}
