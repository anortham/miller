using Microsoft.Data.Sqlite;
using Miller.Testing;
using Miller.Tests.Testing.Daemon.Engine;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// The command-file stem IS the command id, and every protocol path REFUSES an id outside
/// <c>^[A-Za-z0-9._-]+$</c> by throwing. A file whose name breaks that pattern must therefore be
/// moved aside before its stem reaches any protocol call — the throw used to happen on the ack
/// probe, outside the per-command guard, so one such file killed the daemon at every start.
/// </summary>
public sealed class CtMalformedCommandFileTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-bad-command-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_malformed_request_filename_does_not_kill_the_loop_and_is_not_reprocessed()
    {
        string commands = CtDaemonProtocol.CommandDirectory(_root);
        Directory.CreateDirectory(commands);
        string bad = Path.Combine(commands, "bad name.request.json");
        await File.WriteAllTextAsync(bad, "{}", TestContext.Current.CancellationToken);

        var lines = new List<string>();
        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                Enqueuer = new RecordingEnqueuer(),
                PollInterval = TimeSpan.FromMilliseconds(5),
                Diagnostic = line => { lock (lines) lines.Add(line); },
            },
            cts.Token);

        try
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted, "the daemon died on a malformed command filename");
            Assert.False(File.Exists(bad), "the malformed request file was left where the drain rereads it");
            Assert.True(
                File.Exists(bad + ".rejected"),
                "the malformed request file was not moved aside");

            // The bad stem never reached a protocol path, so no acknowledgement was ever written for it.
            Assert.Empty(Directory.GetFiles(commands, "*.ack.json"));

            string[] reported;
            lock (lines)
                reported = [.. lines];
            Assert.Single(reported, line => line.Contains("bad name.request.json", StringComparison.Ordinal));
        }
        finally
        {
            await cts.CancelAsync();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task A_valid_command_beside_a_malformed_one_still_executes()
    {
        string commands = CtDaemonProtocol.CommandDirectory(_root);
        Directory.CreateDirectory(commands);
        await File.WriteAllTextAsync(
            Path.Combine(commands, "bad name.request.json"),
            "{}",
            TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        Task<ContinuousTestDaemonSnapshot> run = ContinuousTestDaemonHost.RunAsync(
            _root,
            new ContinuousTestDaemonHostOptions
            {
                Enabled = true,
                Enqueuer = new RecordingEnqueuer(),
                PollInterval = TimeSpan.FromMilliseconds(5),
            },
            cts.Token);

        try
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            CtDaemonCommandRequest stop = CtCommandChannel.WriteRequest(
                _root, CtDaemonCommandKind.Stop, reason: "stop", freshness: null);

            await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotNull(CtCommandChannel.TryReadAck(_root, stop.CommandId));
        }
        finally
        {
            await cts.CancelAsync();
            try { await run; } catch (OperationCanceledException) { }
        }
    }
}
