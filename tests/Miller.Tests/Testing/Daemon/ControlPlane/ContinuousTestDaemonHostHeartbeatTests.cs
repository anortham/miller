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

    [Fact]
    public async Task Run_refreshes_the_heartbeat_file_while_the_loop_is_alive()
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
            CtDaemonHeartbeatRecord first = await WaitForHeartbeatAsync(after: null);
            CtDaemonHeartbeatRecord second = await WaitForHeartbeatAsync(after: first.HeartbeatUtc);
            Assert.True(second.HeartbeatUtc > first.HeartbeatUtc);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    private async Task<CtDaemonHeartbeatRecord> WaitForHeartbeatAsync(DateTimeOffset? after)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            CtDaemonHeartbeatRecord? record = CtDaemonLease.TryReadHeartbeat(_root);
            if (record is not null && (after is null || record.HeartbeatUtc > after))
                return record;
            await Task.Delay(10);
        }

        throw new TimeoutException("the daemon heartbeat file did not refresh");
    }
}
