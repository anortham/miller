using Microsoft.Data.Sqlite;
using Miller.Testing;
using Miller.Tests.Testing.Daemon.Engine;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

public sealed class ContinuousTestDaemonStaleStopTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-stale-stop-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_stop_requested_before_this_daemon_started_does_not_kill_it()
    {
        CtCommandChannel.WriteRequest(
            _root,
            CtDaemonCommandKind.Stop,
            reason: "stop",
            freshness: null,
            time: new FixedTimeProvider(DateTimeOffset.UtcNow.AddSeconds(-30)));

        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store));
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                Queue = queue,
                PollInterval = TimeSpan.FromMilliseconds(5),
            },
            cts.Token);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted, "the daemon exited on a stop request that predates its start");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_stop_requested_while_this_daemon_runs_still_stops_it()
    {
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(new FakeContinuousTestProvider(), store));
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                Queue = queue,
                PollInterval = TimeSpan.FromMilliseconds(5),
            },
            cts.Token);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        CtCommandChannel.WriteRequest(_root, CtDaemonCommandKind.Stop, reason: "stop", freshness: null);

        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
