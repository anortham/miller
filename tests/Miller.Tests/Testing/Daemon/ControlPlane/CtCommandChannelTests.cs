using System.Diagnostics;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

public sealed class CtCommandChannelTests : IDisposable
{
    /// <summary>
    /// The cap on a wait for something that MUST happen — a request file appearing, a killed process
    /// exiting. Every one of these is condition-based and ends the moment the condition holds, so on a
    /// healthy machine the cap is never reached and a generous value costs nothing.
    ///
    /// <para>It was two seconds, which is a budget rather than a cap: a full-suite CT run starved the
    /// background write past it and reported
    /// <c>Run_WhenDaemonIsLive_WritesRequestAndReturnsAck</c> RED while the same test passed in 611ms on
    /// a quiet machine (observed 2026-08-21). CT's own job is to run the whole suite, so the load that
    /// broke this test is the load it runs under. A false red there costs more than a slow failure here.
    /// Waits that assert a timeout EXPIRES stay short — they are measuring the giving up.</para>
    /// </summary>
    private static readonly TimeSpan PositiveWait = TimeSpan.FromSeconds(30);

    private readonly string _root;

    public CtCommandChannelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-ct-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void WriteRequest_ThenAck_RoundTripsRunAndStop()
    {
        var freshness = new CtFreshnessKey("store:abc", 4);
        CtDaemonCommandRequest request = CtCommandChannel.WriteRequest(
            _root,
            CtDaemonCommandKind.Run,
            "explicit",
            freshness,
            commandId: "run-1");

        Assert.Equal("run-1", request.CommandId);
        Assert.True(File.Exists(CtDaemonProtocol.CommandRequestPath(_root, "run-1")));
        Assert.Equal(request, CtCommandChannel.TryReadRequest(_root, "run-1"));
        Assert.Null(CtCommandChannel.TryReadAck(_root, "run-1"));

        var ack = new CtDaemonCommandAck(
            "run-1",
            CtDaemonCommandState.Acknowledged,
            DateTimeOffset.UtcNow,
            Reason: null);
        CtCommandChannel.WriteAck(_root, ack);

        Assert.Equal(CtDaemonCommandState.Acknowledged, CtCommandChannel.TryReadAck(_root, "run-1")!.State);
        Assert.Null(CtCommandChannel.WaitForAck(_root, "missing", TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void PathHelpers_DoNotCreateDirectories()
    {
        string other = Path.Combine(Path.GetTempPath(), "miller-ct-cmd-read-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(CtCommandChannel.TryReadRequest(other, "abc"));
            Assert.Null(CtCommandChannel.TryReadAck(other, "abc"));
            Assert.False(Directory.Exists(other));
            Assert.False(Directory.Exists(CtDaemonProtocol.CommandDirectory(other)));
        }
        finally
        {
            if (Directory.Exists(other))
                Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void InvalidCommandId_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => CtDaemonProtocol.CommandRequestPath(_root, "../x"));
        Assert.Throws<ArgumentException>(() => CtCommandChannel.WriteRequest(
            _root, CtDaemonCommandKind.Stop, reason: null, freshness: null, commandId: "bad/id"));
    }

    [Fact]
    public void UnackedRequest_IsNotTreatedAsDone()
    {
        CtCommandChannel.WriteRequest(_root, CtDaemonCommandKind.Stop, "halt", freshness: null, commandId: "stop-1");
        Assert.False(CtCommandChannel.IsAcknowledged(_root, "stop-1"));
        Assert.Null(CtCommandChannel.TryReadAck(_root, "stop-1"));
    }

    [Fact]
    public void Stop_TerminatesOnlyTheLeasedProcessTree()
    {
        using Process leased = StartStub();
        using Process other = StartStub();
        CtDaemonLeaseIdentity identity = IdentityOf(leased);
        using (CtDaemonLease.TryAcquire(_root, "1.20.0-stop", identity))
        {
            CtDaemonStopResult result = CtCommandChannel.Stop(
                _root,
                gracefulWait: TimeSpan.FromMilliseconds(40),
                exitWait: PositiveWait);

            Assert.Equal(CtDaemonStopStatus.Stopped, result.Status);
            Assert.True(leased.HasExited);
            Assert.False(other.HasExited);
            Assert.Equal(CtDaemonLifecycleState.Stopped, CtDaemonLease.TryReadStatus(_root)?.State);
        }

        if (!other.HasExited)
        {
            other.Kill(entireProcessTree: true);
            other.WaitForExit(2000);
        }
    }

    [Fact]
    public void Stop_WithNoDaemon_DoesNotCreateControlPlane()
    {
        CtDaemonStopResult result = CtCommandChannel.Stop(_root, TimeSpan.FromMilliseconds(10));
        Assert.Equal(CtDaemonStopStatus.AlreadyStopped, result.Status);
        Assert.False(Directory.Exists(CtDaemonProtocol.RootDirectory(_root)));
    }

    [Fact]
    public async Task Run_WhenDaemonIsLive_WritesRequestAndReturnsAck()
    {
        using CtDaemonLease? lease = CtDaemonLease.TryAcquire(_root, "1.20.0-run");
        Assert.NotNull(lease);

        var freshness = new CtFreshnessKey("idx", 9);
        Task<CtRunResult> pending = Task.Run(() => CtCommandChannel.Run(
            _root,
            reason: "wake",
            freshness: freshness,
            ackTimeout: PositiveWait));

        DateTimeOffset deadline = DateTimeOffset.UtcNow + PositiveWait;
        string? commandId = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            commandId = FindRequestId(CtDaemonCommandKind.Run);
            if (commandId is not null)
                break;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.False(string.IsNullOrWhiteSpace(commandId));
        CtCommandChannel.WriteAck(_root, new CtDaemonCommandAck(
            commandId!,
            CtDaemonCommandState.Acknowledged,
            DateTimeOffset.UtcNow,
            "ok"));

        CtRunResult result = await pending;
        Assert.Equal(CtRunExecution.Daemon, result.Execution);
        Assert.NotNull(result.Ack);
        Assert.Equal(CtDaemonCommandState.Acknowledged, result.Ack.State);
    }

    private string? FindRequestId(CtDaemonCommandKind kind)
    {
        string dir = CtDaemonProtocol.CommandDirectory(_root);
        if (!Directory.Exists(dir))
            return null;
        foreach (string path in Directory.EnumerateFiles(dir, "*.request.json"))
        {
            string id = Path.GetFileName(path)[..^".request.json".Length];
            CtDaemonCommandRequest? request = CtCommandChannel.TryReadRequest(_root, id);
            if (request?.Kind == kind)
                return id;
        }

        return null;
    }

    private static Process StartStub()
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        }
        else
        {
            info.FileName = "sh";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("sleep 30");
        }

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException("stub process did not start");
        return process;
    }

    private static CtDaemonLeaseIdentity IdentityOf(Process process)
    {
        process.Refresh();
        return new CtDaemonLeaseIdentity(
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime()));
    }
}
