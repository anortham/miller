using System.Text.RegularExpressions;

namespace Miller.Testing;

public enum CtDaemonCommandKind
{
    Run,
    Stop,
}

public enum CtDaemonCommandState
{
    Requested,
    Acknowledged,
    Rejected,
}

public enum CtDaemonLifecycleState
{
    Running,
    Paused,
    Stopped,
}

/// <summary>
/// Lease identity is PID plus process start time so a reused PID cannot inherit a dead daemon's lease.
/// </summary>
public sealed record CtDaemonLeaseIdentity(int Pid, DateTimeOffset ProcessStartTimeUtc);

public sealed record CtDaemonLeaseRecord(
    CtDaemonLeaseIdentity Identity,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset HeartbeatUtc,
    string WorkspaceRoot,
    string MillerVersion);

public sealed record CtDaemonHeartbeatRecord(
    CtDaemonLeaseIdentity Identity,
    DateTimeOffset HeartbeatUtc);

public sealed record CtDaemonCommandRequest(
    string CommandId,
    CtDaemonCommandKind Kind,
    DateTimeOffset RequestedAtUtc,
    string? Reason,
    CtFreshnessKey? Freshness);

public sealed record CtDaemonCommandAck(
    string CommandId,
    CtDaemonCommandState State,
    DateTimeOffset AcknowledgedAtUtc,
    string? Reason);

/// <summary>
/// What the daemon is DOING, as opposed to what lifecycle state it is in. A daemon can be
/// <see cref="CtDaemonLifecycleState.Running"/> with nothing to do, and it can be
/// <see cref="CtDaemonLifecycleState.Paused"/> while still holding accepted work.
///
/// <para>This exists because <c>tests run --wait</c> had no way to tell "the run has finished" from "the
/// run has not started yet". It waited on the VERDICT instead, and the verdict is not a completion signal:
/// accepting a run marks the selected cases stale, which makes the verdict <c>Partial</c> immediately, so
/// the wait returned at once with a mid-run answer.</para>
/// </summary>
public enum CtDaemonActivity
{
    /// <summary>No accepted work is outstanding. A wait may stop here.</summary>
    Idle,

    /// <summary>Work has been accepted but is not executing — usually the execution budget is held.</summary>
    Queued,

    /// <summary>A provider run is in flight.</summary>
    Executing,
}

/// <summary>
/// How lively the running child process is, DERIVED by the daemon from the child's last output so a reader
/// never has to subtract timestamps or open a second file.
///
/// <para><c>daemon.status.json</c> used to freeze at reason "executing" for a whole run; only the heartbeat
/// moved. A reader could not separate a slow suite from a wedged one without comparing clocks.</para>
/// </summary>
public enum CtRunActivity
{
    /// <summary>The child has produced no output yet.</summary>
    Starting,

    /// <summary>Output arrived recently — less than a quarter of the stall bound ago.</summary>
    Active,

    /// <summary>Silent for a noticeable share of the stall bound, but not yet over it.</summary>
    Quiet,

    /// <summary>Silent for the whole stall bound. The kill is due.</summary>
    Stalled,
}

/// <summary>
/// The run the daemon is executing right now. Absent when nothing is running.
/// </summary>
public sealed record CtDaemonRunProgress(
    string ProjectPath,
    string RunId,
    int SelectedCaseCount,
    DateTimeOffset RunStartedAtUtc,
    CtRunActivity Activity);

/// <summary>
/// The published daemon status. <see cref="Activity"/> and <see cref="Run"/> are trailing optionals so an
/// older status file still deserializes (it reads as <see cref="CtDaemonActivity.Idle"/> with no run) and
/// every existing positional construction keeps compiling.
/// </summary>
public sealed record CtDaemonStatusRecord(
    CtDaemonLifecycleState State,
    string Reason,
    CtDaemonLeaseIdentity? Identity,
    DateTimeOffset UpdatedAtUtc,
    CtDaemonActivity Activity = CtDaemonActivity.Idle,
    CtDaemonRunProgress? Run = null);

/// <summary>
/// File layout for the detached CT control plane under <c>&lt;workspace&gt;/.miller/ct/</c>.
/// Paths are computed only; callers must not create the directory as a side effect of a status read.
/// </summary>
public static class CtDaemonProtocol
{
    public const string MillerDirectoryName = ".miller";
    public const string DirectoryName = "ct";
    public const string CommandDirectoryName = "commands";
    public const string LockFileName = "daemon-v1.lock";
    public const string LeaseFileName = "daemon.lease.json";
    public const string HeartbeatFileName = "daemon.heartbeat.json";
    public const string StatusFileName = "daemon.status.json";

    private static readonly Regex CommandIdPattern = new(
        "^[A-Za-z0-9._-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string RootDirectory(string workspaceRoot) =>
        Path.Combine(RequireRoot(workspaceRoot), MillerDirectoryName, DirectoryName);

    public static string LockPath(string workspaceRoot) =>
        Path.Combine(RootDirectory(workspaceRoot), LockFileName);

    public static string LeasePath(string workspaceRoot) =>
        Path.Combine(RootDirectory(workspaceRoot), LeaseFileName);

    public static string HeartbeatPath(string workspaceRoot) =>
        Path.Combine(RootDirectory(workspaceRoot), HeartbeatFileName);

    public static string StatusPath(string workspaceRoot) =>
        Path.Combine(RootDirectory(workspaceRoot), StatusFileName);

    public static string CommandDirectory(string workspaceRoot) =>
        Path.Combine(RootDirectory(workspaceRoot), CommandDirectoryName);

    public static string CommandRequestPath(string workspaceRoot, string commandId) =>
        Path.Combine(CommandDirectory(workspaceRoot), $"{RequireCommandId(commandId)}.request.json");

    public static string CommandAckPath(string workspaceRoot, string commandId) =>
        Path.Combine(CommandDirectory(workspaceRoot), $"{RequireCommandId(commandId)}.ack.json");

    private static string RequireRoot(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("must not be empty", nameof(workspaceRoot));
        return workspaceRoot;
    }

    private static string RequireCommandId(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId) || !CommandIdPattern.IsMatch(commandId))
            throw new ArgumentException("must be a file-safe token", nameof(commandId));
        return commandId;
    }
}
