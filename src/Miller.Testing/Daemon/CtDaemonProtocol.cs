using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Miller.Indexing;

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

/// <summary>
/// The lease file names the holder; it is written once, when the lock is taken. It carried a
/// <c>heartbeat_utc</c> that was stamped at acquire and never renewed, so a field that read like a
/// liveness signal grew staler the longer the daemon stayed healthy. Liveness rides the OS lock and
/// the recorded identity, and how recently the daemon MOVED is now
/// <see cref="CtDaemonStatusRecord.UpdatedAtUtc"/>, which is republished every pulse.
/// </summary>
public sealed record CtDaemonLeaseRecord(
    CtDaemonLeaseIdentity Identity,
    DateTimeOffset AcquiredAtUtc,
    string WorkspaceRoot,
    string MillerVersion);

/// <summary>
/// <paramref name="WorkspaceRoot"/> is a trailing optional so an older request file still
/// deserializes (it reads as null) and every existing positional construction keeps compiling.
/// Null or the daemon's own root targets the daemon's primary workspace; any other value names an
/// ADOPTED worktree the family daemon serves, so <c>run</c> reaches that worktree's own queue and
/// <c>stop</c> detaches only that worktree's context.
/// </summary>
public sealed record CtDaemonCommandRequest(
    string CommandId,
    CtDaemonCommandKind Kind,
    DateTimeOffset RequestedAtUtc,
    string? Reason,
    CtFreshnessKey? Freshness,
    string? WorkspaceRoot = null);

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
/// <para><c>daemon.status.json</c> used to freeze at reason "executing" for a whole run; only its timestamp
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
/// <param name="SilenceSeconds">
/// How long the child has been silent, in whole seconds, measured by the DAEMON on its own monotonic clock —
/// the same measurement the kill is armed from. <see cref="Activity"/> alone says only that the silence has
/// passed the bound, never by how much, so a reader could not tell a kill that is due this instant from one
/// that is an hour late. Null on a record from a build that predates the field.
/// </param>
/// <param name="ChildStallSeconds">
/// The silence bound the daemon will kill at, in whole seconds, as THIS daemon resolved it. A reader that
/// re-resolved <c>MILLER_CT_STALL_TIMEOUT</c> from its own environment would judge the daemon against a number
/// the daemon never used. <c>0</c> means the guard is off and no kill is coming; null on a record from a build
/// that predates the field.
/// </param>
public sealed record CtDaemonRunProgress(
    string ProjectPath,
    string RunId,
    int SelectedCaseCount,
    DateTimeOffset RunStartedAtUtc,
    CtRunActivity Activity,
    int? SilenceSeconds = null,
    int? ChildStallSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProviderSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ContinuousTestDaemonSelectionFacts? Selection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? ElapsedSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RequestedUniqueUnitCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ChunkCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CurrentPart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? CurrentPartUnitCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? NameSamples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NameDigest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? NamesTruncated = null);

/// <summary>
/// The published daemon status. <see cref="Activity"/>, <see cref="Run"/> and
/// <see cref="LoopTickAtUtc"/> are trailing optionals so an older status file still deserializes (it
/// reads as <see cref="CtDaemonActivity.Idle"/> with no run and no tick) and every existing positional
/// construction keeps compiling.
/// </summary>
/// <param name="UpdatedAtUtc">
/// When this record was written. The pulse task republishes it every heartbeat interval, so it keeps
/// moving even while the main loop is blocked.
/// </param>
/// <param name="LoopTickAtUtc">
/// When the MAIN LOOP last published a status of its own, copied verbatim into every republish the pulse
/// makes. It exists because nothing else on disk proves the loop is alive: the pulse survives a wedged
/// loop by design, and the pid probe proves only the process. Two stamps from the same clock in the same
/// file make the lag measurable without the reader's own clock entering it, so machine load — which stalls
/// both writers together — cannot fake a stall. Null on a record written before this field existed, and on
/// the transition records a family daemon writes for an adopted worktree; absence means unknown, never a
/// stall.
/// </param>
/// <param name="LoopAgeSeconds">
/// How long the main loop had been standing still when this record was written, in seconds, measured by the
/// DAEMON on its own MONOTONIC clock — the same kind of measurement <see cref="CtDaemonRunProgress.SilenceSeconds"/>
/// already carries for the child.
///
/// <para>Why it exists beside the two wall-clock stamps: both of those come from the daemon's wall clock, so a
/// forward correction landing between the loop's tick and the pulse's write — an NTP step, a laptop waking —
/// fabricated a lag the loop never had, and a backward one hid a real stall. A monotonic clock cannot be
/// corrected. The reader cannot hold a monotonic stamp of the daemon's (the two are different processes, and
/// the tick counts are not comparable across them), so the daemon subtracts and publishes the AGE.</para>
///
/// <para>Null on a record from a build that predates the field, and whenever <see cref="LoopTickAtUtc"/> is
/// null — there is no tick to measure from. A reader falls back to the two stamps then.</para>
/// </param>
/// <param name="AutoRunsPaused">
/// Whether the daemon has paused AUTOMATIC runs, which the lifecycle state does not answer: the pause
/// lived only as free text in <see cref="Reason"/>, so <c>tests status</c> printed
/// <c>daemon: running (idle)</c> while auto-runs had been silently stopped for minutes. Trailing
/// optional: a record from an older build reads as not paused, never as an error.
/// </param>
/// <param name="PauseReason">
/// Why auto-runs are paused — the reason after the <c>auto-runs paused: </c> prefix of the published
/// wording, for example <c>impact unavailable (moving_cursor)</c>. Null whenever
/// <see cref="AutoRunsPaused"/> is false, and on a record from a build that predates the field.
/// </param>
public sealed record CtDaemonStatusRecord(
    CtDaemonLifecycleState State,
    string Reason,
    CtDaemonLeaseIdentity? Identity,
    DateTimeOffset UpdatedAtUtc,
    CtDaemonActivity Activity = CtDaemonActivity.Idle,
    CtDaemonRunProgress? Run = null,
    DateTimeOffset? LoopTickAtUtc = null,
    double? LoopAgeSeconds = null,
    bool AutoRunsPaused = false,
    string? PauseReason = null);

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

    public static string StatusPath(string workspaceRoot) =>
        Path.Combine(RootDirectory(workspaceRoot), StatusFileName);

    public static string CommandDirectory(string workspaceRoot) =>
        Path.Combine(RootDirectory(workspaceRoot), CommandDirectoryName);

    public static string CommandRequestPath(string workspaceRoot, string commandId) =>
        Path.Combine(CommandDirectory(workspaceRoot), $"{RequireCommandId(commandId)}.request.json");

    public static string CommandAckPath(string workspaceRoot, string commandId) =>
        Path.Combine(CommandDirectory(workspaceRoot), $"{RequireCommandId(commandId)}.ack.json");

    /// <summary>
    /// Whether <paramref name="commandId"/> is a legal command id — the SAME rule
    /// <see cref="RequireCommandId"/> enforces by throwing.
    ///
    /// <para>A reader that discovers ids by listing the command directory needs to ASK this question,
    /// because the file name is chosen by whoever wrote the file and the answer decides whether the id
    /// may touch a protocol path at all. Asking by catching the throw does not work: the throw escapes
    /// on the first path call, which is what killed the daemon on one badly named file.</para>
    /// </summary>
    public static bool IsCommandId(string? commandId) =>
        !string.IsNullOrWhiteSpace(commandId) && CommandIdPattern.IsMatch(commandId);

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

/// <summary>
/// Where <c>tests</c> verbs for a workspace find a live daemon. <see cref="EndpointRoot"/> is the
/// root whose control plane (<c>.miller/ct/</c>) carries the command files; <see cref="Adopting"/>
/// is true when that daemon lives on the repo's MAIN checkout and serves the asked-about worktree
/// by adoption rather than by holding a lease on it.
/// </summary>
public sealed record CtDaemonEndpoint(string EndpointRoot, CtDaemonLeaseRecord Lease, bool Adopting);

/// <summary>
/// Command routing for the family daemon. A linked worktree has no daemon of its own in the adopted
/// arrangement, so a command written into ITS command directory would sit unread forever; these
/// helpers resolve the live endpoint (own lease first, then the main checkout's) and write requests
/// that carry the target worktree's identity in the payload.
/// </summary>
public static class CtDaemonRouting
{
    /// <summary>
    /// The live daemon endpoint for <paramref name="workspaceRoot"/>, or null when no live daemon
    /// serves it. The root's OWN live lease always wins - a worktree running its own daemon is
    /// never routed away from it. Only then does a linked worktree fall through to the repo's main
    /// checkout. Resolution reads lease files and the two git pointer files; it creates nothing.
    /// </summary>
    public static CtDaemonEndpoint? ResolveLiveEndpoint(
        string workspaceRoot,
        Func<string, CtDaemonLeaseRecord?>? readLiveLease = null,
        Func<string, GitWorktreeLayout?>? resolveLayout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string root = Path.GetFullPath(workspaceRoot);
        Func<string, CtDaemonLeaseRecord?> probe =
            readLiveLease ?? (candidate => CtDaemonLease.TryReadLive(candidate));
        if (probe(root) is { } own)
            return new CtDaemonEndpoint(root, own, Adopting: false);

        GitWorktreeLayout? layout = (resolveLayout ?? GitWorktreeLayout.Resolve)(root);
        if (layout is { IsLinkedWorktree: true, MainCheckoutRoot: { } mainRoot })
        {
            string main = Path.GetFullPath(mainRoot);
            if (probe(main) is { } family)
                return new CtDaemonEndpoint(main, family, Adopting: true);
        }

        return null;
    }

    /// <summary>
    /// Writes one command request into <paramref name="endpointRoot"/>'s command directory with the
    /// TARGET workspace in the payload, so the family daemon routes it to the right context. The
    /// same shape serves the endpoint's own workspace too: a target equal to the endpoint resolves
    /// to the daemon's primary context.
    /// </summary>
    public static CtDaemonCommandRequest WriteRoutedRequest(
        string endpointRoot,
        CtDaemonCommandKind kind,
        string? reason,
        CtFreshnessKey? freshness,
        string targetWorkspaceRoot,
        string? commandId = null,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetWorkspaceRoot);
        string id = string.IsNullOrWhiteSpace(commandId)
            ? Guid.NewGuid().ToString("N")
            : commandId;
        var request = new CtDaemonCommandRequest(
            id,
            kind,
            (time ?? TimeProvider.System).GetUtcNow(),
            reason,
            freshness,
            Path.GetFullPath(targetWorkspaceRoot));
        CtDaemonJson.WriteAtomic(
            CtDaemonProtocol.CommandRequestPath(endpointRoot, id),
            request,
            CtDaemonJsonContext.Default.CtDaemonCommandRequest);
        return request;
    }

    /// <summary>
    /// Submits a <c>run</c> for <paramref name="targetWorkspaceRoot"/> to the daemon at
    /// <paramref name="endpointRoot"/> and waits for the acknowledgement. Mirrors
    /// <see cref="CtCommandChannel.Run"/>, plus the routed workspace payload.
    /// </summary>
    public static CtRunResult SubmitRun(
        string endpointRoot,
        string targetWorkspaceRoot,
        string? reason = null,
        CtFreshnessKey? freshness = null,
        TimeSpan? ackTimeout = null)
    {
        if (CtDaemonLease.TryReadLive(endpointRoot) is null)
            return new CtRunResult(CtRunExecution.ForegroundOneShot, null, "no daemon");

        CtDaemonCommandRequest request = WriteRoutedRequest(
            endpointRoot, CtDaemonCommandKind.Run, reason, freshness, targetWorkspaceRoot);
        CtDaemonCommandAck? ack = CtCommandChannel.WaitForAck(
            endpointRoot, request.CommandId, ackTimeout ?? CtCommandChannel.DefaultAckTimeout);
        return new CtRunResult(CtRunExecution.Daemon, ack, ack is null ? "unacked" : null);
    }

    /// <summary>
    /// Asks the family daemon at <paramref name="endpointRoot"/> to detach the adopted worktree
    /// <paramref name="worktreeRoot"/>. The daemon itself keeps running - this is the worktree
    /// shape of <c>tests stop</c>, and it must never kill the family daemon.
    /// </summary>
    public static CtDaemonCommandAck? RequestDetach(
        string endpointRoot,
        string worktreeRoot,
        TimeSpan? ackTimeout = null)
    {
        CtDaemonCommandRequest request = WriteRoutedRequest(
            endpointRoot, CtDaemonCommandKind.Stop, "detach", freshness: null, worktreeRoot);
        return CtCommandChannel.WaitForAck(
            endpointRoot, request.CommandId, ackTimeout ?? CtCommandChannel.DefaultAckTimeout);
    }
}
