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

public sealed record CtDaemonStatusRecord(
    CtDaemonLifecycleState State,
    string Reason,
    CtDaemonLeaseIdentity? Identity,
    DateTimeOffset UpdatedAtUtc);

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
