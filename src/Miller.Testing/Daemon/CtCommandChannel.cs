using System.Diagnostics;

namespace Miller.Testing;

public enum CtDaemonStopStatus
{
    Stopped,
    AlreadyStopped,
    Failed,
}

public sealed record CtRunResult(
    CtRunExecution Execution,
    CtDaemonCommandAck? Ack,
    string? Reason);

public sealed record CtDaemonStopResult(CtDaemonStopStatus Status, string? Reason);

/// <summary>
/// File command channel for <c>run</c> and <c>stop</c>. Writer creates <c>*.request.json</c>
/// then waits for <c>*.ack.json</c>. A <c>run</c> with no live daemon is a foreground one-shot
/// and does not spawn. <c>stop</c> signals the leased daemon, waits, then kills that process tree.
/// </summary>
public static class CtCommandChannel
{
    public static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultGracefulStopWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultExitWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public static CtDaemonCommandRequest WriteRequest(
        string workspaceRoot,
        CtDaemonCommandKind kind,
        string? reason,
        CtFreshnessKey? freshness,
        string? commandId = null,
        TimeProvider? time = null)
    {
        string id = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId;
        var request = new CtDaemonCommandRequest(
            id,
            kind,
            (time ?? TimeProvider.System).GetUtcNow(),
            reason,
            freshness);
        CtDaemonJson.WriteAtomic(
            CtDaemonProtocol.CommandRequestPath(workspaceRoot, id),
            request,
            CtDaemonJsonContext.Default.CtDaemonCommandRequest);
        return request;
    }

    public static void WriteAck(string workspaceRoot, CtDaemonCommandAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        CtDaemonJson.WriteAtomic(
            CtDaemonProtocol.CommandAckPath(workspaceRoot, ack.CommandId),
            ack,
            CtDaemonJsonContext.Default.CtDaemonCommandAck);
    }

    public static CtDaemonCommandRequest? TryReadRequest(string workspaceRoot, string commandId) =>
        CtDaemonJson.TryRead(
            CtDaemonProtocol.CommandRequestPath(workspaceRoot, commandId),
            CtDaemonJsonContext.Default.CtDaemonCommandRequest);

    public static CtDaemonCommandAck? TryReadAck(string workspaceRoot, string commandId) =>
        CtDaemonJson.TryRead(
            CtDaemonProtocol.CommandAckPath(workspaceRoot, commandId),
            CtDaemonJsonContext.Default.CtDaemonCommandAck);

    public static bool IsAcknowledged(string workspaceRoot, string commandId) =>
        TryReadAck(workspaceRoot, commandId) is { State: CtDaemonCommandState.Acknowledged };

    public static CtDaemonCommandAck? WaitForAck(
        string workspaceRoot, string commandId, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            CtDaemonCommandAck? ack = TryReadAck(workspaceRoot, commandId);
            if (ack is not null)
                return ack;
            if (stopwatch.Elapsed >= timeout)
                return null;
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            Thread.Sleep(remaining < PollInterval ? remaining : PollInterval);
        }
    }

    public static CtRunResult Run(
        string workspaceRoot,
        string? reason = null,
        CtFreshnessKey? freshness = null,
        TimeSpan? ackTimeout = null,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        if (CtDaemonLease.TryReadLive(workspaceRoot) is null)
        {
            _ = startProcess;
            return new CtRunResult(CtRunExecution.ForegroundOneShot, null, "no daemon");
        }

        CtDaemonCommandRequest request = WriteRequest(
            workspaceRoot, CtDaemonCommandKind.Run, reason, freshness);
        CtDaemonCommandAck? ack = WaitForAck(workspaceRoot, request.CommandId, ackTimeout ?? DefaultAckTimeout);
        return new CtRunResult(CtRunExecution.Daemon, ack, ack is null ? "unacked" : null);
    }

    public static CtDaemonStopResult Stop(
        string workspaceRoot,
        TimeSpan? gracefulWait = null,
        TimeSpan? exitWait = null)
    {
        CtDaemonLeaseRecord? live = CtDaemonLease.TryReadLive(workspaceRoot);
        if (live is null)
            return new CtDaemonStopResult(CtDaemonStopStatus.AlreadyStopped, "no daemon");

        TimeSpan graceful = gracefulWait ?? DefaultGracefulStopWait;
        TimeSpan exit = exitWait ?? DefaultExitWait;
        CtDaemonCommandRequest request = WriteRequest(
            workspaceRoot, CtDaemonCommandKind.Stop, "stop", freshness: null);
        CtDaemonLease.WriteStatus(
            workspaceRoot,
            new CtDaemonStatusRecord(
                CtDaemonLifecycleState.Paused,
                "stop requested",
                live.Identity,
                DateTimeOffset.UtcNow));

        WaitForAck(workspaceRoot, request.CommandId, graceful);
        WaitUntilDead(live.Identity, graceful);

        if (CtDaemonLease.IsIdentityLive(live.Identity))
            KillLeasedProcess(live.Identity, exit);

        bool gone = !CtDaemonLease.IsIdentityLive(live.Identity);
        CtDaemonLease.WriteStatus(
            workspaceRoot,
            new CtDaemonStatusRecord(
                CtDaemonLifecycleState.Stopped,
                gone ? "stopped" : "stop failed",
                live.Identity,
                DateTimeOffset.UtcNow));
        return gone
            ? new CtDaemonStopResult(CtDaemonStopStatus.Stopped, "stopped")
            : new CtDaemonStopResult(CtDaemonStopStatus.Failed, "process still live");
    }

    private static void WaitUntilDead(CtDaemonLeaseIdentity identity, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (CtDaemonLease.IsIdentityLive(identity) && stopwatch.Elapsed < timeout)
        {
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            Thread.Sleep(remaining < PollInterval ? remaining : PollInterval);
        }
    }

    private static void KillLeasedProcess(CtDaemonLeaseIdentity identity, TimeSpan exitWait)
    {
        if (!CtDaemonLease.IsIdentityLive(identity))
            return;

        try
        {
            using var process = Process.GetProcessById(identity.Pid);
            DateTimeOffset started = new(process.StartTime.ToUniversalTime());
            if ((started - identity.ProcessStartTimeUtc).Duration() > TimeSpan.FromSeconds(2))
                return;
            if (process.HasExited)
                return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit((int)Math.Clamp(exitWait.TotalMilliseconds, 0, int.MaxValue));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
        }
    }
}
