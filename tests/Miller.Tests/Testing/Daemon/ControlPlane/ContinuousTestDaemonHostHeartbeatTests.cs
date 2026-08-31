using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Miller.Testing;
using Miller.Tests.Testing.Daemon.Engine;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

public sealed class ContinuousTestDaemonHostHeartbeatTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-heartbeat-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The pulse task republishes <c>daemon.status.json</c> on its own interval. That republish is the only
    /// thing that keeps the record's timestamp moving while a drain blocks the main loop, and it is now the
    /// only periodic control-plane write: the separate <c>daemon.heartbeat.json</c> it used to write 5,760
    /// times a day was read by no production code and is gone.
    /// </summary>
    [Fact]
    public async Task Run_republishes_the_status_record_while_the_loop_is_alive()
    {
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store));
        using var cts = new CancellationTokenSource();
        var options = new ContinuousTestDaemonHostOptions
        {
            Enabled = true,
            Queue = queue,
            PollInterval = TimeSpan.FromMilliseconds(5),
            HeartbeatInterval = TimeSpan.FromMilliseconds(5),
        };

        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(_root, options, cts.Token);
        try
        {
            CtDaemonStatusRecord first = await WaitForStatusAsync(after: null);
            CtDaemonStatusRecord second = await WaitForStatusAsync(after: first.UpdatedAtUtc);
            Assert.True(second.UpdatedAtUtc > first.UpdatedAtUtc);
            Assert.False(File.Exists(Path.Combine(CtDaemonProtocol.RootDirectory(_root), "daemon.heartbeat.json")));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    /// <summary>
    /// The daemon republishes its status every poll interval while a waiting client reads the same
    /// file far faster, so the two collide constantly and Windows raises a sharing failure. An
    /// unguarded write turned that collision into a dead daemon that still held its lease.
    /// </summary>
    [Fact]
    public async Task Run_survives_a_status_write_that_throws_a_sharing_violation()
    {
        var writer = new RecordingStatusWriter(new IOException("the status file is open for reading"));
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            StatusWriterOptions(writer),
            cts.Token);

        await WaitForAsync(() => writer.Count >= 3 || run.IsCompleted);

        Assert.False(run.IsCompleted, "a failed status write ended the daemon loop");
        Assert.True(writer.Count >= 3, $"the loop wrote status only {writer.Count} times before it stalled");

        await cts.CancelAsync();
        ContinuousTestDaemonSnapshot snapshot = await run;
        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.Equal("stopped", snapshot.Reason);
    }

    /// <summary>
    /// Pins the START status write specifically.
    ///
    /// The earlier version of this test only asserted that the tuple (Running, "status-only") was
    /// somewhere in the recording, which the loop's own idle branch also emits, so reverting the
    /// start write to the unguarded form left it green. The park-after-one-pass delay plus a queue
    /// that already holds ready work makes the whole recorded sequence exact: the first write can
    /// only come from the start, the second only from the budget-held branch, and the third only
    /// from the shutdown tail.
    /// </summary>
    [Fact]
    public async Task The_start_write_the_paused_write_and_the_shutdown_write_all_go_through_the_guarded_path()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(provider, store));
        queue.Enqueue(EngineTestSupport.Change(workspace));

        // A second holder of the user-global capacity-1 lease is what puts the loop on its paused
        // branch, which is the branch no earlier test reached.
        var budget = CtExecutionBudget.ForMillerHome(Path.Combine(_root, "budget-home"));
        using CtExecutionBudgetLease? held = budget.TryAcquire(
            new CtExecutionBudgetRequest(_root, "run"),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        Assert.NotNull(held);

        var writer = new RecordingStatusWriter(new IOException("the status file is open for reading"));
        var delay = new ParkingDelay();
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                AcquireLease = false,
                Store = store,
                Queue = queue,
                Budget = budget,
                StatusWriter = writer.Write,
                Delay = delay.DelayAsync,
            },
            cts.Token);

        await WaitForAsync(() => delay.Count >= 1 || run.IsCompleted);
        Assert.False(run.IsCompleted, "a failed status write ended the daemon loop");

        await cts.CancelAsync();
        ContinuousTestDaemonSnapshot snapshot = await run;

        StatusWrite[] expected =
        [
            new(CtDaemonLifecycleState.Running, "status-only"),
            new(CtDaemonLifecycleState.Paused, "execution budget held"),
            new(CtDaemonLifecycleState.Stopped, "stopped"),
        ];
        Assert.Equal(expected, writer.Written);
        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.Empty(provider.RunRequests);
    }

    /// <summary>
    /// Covers the remaining guarded write: the one the loop makes on every execution start, which
    /// in production fires against <c>daemon.status.json</c> while a <c>tests run --wait</c> client
    /// polls the same file. No earlier test gave the host a queue with ready work, so this branch
    /// was never executed at all.
    /// </summary>
    [Fact]
    public async Task The_execution_start_write_goes_through_the_guarded_path()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider { RunResult = Passed("test:app", "2") };
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(provider, store));
        queue.Enqueue(EngineTestSupport.Change(workspace));

        var writer = new RecordingStatusWriter(new UnauthorizedAccessException("the status file is locked"));
        var delay = new ParkingDelay();
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                AcquireLease = false,
                Store = store,
                Queue = queue,
                Budget = CtExecutionBudget.Disabled(),
                StatusWriter = writer.Write,
                Delay = delay.DelayAsync,
            },
            cts.Token);

        await WaitForAsync(() => delay.Count >= 1 || run.IsCompleted);
        Assert.False(run.IsCompleted, "a failed status write ended the daemon loop");

        await cts.CancelAsync();
        await run;

        StatusWrite[] expected =
        [
            new(CtDaemonLifecycleState.Running, "status-only"),
            new(CtDaemonLifecycleState.Running, "executing"),
            new(CtDaemonLifecycleState.Stopped, "stopped"),
        ];
        Assert.Equal(expected, writer.Written);
        Assert.Single(provider.RunRequests);
    }

    /// <summary>
    /// A requested stop is the daemon's normal exit, so it must leave through the shutdown tail.
    /// It used to leave by throwing, which skipped the final status write, skipped the final
    /// snapshot, left the heartbeat task unobserved, and reached the CLI as "ct-daemon failed: The
    /// operation was canceled" with exit code 1. Nothing cancels the token here: if the loop still
    /// throws, the await below reports the exception, and if the heartbeat is awaited without being
    /// cancelled first the 15-second pulse interval blocks the exit past the wait.
    /// </summary>
    [Fact]
    public async Task A_live_stop_request_runs_the_shutdown_tail_and_exits_cleanly()
    {
        var writer = new RecordingStatusWriter();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                AcquireLease = true,
                Enqueuer = new RecordingEnqueuer(),
                StatusWriter = writer.Write,
                PollInterval = TimeSpan.FromMilliseconds(5),
                HeartbeatInterval = TimeSpan.FromSeconds(30),
            },
            TestContext.Current.CancellationToken);

        await WaitForAsync(() => writer.Count >= 1 || run.IsCompleted);
        CtDaemonCommandRequest request = CtCommandChannel.WriteRequest(
            _root,
            CtDaemonCommandKind.Stop,
            reason: "stop",
            freshness: null);

        ContinuousTestDaemonSnapshot snapshot = await run.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.Equal("stopped", snapshot.Reason);

        StatusWrite[] written = writer.Written;
        Assert.Equal(new StatusWrite(CtDaemonLifecycleState.Stopped, "stop"), written[^2]);
        Assert.Equal(new StatusWrite(CtDaemonLifecycleState.Stopped, "stopped"), written[^1]);
        Assert.Equal("stopping", CtCommandChannel.TryReadAck(_root, request.CommandId)?.Reason);
    }

    /// <summary>
    /// The ack write reaches the same atomic-replace path the status write does, and it used to be
    /// unguarded, so a sharing violation or a denied ACL on the command directory killed the daemon
    /// on the very command that asked it to stop. A directory standing where the ack file belongs
    /// makes that write fail on every platform without a second process.
    /// </summary>
    [Fact]
    public async Task A_stop_whose_ack_cannot_be_written_still_stops_the_daemon_cleanly()
    {
        const string commandId = "stop-ack-blocked";
        Directory.CreateDirectory(CtDaemonProtocol.CommandAckPath(_root, commandId));

        var writer = new RecordingStatusWriter();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                AcquireLease = false,
                Enqueuer = new RecordingEnqueuer(),
                StatusWriter = writer.Write,
                PollInterval = TimeSpan.FromMilliseconds(5),
            },
            TestContext.Current.CancellationToken);

        await WaitForAsync(() => writer.Count >= 1 || run.IsCompleted);
        CtCommandChannel.WriteRequest(
            _root,
            CtDaemonCommandKind.Stop,
            reason: "stop",
            freshness: null,
            commandId: commandId);

        ContinuousTestDaemonSnapshot snapshot = await run.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.Equal("stopped", snapshot.Reason);
        Assert.Null(CtCommandChannel.TryReadAck(_root, commandId));
    }

    /// <summary>
    /// The run ack has the same exposure, plus one of its own: the ack FILE is what stops the loop
    /// re-reading a request it already handled, so guarding the write alone would make an unwritable
    /// ack directory re-enqueue and re-execute the whole suite on every poll. The daemon must
    /// survive the failed write AND enqueue the request exactly once.
    /// </summary>
    [Fact]
    public async Task A_run_whose_ack_cannot_be_written_survives_and_enqueues_the_request_once()
    {
        const string commandId = "run-ack-blocked";
        Directory.CreateDirectory(CtDaemonProtocol.CommandAckPath(_root, commandId));

        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var enqueueLog = new ConcurrentQueue<string>();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store),
            lifecycleLog: enqueueLog.Enqueue);

        // Holding the budget parks the queue on the paused branch, so the request is observed
        // through the enqueue log rather than through a provider run.
        var budget = CtExecutionBudget.ForMillerHome(Path.Combine(_root, "budget-home"));
        using CtExecutionBudgetLease? held = budget.TryAcquire(
            new CtExecutionBudgetRequest(_root, "run"),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        Assert.NotNull(held);

        var writer = new RecordingStatusWriter();
        var delay = new CountingDelay();
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                AcquireLease = false,
                Store = store,
                Queue = queue,
                Projects =
                [
                    new ContinuousTestProject(
                        "proj:1",
                        EngineTestSupport.WorkspaceId,
                        workspace.ProjectPath,
                        Framework: "xunit"),
                ],
                Budget = budget,
                StatusWriter = writer.Write,
                PollInterval = TimeSpan.FromMilliseconds(5),
                Delay = delay.DelayAsync,
            },
            cts.Token);

        CtCommandChannel.WriteRequest(
            _root,
            CtDaemonCommandKind.Run,
            reason: "run",
            freshness: new CtFreshnessKey(EngineTestSupport.Identity, 2),
            commandId: commandId);

        await WaitForAsync(() => EnqueueCount(enqueueLog) > 0 || run.IsCompleted);
        int passesWhenEnqueued = delay.Count;
        await WaitForAsync(() => delay.Count >= passesWhenEnqueued + 8 || run.IsCompleted);

        await cts.CancelAsync();
        await run;

        Assert.True(
            delay.Count >= passesWhenEnqueued + 8,
            $"the loop completed only {delay.Count - passesWhenEnqueued} passes over the unacked request");
        Assert.Equal(1, EnqueueCount(enqueueLog));
        Assert.Null(CtCommandChannel.TryReadAck(_root, commandId));
        Assert.Contains(new StatusWrite(CtDaemonLifecycleState.Paused, "execution budget held"), writer.Written);
    }

    /// <summary>
    /// No lease and no queue: the loop needs neither to reach its status writes, and leaving both
    /// out keeps the test pure — no lock file, no SQLite, no second process.
    /// </summary>
    [Fact]
    public async Task Loop_AcksRunCommandWhilePollReadInFlight()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BlockingRevisionSource(gate)
        {
            Observation = new ContinuousTestRevisionObservation(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey(EngineTestSupport.Identity, 2),
                true,
                "fresh",
                DateTimeOffset.UtcNow),
        };
        var writer = new RecordingStatusWriter();
        var delay = new CountingDelay();
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                AcquireLease = false,
                Poller = new ContinuousTestRevisionPoller(source),
                Enqueuer = new RecordingEnqueuer(),
                StatusWriter = writer.Write,
                PollInterval = TimeSpan.FromMilliseconds(5),
                Delay = delay.DelayAsync,
            },
            cts.Token);
        try
        {
            await WaitForAsync(() => source.RefreshCount >= 1 || run.IsCompleted);
            Assert.False(run.IsCompleted, "the loop exited while the poll read was still pending");

            CtDaemonCommandRequest request = CtCommandChannel.WriteRequest(
                _root,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: new CtFreshnessKey(EngineTestSupport.Identity, 2));

            await WaitForAckAsync(request.CommandId);
            Assert.False(gate.Task.IsCompleted, "the poll read finished before the run command was acked");
            Assert.NotNull(CtCommandChannel.TryReadAck(_root, request.CommandId));

            gate.TrySetResult();
            await WaitForAsync(() =>
                writer.Written.Any(write => write.Reason == "idle") || run.IsCompleted);
            Assert.Contains(writer.Written, write => write.Reason == "idle");
        }
        finally
        {
            gate.TrySetResult();
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Loop_Stop_unblocks_an_in_flight_poll_read()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BlockingRevisionSource(gate)
        {
            Observation = new ContinuousTestRevisionObservation(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey(EngineTestSupport.Identity, 2),
                true,
                "fresh",
                DateTimeOffset.UtcNow),
        };
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                AcquireLease = false,
                Poller = new ContinuousTestRevisionPoller(source),
                Enqueuer = new RecordingEnqueuer(),
                PollInterval = TimeSpan.FromMilliseconds(5),
            },
            CancellationToken.None);

        await WaitForAsync(() => source.RefreshCount >= 1 || run.IsCompleted);
        Assert.False(run.IsCompleted, "the loop exited before the poll read blocked");

        CtCommandChannel.WriteRequest(
            _root,
            CtDaemonCommandKind.Stop,
            reason: "stop",
            freshness: null);

        ContinuousTestDaemonSnapshot snapshot = await run.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
        Assert.False(gate.Task.IsCompleted);
    }

    [Fact]
    public async Task Loop_Shutdown_survives_a_faulted_in_flight_poll_read()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FaultingRevisionSource(gate, new IOException("poll boom"));
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                AcquireLease = false,
                Poller = new ContinuousTestRevisionPoller(source),
                Enqueuer = new RecordingEnqueuer(),
                PollInterval = TimeSpan.FromMilliseconds(5),
            },
            CancellationToken.None);

        await WaitForAsync(() => source.RefreshCount >= 1 || run.IsCompleted);
        Assert.False(run.IsCompleted, "the loop exited before the poll read blocked");

        CtDaemonCommandRequest stop = CtCommandChannel.WriteRequest(
            _root,
            CtDaemonCommandKind.Stop,
            reason: "stop",
            freshness: null);
        await WaitForAsync(() => CtCommandChannel.TryReadAck(_root, stop.CommandId) is not null || run.IsCompleted);

        gate.TrySetResult();
        ContinuousTestDaemonSnapshot snapshot = await run.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(CtDaemonLifecycleState.Stopped, snapshot.State);
    }

    [Fact]
    public async Task Loop_DefersExplicitRunUntilInFlightPollAppliesLiveKey()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var enqueueLog = new ConcurrentQueue<string>();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store),
            lifecycleLog: enqueueLog.Enqueue);
        var budget = CtExecutionBudget.ForMillerHome(Path.Combine(_root, "budget-home"));
        using CtExecutionBudgetLease? held = budget.TryAcquire(
            new CtExecutionBudgetRequest(_root, "run"),
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);
        Assert.NotNull(held);

        var source = new RepeatableBlockingSource
        {
            Observation = new ContinuousTestRevisionObservation(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey(EngineTestSupport.Identity, 2),
                true,
                "fresh",
                DateTimeOffset.UtcNow),
        };
        var delay = new CountingDelay();
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                AcquireLease = false,
                Store = store,
                Queue = queue,
                Poller = new ContinuousTestRevisionPoller(source),
                Projects =
                [
                    new ContinuousTestProject(
                        "proj:1",
                        EngineTestSupport.WorkspaceId,
                        workspace.ProjectPath,
                        Framework: "xunit"),
                ],
                Budget = budget,
                PollInterval = TimeSpan.FromMilliseconds(5),
                Delay = delay.DelayAsync,
            },
            cts.Token);
        try
        {
            await WaitForAsync(() => source.RefreshCount >= 1 || run.IsCompleted);
            source.Release();
            await WaitForAsync(() => delay.Count >= 2 || run.IsCompleted);

            const string rebuiltIdentity = "gen-rebuild";
            source.Observation = new ContinuousTestRevisionObservation(
                EngineTestSupport.WorkspaceId,
                new CtFreshnessKey(rebuiltIdentity, 1),
                true,
                "fresh",
                DateTimeOffset.UtcNow,
                Rebuild: true);

            await WaitForAsync(() => source.RefreshCount >= 2 || run.IsCompleted);
            Assert.Equal(0, ExplicitEnqueueCount(enqueueLog));

            CtDaemonCommandRequest request = CtCommandChannel.WriteRequest(
                _root,
                CtDaemonCommandKind.Run,
                reason: "run",
                freshness: new CtFreshnessKey(EngineTestSupport.Identity, 2));
            await WaitForAckAsync(request.CommandId);
            Assert.Equal(0, ExplicitEnqueueCount(enqueueLog));

            source.Release();
            await WaitForAsync(() => ExplicitEnqueueCount(enqueueLog) >= 1 || run.IsCompleted);
            string line = Assert.Single(enqueueLog, row => row.StartsWith("ct enqueue workspace=", StringComparison.Ordinal));
            Assert.Contains("identity=" + rebuiltIdentity, line, StringComparison.Ordinal);
            Assert.Contains("revision=1", line, StringComparison.Ordinal);
        }
        finally
        {
            source.Release();
            await cts.CancelAsync();
            await run;
        }
    }


    private async Task WaitForAckAsync(string commandId)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (CtCommandChannel.TryReadAck(_root, commandId) is not null)
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("the run command was not acked while the poll read was in flight");
    }

    private static ContinuousTestDaemonHostOptions StatusWriterOptions(RecordingStatusWriter writer) =>
        new()
        {
            Enabled = true,
            AcquireLease = false,
            Enqueuer = new RecordingEnqueuer(),
            StatusWriter = writer.Write,
            PollInterval = TimeSpan.FromMilliseconds(5),
        };

    private static int EnqueueCount(IEnumerable<string> lifecycleLog) =>
        lifecycleLog.Count(line => line.StartsWith("ct enqueue", StringComparison.Ordinal));

    private static int ExplicitEnqueueCount(IEnumerable<string> lifecycleLog) =>
        lifecycleLog.Count(line => line.StartsWith("ct enqueue workspace=", StringComparison.Ordinal));

    private static ProviderRunResult Passed(string testCaseId, string revision) =>
        new(
            "run:1",
            "passed",
            CaseResults:
            [
                new ProviderCaseResult("r1", testCaseId, "passed", revision, EngineTestSupport.Identity),
            ]);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            if (predicate())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private async Task<CtDaemonStatusRecord> WaitForStatusAsync(DateTimeOffset? after)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            CtDaemonStatusRecord? record = CtDaemonLease.TryReadStatus(_root);
            if (record is not null && (after is null || record.UpdatedAtUtc > after))
                return record;
            await Task.Delay(10);
        }

        throw new TimeoutException("the daemon status record did not refresh");
    }

    private sealed record StatusWrite(CtDaemonLifecycleState State, string Reason);

    /// <summary>
    /// Stands in for the daemon's own status write: it records the call, then throws the failure a
    /// concurrent reader causes on Windows. A null failure records without throwing, for the tests
    /// that read the recorded sequence rather than the guard.
    /// </summary>
    private sealed class RecordingStatusWriter(Exception? failure = null)
    {
        private readonly ConcurrentQueue<StatusWrite> _written = new();

        public int Count => _written.Count;

        public StatusWrite[] Written => [.. _written];

        public void Write(CtDaemonLifecycleState state, string reason)
        {
            _written.Enqueue(new StatusWrite(state, reason));
            if (failure is not null)
                throw failure;
        }
    }

    /// <summary>
    /// Counts loop passes and then parks the loop for good, so a test can assert the EXACT sequence
    /// of status writes one pass produces instead of whatever a racing timer happened to add.
    /// </summary>
    private sealed class ParkingDelay
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task DelayAsync(TimeSpan _, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>Counts loop passes while letting the loop keep running.</summary>
    private sealed class CountingDelay
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.Delay(duration, cancellationToken);
        }
    }

    private sealed class BlockingRevisionSource : IContinuousTestRevisionSource
    {
        private readonly TaskCompletionSource _gate;
        private int _refreshCount;

        public BlockingRevisionSource(TaskCompletionSource gate)
        {
            _gate = gate;
        }

        public ContinuousTestRevisionObservation? Observation { get; init; }

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public async Task<ContinuousTestRevisionObservation?> RefreshAsync(
            string workspaceId,
            string workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _refreshCount);
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Observation;
        }
    }

    private sealed class FaultingRevisionSource : IContinuousTestRevisionSource
    {
        private readonly TaskCompletionSource _gate;
        private readonly Exception _fault;
        private int _refreshCount;

        public FaultingRevisionSource(TaskCompletionSource gate, Exception fault)
        {
            _gate = gate;
            _fault = fault;
        }

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public async Task<ContinuousTestRevisionObservation?> RefreshAsync(
            string workspaceId,
            string workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _refreshCount);
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw _fault;
        }
    }

    private sealed class RepeatableBlockingSource : IContinuousTestRevisionSource
    {
        private volatile TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _refreshCount;

        public ContinuousTestRevisionObservation? Observation { get; set; }

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public void Release()
        {
            TaskCompletionSource previous = _gate;
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }

        public void BlockAgain() =>
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ContinuousTestRevisionObservation?> RefreshAsync(
            string workspaceId,
            string workspaceRoot,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _refreshCount);
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Observation;
        }
    }
}
