using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

public sealed class ContinuousTestDaemonRunnerTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-runner-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Runner_drains_ready_work_and_records_green()
    {
        var workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new FakeContinuousTestProvider
        {
            RunResult = new ProviderRunResult(
                "run:1",
                "passed",
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults:
                [
                    new ProviderCaseResult("r1", "test:app", "passed", "2", EngineTestSupport.Identity),
                ]),
        };
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));
        var clock = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
        var delay = new ManualDelay();
        var runner = new ContinuousTestDaemonRunner(queue, new ContinuousTestDaemonRunnerOptions
        {
            Clock = () => clock,
            Delay = delay.DelayAsync,
            PollInterval = TimeSpan.FromMilliseconds(25),
        });
        runner.Enqueue(EngineTestSupport.Change(workspace, observedAt: clock, debounce: TimeSpan.Zero));
        runner.Start();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await runner.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal("2", Assert.Single(provider.RunRequests).SelectedRevision);
        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(EngineTestSupport.WorkspaceId));
        Assert.Equal(ContinuousTestState.Green, status.State);
        Assert.Equal(EngineTestSupport.Identity, status.IndexIdentity);
        Assert.Equal(2, status.Revision);
    }

    [Fact]
    public async Task Double_start_is_rejected()
    {
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store));
        var delay = new ManualDelay();
        var runner = new ContinuousTestDaemonRunner(queue, new ContinuousTestDaemonRunnerOptions
        {
            Delay = delay.DelayAsync,
        });
        runner.Start();
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(runner.Start);
        await runner.StopAsync(TestContext.Current.CancellationToken);
        await runner.StopAsync(TestContext.Current.CancellationToken);
        Assert.Contains("already running", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(runner.IsRunning);
    }
}
