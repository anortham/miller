using Microsoft.Data.Sqlite;
using Miller.Testing;
using Miller.Tests.Testing.Daemon.Engine;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// The daemon blocks inside <c>DrainReadyAsync</c> for a whole run, so <c>daemon.status.json</c> used to
/// freeze at the reason "executing" until the run ended. Only the heartbeat moved. A reader could not name the
/// project the daemon was on, could not tell a slow suite from a wedged one, and <c>tests run --wait</c> had
/// nothing to wait on except a verdict that is true the instant work is accepted.
///
/// <para>These tests use a REAL lease and read the real file, because the defect was in what reached that
/// file, not in what the loop computed.</para>
/// </summary>
public sealed class ContinuousTestDaemonActivityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("miller-ct-activity-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_status_file_names_the_project_the_daemon_is_running()
    {
        using Harness harness = StartWithBlockingRun();
        try
        {
            await harness.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            CtDaemonStatusRecord status = await harness.WaitForStatusAsync(
                record => record.Run is not null,
                "no run reached the status file");

            Assert.Equal(CtDaemonActivity.Executing, status.Activity);
            CtDaemonRunProgress run = Assert.IsType<CtDaemonRunProgress>(status.Run);
            Assert.Equal(harness.Workspace.ProjectPath, run.ProjectPath);
            Assert.Equal(1, run.SelectedCaseCount);
            Assert.False(string.IsNullOrWhiteSpace(run.RunId));
        }
        finally
        {
            await harness.StopAsync();
        }
    }

    /// <summary>
    /// The frozen-file defect itself. The main loop cannot republish while it is blocked in the drain, so the
    /// pulse task has to do it. Without that republish this assertion never fires.
    /// </summary>
    [Fact]
    public async Task The_status_file_keeps_moving_while_a_run_blocks_the_main_loop()
    {
        using Harness harness = StartWithBlockingRun();
        try
        {
            await harness.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            CtDaemonStatusRecord first = await harness.WaitForStatusAsync(
                record => record.Activity == CtDaemonActivity.Executing,
                "the daemon never published an executing status");

            CtDaemonStatusRecord later = await harness.WaitForStatusAsync(
                record => record.Activity == CtDaemonActivity.Executing
                    && record.UpdatedAtUtc > first.UpdatedAtUtc,
                "the status file froze while the run blocked the loop");

            // The republish must carry the state the MAIN loop chose, not one the pulse invented.
            Assert.Equal(CtDaemonLifecycleState.Running, later.State);
            Assert.Equal("executing", later.Reason);
        }
        finally
        {
            await harness.StopAsync();
        }
    }

    /// <summary>
    /// An out-of-process reader — <c>tests status</c>, the dashboard — goes through <c>ReadStatus</c>, which
    /// hardcoded <c>Executing: false</c> no matter what the file said.
    /// </summary>
    [Fact]
    public async Task An_out_of_process_reader_sees_the_daemon_as_executing()
    {
        using Harness harness = StartWithBlockingRun();
        try
        {
            await harness.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            // Waits for the RUN, not merely for "executing": the main loop publishes the executing status
            // before the queue starts the child, so a wait on the activity alone races the run details.
            await harness.WaitForStatusAsync(
                record => record.Activity == CtDaemonActivity.Executing && record.Run is not null,
                "the daemon never published a running project");

            ContinuousTestDaemonSnapshot snapshot = ContinuousTestDaemonHost.ReadStatus(_root);

            Assert.True(snapshot.Executing);
            Assert.Equal(CtDaemonActivity.Executing, snapshot.Activity);
            Assert.Equal(harness.Workspace.ProjectPath, snapshot.Run?.ProjectPath);
        }
        finally
        {
            await harness.StopAsync();
        }
    }

    [Fact]
    public async Task A_stopped_daemon_publishes_no_activity_and_no_run()
    {
        using Harness harness = StartWithBlockingRun();
        await harness.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await harness.WaitForStatusAsync(
            record => record.Activity == CtDaemonActivity.Executing,
            "the daemon never published an executing status");

        await harness.StopAsync();

        CtDaemonStatusRecord? status = CtDaemonLease.TryReadStatus(_root);
        Assert.NotNull(status);
        Assert.Equal(CtDaemonLifecycleState.Stopped, status.State);
        Assert.Equal(CtDaemonActivity.Idle, status.Activity);
        Assert.Null(status.Run);
    }

    [Fact]
    public async Task A_blocking_run_publishes_provider_selection_and_advancing_chunks()
    {
        using ProgressBlockingProvider provider = new();
        using Harness harness = StartWithBlockingRun(provider, caseCount: 2, providerSource: "ct-provider:fixture");
        try
        {
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            CtDaemonStatusRecord first = await harness.WaitForStatusAsync(
                record => record.Run is { ProviderSource: "ct-provider:fixture", CurrentPart: 1 },
                "the daemon never published provider and first chunk facts");
            CtDaemonRunProgress firstRun = Assert.IsType<CtDaemonRunProgress>(first.Run);

            CtDaemonStatusRecord later = await harness.WaitForStatusAsync(
                record => record.Run is { CurrentPart: 2 },
                "the daemon never published the next chunk");
            CtDaemonRunProgress laterRun = Assert.IsType<CtDaemonRunProgress>(later.Run);

            Assert.Equal(firstRun.RunId, laterRun.RunId);
            Assert.Equal(firstRun.RunStartedAtUtc, laterRun.RunStartedAtUtc);
            Assert.Equal(firstRun.Selection, laterRun.Selection);
            Assert.Equal(2, laterRun.RequestedUniqueUnitCount);
            Assert.Equal(2, laterRun.ChunkCount);
            Assert.Equal(2, laterRun.CurrentPartUnitCount);
            Assert.Equal(["test:app:part2"], laterRun.NameSamples);
        }
        finally
        {
            await harness.StopAsync();
        }
    }

    private Harness StartWithBlockingRun(
        IContinuousTestProvider? providerOverride = null,
        int caseCount = 1,
        string providerSource = "ct-provider:dotnet")
    {
        // ShouldConstructEngine gates the out-of-process read path on the workspace opt-in marker.
        Directory.CreateDirectory(Path.Combine(_root, CtSchema.MillerDirectoryName));
        File.WriteAllText(Path.Combine(_root, CtSchema.MillerDirectoryName, "ct.enabled"), string.Empty);

        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        for (var index = 0; index < caseCount; index++)
            store.PutTestCase(EngineTestSupport.Case(index == 0 ? "test:app" : $"test:app:{index}", workspace.ProjectPath));

        IContinuousTestProvider provider = providerOverride ?? new FakeContinuousTestProvider { BlockUntilCanceled = true };
        var activity = new CtRunActivityCell(TimeSpan.FromMinutes(10));
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FixedContinuousTestProviderResolver(provider, providerSource), store),
            runActivity: activity);
        queue.Enqueue(EngineTestSupport.Change(workspace));

        var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                WorkspaceId = EngineTestSupport.WorkspaceId,
                Store = store,
                Queue = queue,
                Budget = CtExecutionBudget.Disabled(),
                RunActivity = activity,
                PollInterval = TimeSpan.FromMilliseconds(5),

                // The pulse republish rides the heartbeat interval, so this is what makes the frozen-file
                // assertion finish in a test rather than in fifteen seconds.
                HeartbeatInterval = TimeSpan.FromMilliseconds(5),
            },
            cts.Token);

        return new Harness(_root, workspace, provider, store, cts, run);
    }

    private sealed class Harness(
        string root,
        ContinuousTestWorkspace workspace,
        IContinuousTestProvider provider,
        ContinuousTestStore store,
        CancellationTokenSource cancellation,
        Task<ContinuousTestDaemonSnapshot> run) : IDisposable
    {
        public ContinuousTestWorkspace Workspace => workspace;

        public IContinuousTestProvider Provider => provider;

        public Task ProviderStarted => provider switch
        {
            FakeContinuousTestProvider fake => fake.Started.Task,
            ProgressBlockingProvider progress => progress.Started.Task,
            _ => throw new InvalidOperationException("activity provider has no start signal")
        };

        public async Task StopAsync()
        {
            if (run.IsCompleted)
                return;
            await cancellation.CancelAsync();
            await run;
        }

        public async Task<CtDaemonStatusRecord> WaitForStatusAsync(
            Func<CtDaemonStatusRecord, bool> predicate,
            string failure)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (CtDaemonLease.TryReadStatus(root) is { } record && predicate(record))
                    return record;
                if (run.IsCompleted)
                    break;
                await Task.Delay(5);
            }

            Assert.Fail($"{failure}; last status: {CtDaemonLease.TryReadStatus(root)}");
            throw new InvalidOperationException("unreachable");
        }

        public void Dispose()
        {
            cancellation.Dispose();
            store.Dispose();
        }
    }

    private sealed class ProgressBlockingProvider : IContinuousTestProvider, IDisposable
    {
        public TaskCompletionSource<ContinuousTestProviderRunRequest> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        public async Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(request);
            request.Progress?.Invoke(new ContinuousTestProviderChunkProgress(
                RequestedUniqueUnitCount: request.TestCaseIds.Count,
                ChunkCount: 2,
                CurrentPart: 1,
                CurrentPartUnitCount: request.TestCaseIds.Count,
                NameSamples: ["test:app:part1"],
                NameDigest: "part1",
                NamesTruncated: false));
            await Task.Delay(50, cancellationToken);
            request.Progress?.Invoke(new ContinuousTestProviderChunkProgress(
                RequestedUniqueUnitCount: request.TestCaseIds.Count,
                ChunkCount: 2,
                CurrentPart: 2,
                CurrentPartUnitCount: request.TestCaseIds.Count,
                NameSamples: ["test:app:part2"],
                NameDigest: "part2",
                NamesTruncated: false));
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ProviderRunResult(request.RunId ?? "run:1", "passed");
        }

        public void Dispose()
        {
        }
    }
}
