using System.Diagnostics;
using System.Globalization;
using Miller.Indexing;

namespace Miller.Testing;

/// <summary>
/// <paramref name="Activity"/>, <paramref name="Run"/> and <paramref name="LoopHealth"/> are trailing
/// optionals so every existing positional construction keeps compiling. <paramref name="Executing"/> stays
/// because callers read it; it answers "is a drain in flight", while <paramref name="Run"/> names the project
/// that drain is on.
/// </summary>
/// <param name="LoopHealth">
/// What the published record proves about the daemon's MAIN LOOP, or null when the snapshot was not read from
/// a record at all. See <see cref="CtDaemonLoopHealth"/>.
/// </param>
/// <param name="AutoRunsPaused">
/// Whether the published record says AUTOMATIC runs are paused, with <paramref name="PauseReason"/> naming
/// why. A record from an older build reads as not paused. Distinct from
/// <see cref="CtDaemonLifecycleState.Paused"/>, which is the lifecycle state.
/// </param>
public sealed record ContinuousTestDaemonSnapshot(
    CtDaemonLifecycleState State,
    string Reason,
    ContinuousTestVerdict Verdict,
    CtFreshnessKey? Selected,
    int StaleCount,
    int SelectedCount,
    bool Enabled,
    bool Executing,
    CtDaemonActivity Activity = CtDaemonActivity.Idle,
    CtDaemonRunProgress? Run = null,
    CtLoopHealthVerdict? LoopHealth = null,
    bool AutoRunsPaused = false,
    string? PauseReason = null);

public sealed class ContinuousTestDaemonHostOptions
{
    public bool? Enabled { get; init; }

    public string? KillSwitch { get; init; }

    public string? WorkspaceId { get; init; }

    public string MillerVersion { get; init; } = "dev";

    public ContinuousTestStore? Store { get; init; }

    public ContinuousTestDaemonQueue? Queue { get; init; }

    public ContinuousTestRevisionPoller? Poller { get; init; }

    public IReadOnlyList<ContinuousTestProject>? Projects { get; init; }

    public CtExecutionBudget? Budget { get; init; }

    public bool AcquireLease { get; init; } = true;

    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.UtcNow;

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public Action<ContinuousTestDaemonSnapshot>? StatusSink { get; init; }

    /// <summary>
    /// Test seam for the control-plane status write. Production leaves this null and the loop
    /// publishes through its own lease. A test sets it to fail the write on purpose, which is the
    /// only way to prove the loop survives a sharing violation without a second live process.
    /// </summary>
    public Action<CtDaemonLifecycleState, string>? StatusWriter { get; init; }

    /// <summary>
    /// Test seam for the PER-WORKTREE status write, which <see cref="StatusWriter"/> cannot serve
    /// because that seam carries no root and every adopted worktree publishes into its own
    /// <c>.miller/ct/</c>. Returns whether the record landed. Production leaves this null and the
    /// write goes through <see cref="CtDaemonLease.WriteStatus(string, CtDaemonStatusRecord, CtDaemonWriteMode)"/>.
    /// A test returns false to prove a lost write is retried instead of being remembered as published.
    ///
    /// <para>The write MODE is part of the seam. Without it a test that delegates to the real writer
    /// would create a control plane on a detach record, which is the opposite of what the production
    /// path does — a seam that cannot reproduce the behaviour it stands in for is worse than none.</para>
    /// </summary>
    public Func<string, CtDaemonStatusRecord, CtDaemonWriteMode, bool>? AdoptedStatusWriter { get; init; }

    public IContinuousTestDaemonEnqueuer? Enqueuer { get; init; }

    /// <summary>
    /// Quiet window the idle backlog drain requires before it may schedule the owed stale set
    /// (<see cref="CtIdleDrainPolicy"/>). Null resolves the same <c>MILLER_CT_DEBOUNCE</c> value
    /// the poller uses, so the drain waits at least one debounce of silence; a test injects a
    /// fixed value to stay deterministic.
    /// </summary>
    public TimeSpan? IdleDrainQuietPeriod { get; init; }

    /// <summary>
    /// The liveness cell shared with the provider factory and the queue. Supplied, the loop publishes what the
    /// daemon is doing and how lively its child is; left null, the status file carries the lifecycle state
    /// alone, exactly as before.
    /// </summary>
    public CtRunActivityCell? RunActivity { get; init; }

    /// <summary>
    /// Where the loop reports a failure it would otherwise discard. Production points this at
    /// <see cref="CtDaemonLog.Write"/>, so a poll error lands in the shared daily log instead of
    /// being swallowed; a unit test leaves it null and the loop stays silent.
    ///
    /// <para>The loop invokes this only on the LIVE branch. Both disabled branches return before the
    /// loop starts, so a workspace under <c>MILLER_CT=off</c> writes no line and creates no
    /// <c>.miller/logs</c> directory even when the caller supplies a real sink. That is the
    /// permanent zero-work guarantee, and
    /// <c>ForbiddenEnqueueTests.A_disabled_daemon_writes_no_log_line_and_creates_no_logs_directory</c>
    /// holds it.</para>
    /// </summary>
    public Action<string>? Diagnostic { get; init; }

    /// <summary>
    /// Family-worktree adoption. Null (the default) keeps the host single-workspace, exactly as
    /// before. Supplied, the live loop scans for registered, opted-in linked worktrees of ITS OWN
    /// repo and serves each through a per-worktree <see cref="ContinuousTestWorkspaceContext"/> -
    /// one loop, one lease, N contexts sharing only the execution budget.
    /// </summary>
    public ContinuousTestWorktreeAdoptionOptions? WorktreeAdoption { get; init; }
}

/// <summary>
/// One workspace the daemon serves: the machinery bound to that workspace's OWN index and
/// <c>ct.db</c>. The host's primary workspace is a context, and every adopted worktree is another;
/// the single loop iterates them as data. Nothing here is shared across workspaces - the shared
/// pieces (the process, the lease on the main root, the execution budget, the run-activity cell)
/// live on the host.
/// </summary>
public sealed class ContinuousTestWorkspaceContext : IDisposable
{
    public required string WorkspaceRoot { get; init; }

    public required string WorkspaceId { get; init; }

    public ContinuousTestStore? Store { get; init; }

    public ContinuousTestDaemonQueue? Queue { get; init; }

    public ContinuousTestRevisionPoller? Poller { get; init; }

    public IReadOnlyList<ContinuousTestProject> Projects { get; init; } = [];

    public IContinuousTestDaemonEnqueuer? Enqueuer { get; init; }

    /// <summary>
    /// What the host disposes when this context detaches - usually the context's own store. After
    /// detach the daemon never writes that workspace's <c>ct.db</c> again.
    /// </summary>
    public IDisposable? Owned { get; init; }

    internal CtWatchHealth Watch { get; } = new();

    internal CtDegradationBackoff Backoff { get; } = new();

    /// <summary>
    /// How long this context's poll has answered "the delta is unreadable" in a row, and the plain
    /// reason to publish once that run is long enough to mean automatic runs have stopped.
    /// </summary>
    internal CtUnavailableDeltaTracker Unavailable { get; } = new();

    internal CtFreshnessKey? StartedAt;

    internal CtFreshnessKey? LatestFreshness;

    /// <summary>
    /// Whether the LAST poll proved the context settled: a healthy answer whose saved cursor
    /// equals the live revision (the poller's <c>same_revision</c>). Any other answer — an
    /// enqueue, a rebuild, a degradation, an unavailable delta — clears it, so the idle drain
    /// only ever fires from a reconciled cursor.
    /// </summary>
    internal bool PollSettled;

    /// <summary>
    /// When a poll last observed anything other than a settled no-op, stamped on the loop's own
    /// clock. The idle drain's quiet window counts from here; null means no activity was ever
    /// observed, which reads as quiet.
    /// </summary>
    internal DateTimeOffset? LastActivityAt;

    /// <summary>
    /// The idle-drain cooldown anchor: the last idle drain this loop scheduled for the context,
    /// initialized to the loop's FIRST evaluation so a freshly started daemon stays status-only
    /// for one full cooldown before draining a backlog it did not watch grow.
    /// </summary>
    internal DateTimeOffset? LastIdleDrainAt;

    /// <summary>
    /// The record that is ON DISK for this workspace, not the one the host meant to write. Set only
    /// after a write returns, so one lost write cannot arm the dedupe guard forever.
    /// </summary>
    internal CtDaemonLifecycleState? PublishedState;

    internal string? PublishedReason;

    /// <summary>
    /// The attach record a failed write still owes this worktree, retried on later scan passes and
    /// bounded by <see cref="OwedAttempts"/>. A fresh attach builds a fresh context, so all three
    /// fields reset by themselves.
    /// </summary>
    internal CtDaemonLifecycleState? OwedState;

    internal string? OwedReason;

    internal int OwedAttempts;

    public void Dispose() => Owned?.Dispose();
}

/// <summary>
/// How the host discovers and builds family-worktree contexts. Discovery MUST read the workspace
/// registry through a non-creating path (<c>WorkspaceRegistry.TryOpenReadOnly</c>): a scan that
/// runs four times a second must never mint directories or schema as a side effect. The host
/// applies the adoption predicate itself - registered AND root present AND a linked worktree of
/// the daemon's own repo AND opted in AND not running its own daemon - so the discovery function
/// only enumerates candidates.
/// </summary>
public sealed class ContinuousTestWorktreeAdoptionOptions
{
    /// <summary>
    /// Registered workspace roots, from the registry's non-creating read path. A read FAILURE must
    /// THROW rather than degrade to an empty list: on a throw the host keeps its current adopted
    /// set and refuses routed attaches, where an empty list means "nothing registered" and
    /// detaches every adopted worktree.
    /// </summary>
    public required Func<IReadOnlyList<string>> DiscoverRegisteredRoots { get; init; }

    /// <summary>
    /// Builds the per-worktree machinery for a qualifying root: its own store, queue bound to that
    /// store, and poller bound to that worktree's index. Null refuses the adoption this cycle.
    /// </summary>
    public required Func<string, ContinuousTestWorkspaceContext?> CreateContext { get; init; }

    /// <summary>How often the live loop re-scans for attach/detach. Zero scans every pass.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Seam for the non-acquiring own-daemon probe. Null uses the live lease file.</summary>
    public Func<string, bool>? HasOwnLiveDaemon { get; init; }

    /// <summary>Seam for the opt-in probe. Null uses <see cref="ContinuousTestPolicy"/>.</summary>
    public Func<string, bool>? IsOptedIn { get; init; }

    /// <summary>Seam for git-layout resolution. Null uses <see cref="GitWorktreeLayout.Resolve"/>.</summary>
    public Func<string, GitWorktreeLayout?>? ResolveLayout { get; init; }
}

/// <summary>
/// Long-running CT daemon loop. Task 12 wires this behind <c>tests serve</c>.
/// Status reads and kill-switch off construct no CT machinery.
/// </summary>
public sealed class ContinuousTestDaemonHost
{
    /// <summary>
    /// How many times a failed adopted-status write is retried before the daemon gives up on that
    /// root. At the five-second production scan interval this is about a minute of repair attempts,
    /// which covers a transient sharing violation without spinning forever on an unwritable root.
    /// </summary>
    private const int MaxAdoptedStatusAttempts = 12;

    /// <summary>
    /// What a request file whose name is not a legal command id is renamed to. It keeps the original
    /// name for a person to read and leaves the <c>*.request.json</c> listing, so the drain cannot
    /// pick it up again.
    /// </summary>
    private const string RejectedRequestSuffix = ".rejected";

    /// <summary>
    /// The poll answer that says the impact of the interval could not be read. Named here because the
    /// loop's reading of it is a decision, not a log string: see the poll path in
    /// <see cref="PollContextAsync"/>.
    /// </summary>
    private const string UnavailableDeltaReason = "unavailable_delta";

    /// <summary>
    /// The poll answer that proves the saved cursor equals the live revision: nothing changed,
    /// nothing is owed by the poll path. It is the only answer the idle drain accepts as settled.
    /// </summary>
    private const string SettledPollReason = "same_revision";

    private readonly string _workspaceRoot;
    private readonly ContinuousTestDaemonHostOptions _options;
    private readonly CtExecutionBudget _budget;
    private readonly string _workspaceId;
    private readonly CtRunActivityCell? _runActivity;

    /// <summary>The daemon's own workspace. Always present, always first in the iteration.</summary>
    private readonly ContinuousTestWorkspaceContext _primary;

    /// <summary>Adopted family-worktree contexts, keyed by full root path.</summary>
    private readonly Dictionary<string, ContinuousTestWorkspaceContext> _adopted;

    /// <summary>
    /// Roots an explicit worktree <c>stop</c> detached. The scan must not silently re-adopt them -
    /// that would make the stop a five-second pause - but an explicit routed <c>run</c> clears the
    /// suppression, because it is the user asking for that worktree again.
    /// </summary>
    private readonly HashSet<string> _stopDetached;

    private readonly CtIdleDrainPolicy _idleDrainPolicy;
    private readonly ContinuousTestWorktreeAdoptionOptions? _adoption;
    private readonly Func<string, bool> _hasOwnLiveDaemon;
    private readonly Func<string, bool> _isOptedIn;
    private readonly Func<string, GitWorktreeLayout?> _resolveLayout;
    private DateTimeOffset? _lastWorktreeScanAt;
    private CtDaemonLeaseIdentity? _leaseIdentity;

    /// <summary>
    /// Command ids this loop has already handled. The ack FILE is the durable record, but the write
    /// of that file is now guarded, so a workspace whose ack directory cannot be written would
    /// otherwise replay every request on every poll — and a replayed <c>run</c> re-enqueues and
    /// re-executes the suite forever. Pruned each pass to the requests still on disk, so it stays
    /// bounded by the command directory.
    /// </summary>
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);

    /// <summary>
    /// Request files this loop has already reported as badly named, so the report is written once
    /// rather than on every pass. Pruned each pass to the files still on disk, like
    /// <see cref="_acknowledged"/>, so a long-running daemon cannot grow it without bound.
    /// </summary>
    private readonly HashSet<string> _malformedRequests = new(StringComparer.Ordinal);

    private DateTimeOffset _runStartedAtUtc;

    // Read by the pulse task while the main loop writes them. Volatile rather than locked: a republish that
    // reads a state one poll old costs nothing, where a lock would put the pulse behind the main loop.
    private volatile CtDaemonLifecycleState _publishedState = CtDaemonLifecycleState.Running;
    private volatile string _publishedReason = "starting";
    private volatile string? _publishedPauseReason;

    /// <summary>
    /// When the MAIN LOOP last MOVED, as ticks so the field can be read and written atomically. Zero until
    /// the loop's first stamp, which publishes a null tick — an unproven loop, not a stalled one. The pulse
    /// copies this value; it must never stamp one of its own, or a wedged loop would keep reporting a fresh
    /// tick forever.
    /// </summary>
    private long _loopTickTicks;

    /// <summary>
    /// The SAME stamp on a monotonic clock, which no wall-clock correction can move. The published age is
    /// this subtracted at write time; see <see cref="CtDaemonStatusRecord.LoopAgeSeconds"/> for why the
    /// daemon subtracts rather than publishing the raw count.
    /// </summary>
    private long _loopTickTimestamp;

    /// <summary>
    /// Whether the main loop is inside a drain right now. A FALLBACK for the activity a host without a
    /// <see cref="ContinuousTestDaemonHostOptions.RunActivity"/> cell would otherwise publish — a documented,
    /// legal configuration that used to publish <c>idle</c> for the whole drain, so a reader measured the
    /// run's elapsed time as loop lag and called a working daemon wedged. A host WITH a cell reads the cell,
    /// which is authoritative and also names the run.
    /// </summary>
    private volatile bool _drainInFlight;

    public ContinuousTestDaemonHost(string workspaceRoot, ContinuousTestDaemonHostOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _options = options ?? new ContinuousTestDaemonHostOptions();
        _workspaceId = _options.WorkspaceId ?? WorkspaceId.FromCanonicalRoot(_workspaceRoot);
        _budget = _options.Budget ?? CtExecutionBudget.FromEnvironment(MillerHome.ResolveMillerDirectory());
        _runActivity = _options.RunActivity;
        _primary = new ContinuousTestWorkspaceContext
        {
            WorkspaceRoot = _workspaceRoot,
            WorkspaceId = _workspaceId,
            Store = _options.Store,
            Queue = _options.Queue,
            Poller = _options.Poller,
            Projects = _options.Projects ?? [],
            Enqueuer = _options.Enqueuer,
        };
        _idleDrainPolicy = new CtIdleDrainPolicy(
            _options.IdleDrainQuietPeriod
                ?? ContinuousTestRevisionPoller.ResolveDebounceDelay(
                    Environment.GetEnvironmentVariable(ContinuousTestRevisionPoller.DebounceEnvironmentVariable)));
        _adoption = _options.WorktreeAdoption;
        _hasOwnLiveDaemon = _adoption?.HasOwnLiveDaemon
            ?? (root => CtDaemonLease.TryReadLive(root) is not null);
        _isOptedIn = _adoption?.IsOptedIn ?? (root => ContinuousTestPolicy.IsWorkspaceOptedIn(root));
        _resolveLayout = _adoption?.ResolveLayout ?? GitWorktreeLayout.Resolve;
        _adopted = new Dictionary<string, ContinuousTestWorkspaceContext>(PathKeyComparer);
        _stopDetached = new HashSet<string>(PathKeyComparer);
    }

    /// <summary>
    /// The one stdout line the daemon prints at start. The launcher redirects daemon stdout into
    /// <c>daemon.out.log</c>, which holds nothing else on a healthy run, so this line is what turns
    /// a 0-byte mystery file into a pointer at the real diagnostics: the shared
    /// <c>.miller/logs/miller-&lt;yyyyMMdd&gt;.log</c> daily pair (<c>role:ct</c> lines). Total by
    /// design: a root the path helpers refuse degrades the path, never the line, and every input is
    /// flattened so the result is always exactly one line. Writes nothing anywhere.
    /// </summary>
    public static string StartupBreadcrumb(
        string workspaceRoot, string millerVersion, int pid, DateTimeOffset utcNow)
    {
        string diagnostics;
        try
        {
            (diagnostics, _) = CtDaemonLog.LogFilePaths(CtDaemonLog.LogsDirectory(workspaceRoot), utcNow);
        }
        catch (Exception)
        {
            diagnostics = "unavailable";
        }

        string line = $"ct daemon start version={millerVersion} pid={pid} "
            + $"diagnostics={diagnostics} (role:ct in the shared daily log)";
        return string.Join(
            " ",
            line.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public ContinuousTestDaemonSnapshot? LastSnapshot { get; private set; }

    /// <summary>
    /// The daemon state as a one-shot reader must judge it: the published record, and then a liveness
    /// probe on the identity that record names.
    ///
    /// <para>A daemon that dies without a clean shutdown — killed to free the locked binary, crashed, or
    /// taken down with the process that spawned it — leaves its last <c>Running</c> record on disk, and
    /// nothing rewrites that file once the writer is gone. Observed live on 2026-08-21: the process was
    /// gone, <c>tests stop</c> answered "no daemon", and <c>tests status</c> reported
    /// <c>daemon: running, idle</c>. Liveness rides the OS lock and the recorded identity, never the
    /// published state.</para>
    ///
    /// <para>Separate from <see cref="ReadStatus"/> because the probe reads a second file and asks the OS
    /// about a process. The run wait polls the record every 50ms and runs its own probe on a slower clock,
    /// so making every read probe would add twelve thousand process lookups to one full wait.</para>
    /// </summary>
    public static ContinuousTestDaemonSnapshot ReadLiveStatus(
        string workspaceRoot,
        Func<CtDaemonLeaseIdentity, bool>? isLive = null) =>
        ReadStatus(workspaceRoot, isLive ?? CtDaemonLease.IsIdentityLive);

    /// <summary>
    /// The published record verbatim, with no liveness probe. Callers that poll in a loop use this and
    /// probe on their own clock; every other reader wants <see cref="ReadLiveStatus"/>.
    /// </summary>
    public static ContinuousTestDaemonSnapshot ReadStatus(string workspaceRoot) =>
        ReadStatus(workspaceRoot, isLive: null);

    private static ContinuousTestDaemonSnapshot ReadStatus(
        string workspaceRoot,
        Func<CtDaemonLeaseIdentity, bool>? isLive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!ContinuousTestPolicy.ShouldConstructEngine(workspaceRoot))
        {
            return new ContinuousTestDaemonSnapshot(
                CtDaemonLifecycleState.Stopped,
                "disabled",
                ContinuousTestVerdict.Unknown,
                null,
                0,
                0,
                Enabled: false,
                Executing: false);
        }

        CtDaemonStatusRecord? record = CtDaemonLease.TryReadStatus(workspaceRoot);
        // Only an ACTIVE published state can be contradicted by a dead process. A clean shutdown
        // publishes Stopped and THEN exits, so probing that record would relabel every orderly stop
        // "daemon gone" and destroy the very distinction the probe exists to draw.
        if (isLive is not null
            && record is not null
            && record.State is CtDaemonLifecycleState.Running or CtDaemonLifecycleState.Paused
            && !PublisherIsLive(workspaceRoot, record, isLive))
        {
            // Every field of a dead daemon's record lies the same way: one killed mid-run names the
            // project it was executing, so keeping the activity would report a run no process is running.
            return new ContinuousTestDaemonSnapshot(
                CtDaemonLifecycleState.Stopped,
                "daemon gone",
                ContinuousTestVerdict.Unknown,
                null,
                0,
                0,
                Enabled: true,
                Executing: false,
                LoopHealth: CtDaemonLoopHealth.Unknown("the daemon is gone"));
        }

        return new ContinuousTestDaemonSnapshot(
            record?.State ?? CtDaemonLifecycleState.Stopped,
            record?.Reason ?? "stopped",
            ContinuousTestVerdict.Unknown,
            null,
            0,
            0,
            Enabled: true,
            // Executing used to be hardcoded false here, so an out-of-process reader could never tell a busy
            // daemon from an idle one. It now comes from the published record.
            Executing: record?.Activity == CtDaemonActivity.Executing,
            Activity: record?.Activity ?? CtDaemonActivity.Idle,
            Run: record?.Run,
            // Whether the loop behind that record is still turning. The pulse keeps this file moving even
            // when the loop is wedged, so the state above cannot answer it and the record's own two stamps
            // must.
            LoopHealth: CtDaemonLoopHealth.Evaluate(record),
            AutoRunsPaused: record?.AutoRunsPaused ?? false,
            PauseReason: record?.PauseReason);
    }

    /// <summary>
    /// Whether the process that published <paramref name="record"/> is still alive.
    ///
    /// <para>The record names its own writer, and an adopted worktree's record names the family daemon
    /// that serves it, so this probe reaches a dead family daemon through every worktree it left behind.
    /// A record written before that field existed carries no identity, so it falls back to the lease —
    /// which every daemon writes when it takes the lock. Neither one means nothing holds the lock, and
    /// stopped is then the honest answer.</para>
    /// </summary>
    private static bool PublisherIsLive(
        string workspaceRoot,
        CtDaemonStatusRecord record,
        Func<CtDaemonLeaseIdentity, bool> isLive)
    {
        CtDaemonLeaseIdentity? identity =
            record.Identity ?? CtDaemonLease.TryRead(workspaceRoot)?.Identity;
        return identity is not null && isLive(identity);
    }

    public static async Task<ContinuousTestDaemonSnapshot> RunAsync(
        string workspaceRoot,
        ContinuousTestDaemonHostOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ContinuousTestDaemonHostOptions();
        if (!ContinuousTestPolicy.ShouldConstructEngine(workspaceRoot, options.KillSwitch, options.Enabled))
        {
            var disabled = new ContinuousTestDaemonSnapshot(
                CtDaemonLifecycleState.Stopped,
                "disabled",
                ContinuousTestVerdict.Unknown,
                null,
                0,
                0,
                Enabled: false,
                Executing: false);
            options.StatusSink?.Invoke(disabled);
            return disabled;
        }

        var host = new ContinuousTestDaemonHost(workspaceRoot, options);
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return host.LastSnapshot ?? ReadStatus(workspaceRoot);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _runStartedAtUtc = _options.Clock();
        if (!ContinuousTestPolicy.ShouldConstructEngine(_workspaceRoot, _options.KillSwitch, _options.Enabled))
        {
            Publish(DisabledSnapshot());
            return;
        }

        using CtDaemonLease? lease = _options.AcquireLease
            ? CtDaemonLease.TryAcquire(_workspaceRoot, _options.MillerVersion)
            : null;
        if (_options.AcquireLease && lease is null)
        {
            Publish(new ContinuousTestDaemonSnapshot(
                CtDaemonLifecycleState.Stopped,
                "lease held",
                ContinuousTestVerdict.Unknown,
                null,
                0,
                0,
                true,
                false));
            return;
        }

        _leaseIdentity = lease?.Record.Identity;
        TryWriteStatus(lease, CtDaemonLifecycleState.Running, "status-only");
        Publish(Evaluate("status-only", CtDaemonLifecycleState.Running, executing: false));

        if (_primary.Enqueuer is null && _primary.Queue is null)
            throw new InvalidOperationException("CT daemon host requires a queue or enqueuer");

        // The pulse loop exits only on cancellation, and a stop COMMAND does not cancel the caller's
        // token. Its own source lets the shutdown tail stop the pulse and then observe it, instead of
        // blocking the exit for a whole pulse interval or leaving the task unobserved.
        using var pulseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pulse = lease is null
            ? Task.CompletedTask
            : PulseStatusAsync(lease, pulseCancellation.Token);

        while (!cancellationToken.IsCancellationRequested)
        {
            StampLoopTick();

            bool stopRequested;
            try
            {
                stopRequested = ProcessCommands(lease, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (stopRequested || cancellationToken.IsCancellationRequested)
                break;

            ScanWorktrees();

            bool pollCancelled = false;
            foreach (ContinuousTestWorkspaceContext context in EnumerateContexts())
            {
                if (!await PollContextAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    pollCancelled = true;
                    break;
                }
            }

            if (pollCancelled)
                break;

            DateTimeOffset now = _options.Clock();
            foreach (ContinuousTestWorkspaceContext context in EnumerateContexts())
                TryScheduleIdleDrain(context, now);

            bool executing = false;
            List<ContinuousTestWorkspaceContext>? ready = null;
            foreach (ContinuousTestWorkspaceContext context in EnumerateContexts())
            {
                if (context.Queue is not null && context.Backoff.CanEnqueue && context.Queue.HasReadyWork(now))
                    (ready ??= []).Add(context);
            }

            if (ready is not null)
            {
                CtExecutionBudgetLease? acquired;
                try
                {
                    acquired = _budget.TryAcquire(
                        new CtExecutionBudgetRequest(_workspaceRoot, "run"),
                        TimeSpan.Zero,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // A cancellation that lands inside the acquire must leave through the shutdown
                    // tail like every other one, not escape the loop as an exception.
                    break;
                }

                using CtExecutionBudgetLease? budget = acquired;
                if (budget is null)
                {
                    // Work is ready and accepted; another workspace holds the one execution slot. A caller
                    // waiting for this daemon to settle must keep waiting, so this is not idle.
                    _runActivity?.EnterQueued();
                    Publish(Evaluate("execution budget held", CtDaemonLifecycleState.Paused, executing: false));
                    TryWriteStatus(lease, CtDaemonLifecycleState.Paused, "execution budget held");
                }
                else
                {
                    executing = true;

                    // Marked BEFORE the status write, so the record this poll publishes already says
                    // "executing" rather than inheriting the previous poll's activity.
                    _drainInFlight = true;
                    _runActivity?.BeginDrain();
                    TryWriteStatus(lease, CtDaemonLifecycleState.Running, "executing");
                    try
                    {
                        // Every ready context drains under the ONE budget lease taken above: N family
                        // worktrees never mean N concurrent suites.
                        foreach (ContinuousTestWorkspaceContext context in ready)
                        {
                            await context.Queue!.DrainReadyAsync(now, cancellationToken).ConfigureAwait(false);

                            // A run is activity: the idle drain's quiet window restarts behind it,
                            // so a follow-up drain needs both fresh quiet and the cooldown.
                            context.LastActivityAt = _options.Clock();
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    finally
                    {
                        // Stamped BEFORE the cell goes idle, and not after, so no pulse can read the pair
                        // "idle activity, pre-drain tick" — the shape that reported a healthy daemon as
                        // wedged for the run's whole duration once the run had ended.
                        StampLoopTick();

                        // One drain runs every ready project. Cleared only when the whole drain returns, so a
                        // waiting caller cannot slip through the gap between two of its projects.
                        _drainInFlight = false;
                        _runActivity?.EndDrain();
                    }
                }
            }
            else
            {
                _runActivity?.EnterIdle();

                // A daemon whose poll is stuck says so. "idle" is true but useless here: the loop is
                // idle BECAUSE it cannot read the delta, and a reader who is waiting for an automatic
                // run has no other way to learn that. The reason string is what `tests status` prints
                // as daemon.reason, so no new key is needed for the answer to reach a person.
                string reason = _primary.Unavailable.StuckReason
                    ?? (_primary.StartedAt is null ? "status-only" : "idle");
                TryWriteStatus(lease, CtDaemonLifecycleState.Running, reason);
                Publish(Evaluate(reason, CtDaemonLifecycleState.Running, executing: false));
            }

            if (executing)
                Publish(Evaluate("executing", CtDaemonLifecycleState.Running, executing: true));

            try
            {
                await _options.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        TryWriteStatus(lease, CtDaemonLifecycleState.Stopped, "stopped");
        Publish(Evaluate("stopped", CtDaemonLifecycleState.Stopped, executing: false));
        ReleaseAdoptedContexts();

        // Stop the pulse first, then await it. Awaiting an uncancelled pulse would hang the exit,
        // and abandoning it would leave both an unobserved exception and a status write that can still
        // land after the lease below is released.
        await pulseCancellation.CancelAsync().ConfigureAwait(false);
        await pulse.ConfigureAwait(false);
    }

    /// <summary>
    /// Republishes <c>daemon.status.json</c> for the life of the loop, including while a long drain blocks
    /// the main loop. Never throws: liveness is carried by the OS lock on <c>daemon-v1.lock</c>; the status
    /// record is an observable freshness signal only.
    ///
    /// <para>The main loop is BLOCKED for the whole drain, so without this the status file froze at
    /// "executing" until the run ended - which is exactly how a 12-minute run and a wedged one looked
    /// identical. The lifecycle state and reason are the last ones the main loop chose; only the activity,
    /// the child's liveness, and the record's own timestamp are refreshed here. The loop tick is copied
    /// verbatim, which is what lets a reader separate "this pulse is alive" from "the loop is alive".</para>
    ///
    /// <para>It used to write a second file, <c>daemon.heartbeat.json</c>, 5,760 times a day. No production
    /// code ever read it, and the one signal it could have carried - that the process is still there - is
    /// already carried by the OS lock and the recorded identity.</para>
    /// </summary>
    private async Task PulseStatusAsync(CtDaemonLease lease, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _options.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                PublishStatus(lease, _publishedState, _publishedReason);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The single guarded path for every status write in this loop. Never throws, for the same
    /// reason <see cref="PulseStatusAsync"/> never throws.
    ///
    /// The loop republishes <c>daemon.status.json</c> on every poll interval (250 ms by default)
    /// while a waiting <c>tests run --wait</c> reads it every 50 ms, so a read and a publish
    /// overlap constantly. <see cref="CtDaemonJson.WriteAtomic"/> retries the replace a bounded
    /// number of times and then rethrows, so the sharing violation still reaches this loop. An
    /// unguarded write would kill the loop while the lease still holds <c>daemon-v1.lock</c>,
    /// which leaves the workspace with a live lock and no daemon behind it. Liveness rides that
    /// OS lock, not this file: the status record is an observable signal, so one lost write costs
    /// a stale reason string for one interval, where an escaped exception costs the daemon.
    /// </summary>
    private void TryWriteStatus(CtDaemonLease? lease, CtDaemonLifecycleState state, string reason)
    {
        // Remembered so the pulse task can republish the SAME lifecycle state with fresh activity. The pulse
        // must never invent a state of its own — that includes the pause, which only the loop may decide.
        _publishedState = state;
        _publishedReason = reason;
        _publishedPauseReason = state == CtDaemonLifecycleState.Stopped
            ? null
            : PauseReasonOf(_primary.Unavailable.StuckReason);

        // Stamped before the write, so a write that fails still records that the loop moved — the tick
        // measures the loop, not the filesystem.
        StampLoopTick();
        PublishStatus(lease, state, reason);
    }

    /// <summary>
    /// The reason code the status record and <c>tests status</c> carry: the tracker's published wording
    /// minus its <c>auto-runs paused: </c> prefix, so the renderers can re-attach exactly one prefix.
    /// </summary>
    private static string? PauseReasonOf(string? stuckReason)
    {
        const string prefix = "auto-runs paused: ";
        if (stuckReason is null)
            return null;
        return stuckReason.StartsWith(prefix, StringComparison.Ordinal)
            ? stuckReason[prefix.Length..]
            : stuckReason;
    }

    /// <summary>
    /// The loop reached another point of its own. Stamped at the top of every pass and when a drain returns,
    /// not only at the loop's write points: the loop publishes at most once per pass and can be parked in its
    /// poll delay for a long time after a run, so a tick that tracked the WRITES read as lag the moment the
    /// pulse republished the idle that followed a long run.
    /// </summary>
    /// <remarks>
    /// The monotonic stamp is written FIRST, so a reader that finds a non-zero wall stamp always finds the
    /// monotonic one that goes with it.
    /// </remarks>
    private void StampLoopTick()
    {
        Volatile.Write(ref _loopTickTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _loopTickTicks, _options.Clock().UtcTicks);
    }

    /// <summary>
    /// Writes one status record, attaching whatever the activity cell currently reports and the main loop's
    /// last tick VERBATIM. Called by the main loop on every poll and by the pulse task while a drain blocks
    /// that loop.
    /// </summary>
    private void PublishStatus(CtDaemonLease? lease, CtDaemonLifecycleState state, string reason)
    {
        try
        {
            if (_options.StatusWriter is { } writer)
            {
                writer(state, reason);
                return;
            }

            if (lease is null)
                return;

            (CtDaemonActivity activity, CtDaemonRunProgress? run) = ReadActivity();

            // ONE clock stamps both halves of the pair a reader subtracts. The tick came from this clock
            // while the record's timestamp came from TimeProvider.System, and they agreed only because both
            // default to UtcNow. The record is built here rather than through the lease's convenience
            // overload because the pause fields ride every publish, the pulse's republishes included.
            string? pauseReason = _publishedPauseReason;
            CtDaemonLease.WriteStatus(
                lease.Record.WorkspaceRoot,
                new CtDaemonStatusRecord(
                    state,
                    reason,
                    lease.Record.Identity,
                    _options.Clock(),
                    activity,
                    run,
                    LoopTick(),
                    LoopAgeSeconds(),
                    AutoRunsPaused: pauseReason is not null,
                    PauseReason: pauseReason));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The main loop's last stamp, or null before its first one. Never the current time: the pulse calls
    /// this too, and a pulse that stamped "now" would republish a fresh tick for a loop that had stopped.
    /// </summary>
    private DateTimeOffset? LoopTick()
    {
        long ticks = Volatile.Read(ref _loopTickTicks);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>
    /// How long the loop has been standing still, measured on the MONOTONIC clock and rounded to the
    /// millisecond. Null before the loop's first stamp, exactly as <see cref="LoopTick"/> is: the two are
    /// published together and a reader that finds one finds the other.
    ///
    /// <para>The daemon subtracts here rather than publishing its raw tick count because the reader is a
    /// different process: monotonic counts are not comparable across processes, but an AGE is. The wall-clock
    /// pair stays in the record for a reader from an older build.</para>
    /// </summary>
    private double? LoopAgeSeconds()
    {
        if (Volatile.Read(ref _loopTickTicks) == 0)
            return null;
        long elapsed = Stopwatch.GetTimestamp() - Volatile.Read(ref _loopTickTimestamp);
        return elapsed <= 0 ? 0 : Math.Round(elapsed / (double)Stopwatch.Frequency, 3);
    }

    /// <summary>
    /// What the daemon is doing, from the activity cell when there is one and from the loop's own drain flag
    /// when there is not. A cell-less host is a documented configuration, and it must still say "executing"
    /// while it is blocked in a drain — an idle record with a frozen loop tick is exactly the shape a reader
    /// judges as a wedged loop.
    /// </summary>
    private (CtDaemonActivity Activity, CtDaemonRunProgress? Run) ReadActivity() =>
        _runActivity?.Read()
            ?? (_drainInFlight ? CtDaemonActivity.Executing : CtDaemonActivity.Idle, null);

    /// <summary>
    /// Drains the file command channel. Returns <c>true</c> when a live stop request asked this
    /// daemon to exit.
    ///
    /// A stop used to leave through <c>throw new OperationCanceledException()</c>. Production never
    /// cancels the loop token, so that throw was the daemon's only exit, and it jumped over the whole
    /// shutdown tail: the final <c>Stopped</c> status, the final snapshot, and the pulse await
    /// were unreachable, and every requested stop reached the CLI as an error and exit code 1. A
    /// returned flag ends the loop at the top instead, so the tail runs on the normal, requested path
    /// and a real cancellation keeps its own separate route out.
    /// </summary>
    private bool ProcessCommands(CtDaemonLease? lease, CancellationToken cancellationToken)
    {
        string commandDir = CtDaemonProtocol.CommandDirectory(_workspaceRoot);
        if (!Directory.Exists(commandDir))
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenMalformed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(commandDir, "*.request.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = Path.GetFileName(path)[..^".request.json".Length];
            if (!CtDaemonProtocol.IsCommandId(id))
            {
                seenMalformed.Add(path);
                RejectMalformedRequestFile(path);
                continue;
            }

            seen.Add(id);
            if (_acknowledged.Contains(id) || CtCommandChannel.TryReadAck(_workspaceRoot, id) is not null)
                continue;
            CtDaemonCommandRequest? request = CtCommandChannel.TryReadRequest(_workspaceRoot, id);
            if (request is null)
                continue;
            bool stopRequested;
            try
            {
                stopRequested = ProcessCommand(lease, id, request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One poisonous request must never kill the daemon. The file stays on disk
                // forever, so an escaped throw here was a crash on THIS request at every restart
                // (a workspace_root that Path.GetFullPath refuses, for example). Reject it,
                // remember the ack, and keep draining.
                Diagnostic($"ct command {id} rejected {CtDaemonLog.FailureDetail(exception)}");
                TryWriteAck(id, "invalid-request", CtDaemonCommandState.Rejected);
                continue;
            }

            if (stopRequested)
                return true;
        }

        _acknowledged.IntersectWith(seen);
        _malformedRequests.IntersectWith(seenMalformed);
        return false;
    }

    /// <summary>
    /// Moves a request file whose NAME is not a legal command id out of the drain's way.
    ///
    /// <para>The stem is the command id everywhere else in the protocol, and every protocol path
    /// REFUSES an id outside <c>^[A-Za-z0-9._-]+$</c> by throwing. That throw used to escape from the
    /// acknowledgement probe, which runs BEFORE the per-command guard below, so one file called
    /// <c>bad name.request.json</c> killed the daemon — and the file stays on disk, so every restart
    /// died on it again. Moving it aside is the only repair that holds: the bad id must never reach a
    /// protocol path at all, and the reject acknowledgement the guard writes is itself a protocol path,
    /// so rejecting it as a poisonous request would throw in exactly the same place.</para>
    ///
    /// <para>The suffix leaves the file readable for a person and outside the <c>*.request.json</c>
    /// listing, so the drain never sees it again. A move that cannot happen (a held file, a read-only
    /// directory) costs one skip per pass and nothing else — the loop lives, which is the whole
    /// point.</para>
    /// </summary>
    private void RejectMalformedRequestFile(string path)
    {
        bool moved = false;
        try
        {
            File.Move(path, path + RejectedRequestSuffix, overwrite: true);
            moved = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        // Reported once per file. A successful move makes the path unrepeatable anyway; a failed one
        // would otherwise write the same line four times a second forever.
        if (_malformedRequests.Add(path))
            Diagnostic($"ct command file rejected name={Path.GetFileName(path)} moved={moved}");
    }

    /// <summary>
    /// Handles ONE readable command request. Returns <c>true</c> when a live stop request asked
    /// this daemon to exit. Any throw is the caller's signal to reject the request and move on.
    /// </summary>
    private bool ProcessCommand(CtDaemonLease? lease, string id, CtDaemonCommandRequest request)
    {
        string? targetRoot = string.IsNullOrWhiteSpace(request.WorkspaceRoot)
            ? null
            : RootKey(request.WorkspaceRoot);
        bool targetsPrimary = targetRoot is null || PathsEqual(targetRoot, _workspaceRoot);
        if (request.Kind == CtDaemonCommandKind.Stop)
        {
            // A stop request targets the daemon that was alive when it was written. One left
            // unacknowledged by a dead predecessor must not kill this instance at startup.
            if (request.RequestedAtUtc < _runStartedAtUtc)
            {
                TryWriteAck(id, "stale-stop-ignored");
                return false;
            }

            if (!targetsPrimary)
            {
                // A worktree stop detaches THAT context only. It never stops the family daemon:
                // the daemon belongs to the main root, and every other adopted worktree still
                // depends on it.
                if (_adopted.TryGetValue(targetRoot!, out ContinuousTestWorkspaceContext? adopted))
                {
                    DetachWorktree(targetRoot!, adopted, "detached");
                    _stopDetached.Add(targetRoot!);
                    TryWriteAck(id, "detached");
                }
                else
                {
                    TryWriteAck(id, "not-adopted", CtDaemonCommandState.Rejected);
                }

                return false;
            }

            TryWriteAck(id, "stopping");
            TryWriteStatus(lease, CtDaemonLifecycleState.Stopped, "stop");
            return true;
        }

        if (request.Kind == CtDaemonCommandKind.Run)
        {
            ContinuousTestWorkspaceContext? target = targetsPrimary
                ? _primary
                : ResolveRoutedRunTarget(targetRoot!);
            if (target is null)
            {
                TryWriteAck(id, "not-adopted", CtDaemonCommandState.Rejected);
                return false;
            }

            if (target.Queue is not null && target.Projects.Count > 0)
            {
                // Select at the LIVE cursor the poller last observed, never at the daemon's
                // START key. Falling back to StartedAt made every explicit run after an index
                // advance select at the birth revision forever (defect D2): the store rightly
                // records stale-revision results as history-only, so the verdict never
                // converged and only a daemon restart (which reset StartedAt) recovered.
                CtFreshnessKey? selected = request.Freshness
                    ?? target.LatestFreshness
                    ?? target.StartedAt;
                if (selected is not { } freshness)
                {
                    // No poll has landed yet, so no real key exists. The old
                    // ("unspecified", 0) sentinel enqueued anyway, burning the whole suite to
                    // store results at a key that can never match the live one - the
                    // watermark-freshness design removes that sentinel everywhere. Refuse
                    // honestly instead; the caller retries after the first poll lands.
                    TryWriteAck(id, "no-live-key", CtDaemonCommandState.Rejected);
                    return false;
                }

                foreach (ContinuousTestProjectWorkItem item in ContinuousTestProjectInventory.MaterializeProjectWorkItems(
                             target.Projects, target.WorkspaceRoot))
                {
                    target.Queue.EnqueueExplicit(new ContinuousTestDaemonChange(
                        item.Workspace,
                        freshness.Revision.ToString(CultureInfo.InvariantCulture),
                        freshness.IndexIdentity,
                        WorkspaceScope: true,
                        ObservedAt: DateTimeOffset.UtcNow,
                        Command: item.Project.Command,
                        Framework: item.Project.Framework));
                }
            }
        }

        TryWriteAck(id, "run");
        return false;
    }

    /// <summary>
    /// The single guarded path for every command acknowledgement, for the same reason
    /// <see cref="TryWriteStatus"/> exists. <see cref="CtCommandChannel.WriteAck"/> reaches
    /// <see cref="CtDaemonJson.WriteAtomic"/>, which retries the replace a bounded number of times
    /// and then rethrows, so a Defender scan holding the staged temp file — or an ACL that denies the
    /// daemon's user — used to escape this loop and kill the daemon while its lease still held
    /// <c>daemon-v1.lock</c>. A lost ack degrades to the client's existing unacknowledged-command
    /// timeout: <c>stop</c> still stops (the caller falls through to its own wait and kill) and
    /// <c>run</c> still enqueued the work, which costs one command its confirmation instead of
    /// costing the workspace its daemon.
    /// </summary>
    private void TryWriteAck(
        string commandId,
        string reason,
        CtDaemonCommandState state = CtDaemonCommandState.Acknowledged)
    {
        // Remembered whether or not the file lands, so an unwritable ack directory cannot make the
        // loop treat the same request as new on every poll.
        _acknowledged.Add(commandId);
        try
        {
            CtCommandChannel.WriteAck(
                _workspaceRoot,
                new CtDaemonCommandAck(
                    commandId,
                    state,
                    DateTimeOffset.UtcNow,
                    reason));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DemotePriorGreen(ContinuousTestWorkspaceContext context, CtFreshnessKey rebuilt)
    {
        if (context.Store is null)
            return;
        string[] ids = context.Store.ListTestCases(context.WorkspaceId).Select(row => row.Id).ToArray();
        if (ids.Length == 0)
            return;
        context.Store.MarkContinuousTestsStale(context.WorkspaceId, ids, rebuilt);
    }

    private ContinuousTestDaemonSnapshot Evaluate(string reason, CtDaemonLifecycleState state, bool executing)
    {
        ContinuousTestStore? store = _primary.Store;
        CtFreshnessKey? selected = _primary.LatestFreshness ?? _primary.StartedAt;
        ContinuousTestStatusAggregate aggregate = store?.AggregateContinuousTestStatuses(_workspaceId, selected)
            ?? new ContinuousTestStatusAggregate(0, 0, 0, 0);
        ContinuousTestProjectedStatus projected = ContinuousTestStatusProjection.Project(
            selected,
            aggregate,
            watchHealthy: _primary.Watch.IsHealthy);
        (CtDaemonActivity activity, CtDaemonRunProgress? run) = ReadActivity();
        return new ContinuousTestDaemonSnapshot(
            state,
            reason,
            projected.Verdict,
            selected,
            projected.StaleCount,
            aggregate.Total,
            Enabled: true,
            Executing: executing,
            Activity: activity,
            Run: run);
    }

    private ContinuousTestDaemonSnapshot DisabledSnapshot() =>
        new(CtDaemonLifecycleState.Stopped, "disabled", ContinuousTestVerdict.Unknown, null, 0, 0, false, false);

    /// <summary>The primary context first, then a snapshot of the adopted ones.</summary>
    private IEnumerable<ContinuousTestWorkspaceContext> EnumerateContexts()
    {
        yield return _primary;
        if (_adopted.Count == 0)
            yield break;
        foreach (ContinuousTestWorkspaceContext context in _adopted.Values.ToArray())
            yield return context;
    }

    /// <summary>
    /// One context's poll pass: the same freshness/backoff/watch bookkeeping the single-workspace
    /// loop always did, against THIS context's poller, queue, and cursor. Returns false only when
    /// the loop's own token cancelled mid-poll.
    /// </summary>
    private async Task<bool> PollContextAsync(
        ContinuousTestWorkspaceContext context,
        CancellationToken cancellationToken)
    {
        if (context.Poller is null || !context.Backoff.CanPoll)
            return true;
        IContinuousTestDaemonEnqueuer? enqueuer = context.Enqueuer ?? context.Queue;
        if (enqueuer is null)
            return true;
        try
        {
            ContinuousTestRevisionPollResult poll = await context.Poller.PollAsync(
                new ContinuousTestRevisionPollRequest(
                    context.WorkspaceId,
                    context.WorkspaceRoot,
                    context.Projects,
                    enqueuer,
                    EnqueueArmed: context.StartedAt is not null && context.Backoff.CanEnqueue,
                    OnRebuild: rebuilt => DemotePriorGreen(context, rebuilt)),
                cancellationToken).ConfigureAwait(false);
            // Settled means the saved cursor equals the live revision and the poll was healthy;
            // every other answer is activity, which restarts the idle drain's quiet window.
            context.PollSettled = poll.Freshness is not null
                && string.Equals(poll.Reason, SettledPollReason, StringComparison.Ordinal);
            if (!context.PollSettled)
                context.LastActivityAt = _options.Clock();

            string? pauseBefore = context.Unavailable.StuckReason;
            if (poll.Freshness is { } freshness)
            {
                context.StartedAt ??= freshness;
                context.LatestFreshness = freshness;
                context.Queue?.ObserveFreshRevision(context.WorkspaceId, freshness);

                // An unavailable delta is the one answer that is neither a success nor the word
                // "degraded", and it is STICKY: the poller may not absorb an interval whose impact it
                // could not read, so the same unreadable interval comes back every 250 ms. Recording
                // that as a healthy poll left a daemon that looked fine at 4 Hz while automatic runs
                // had silently stopped. A run of them is treated as a degradation instead — of the
                // POLL only, so work accepted at an earlier readable base still drains.
                bool unavailable = string.Equals(poll.Reason, UnavailableDeltaReason, StringComparison.Ordinal);
                bool stuck = unavailable && context.Unavailable.RecordUnavailable(poll.DeltaReason);
                if (!unavailable)
                    context.Unavailable.RecordOther();

                if (string.Equals(poll.Reason, "degraded", StringComparison.Ordinal))
                {
                    context.Backoff.RecordDegraded();
                    context.Watch.RecordError("degraded");
                }
                else if (stuck)
                {
                    context.Backoff.RecordPollDegraded();

                    // Degraded watch health reads as Unknown, which is the honest verdict here: the
                    // daemon cannot say what the unread interval changed, so it cannot stand behind a
                    // green it recorded before that interval.
                    context.Watch.RecordError(
                        string.IsNullOrWhiteSpace(poll.DeltaReason)
                            ? UnavailableDeltaReason
                            : poll.DeltaReason);
                }
                else
                {
                    context.Backoff.RecordHealthy();
                    context.Watch.RecordSuccess(freshness.ToString());
                }
            }
            else
            {
                context.Unavailable.RecordOther();
                context.Backoff.RecordDegraded();
                context.Watch.RecordError(poll.Reason);
            }

            LogPauseTransition(context, pauseBefore);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            context.PollSettled = false;
            context.LastActivityAt = _options.Clock();
            context.Backoff.RecordDegraded();
            context.Watch.RecordError("poll_error");

            // The exception used to be discarded here, so a daemon that degraded on every poll
            // reported only the word "poll_error" and never the reason. Safe inside this
            // last-resort catch because CtDaemonLog.Write never throws for an I/O reason.
            Diagnostic($"ct poll error workspace={context.WorkspaceId} {CtDaemonLog.FailureDetail(exception)}");
        }

        return true;
    }

    /// <summary>
    /// One <c>role:ct</c> line on each auto-run pause EDGE — enter with the reason, clear on recovery —
    /// judged where the tracker is fed, so a persistent pause logs once instead of once per poll. The
    /// status record carries the standing state; the daily log used to carry nothing at all, which is how
    /// a six-minute pause left no trace outside <c>daemon.status.json</c>.
    /// </summary>
    private void LogPauseTransition(ContinuousTestWorkspaceContext context, string? before)
    {
        string? after = context.Unavailable.StuckReason;
        if (before is null && after is not null)
        {
            Diagnostic(
                $"ct auto-runs paused workspace={context.WorkspaceId} reason={PauseReasonOf(after)}");
        }
        else if (before is not null && after is null)
        {
            Diagnostic($"ct auto-runs resumed workspace={context.WorkspaceId}");
        }
    }

    /// <summary>
    /// One context's idle-drain check, run every pass right before the ready scan. Gathers the
    /// tick's observation and consults <see cref="CtIdleDrainPolicy"/>; when the policy fires, one
    /// workspace-scope change per project is minted through
    /// <see cref="ContinuousTestDaemonQueue.EnqueueIdleDrain"/> at the LIVE key the poller last
    /// observed, with no debounce — the quiet window already elapsed — so the same pass's ready
    /// scan executes it under the ordinary user-global execution budget. The stale count comes
    /// from the same aggregate the status projection reads; a store that cannot answer reads as
    /// zero owed, which fails closed.
    /// </summary>
    private void TryScheduleIdleDrain(ContinuousTestWorkspaceContext context, DateTimeOffset now)
    {
        if (context.Queue is not { } queue || context.Store is null || context.Projects.Count == 0)
            return;
        if (context.LatestFreshness is not { } freshness)
            return;

        context.LastIdleDrainAt ??= now;
        var observation = new CtIdleDrainObservation(
            Now: now,
            StaleCount: ReadStaleCount(context, freshness),
            QueueHasPendingWork: queue.HasPendingWork(),
            RunExecuting: _drainInFlight,
            PollSettled: context.PollSettled,
            AutoRunsPaused: context.Unavailable.StuckReason is not null || !context.Backoff.CanEnqueue,
            LastActivityAt: context.LastActivityAt,
            LastDrainAt: context.LastIdleDrainAt);
        if (!_idleDrainPolicy.ShouldDrain(observation))
            return;

        context.LastIdleDrainAt = now;
        foreach (ContinuousTestProjectWorkItem item in ContinuousTestProjectInventory.MaterializeProjectWorkItems(
                     context.Projects, context.WorkspaceRoot))
        {
            queue.EnqueueIdleDrain(new ContinuousTestDaemonChange(
                item.Workspace,
                freshness.Revision.ToString(CultureInfo.InvariantCulture),
                freshness.IndexIdentity,
                WorkspaceScope: true,
                ObservedAt: now,
                Command: item.Project.Command,
                Framework: item.Project.Framework));
        }

        Diagnostic($"ct idle drain scheduled workspace={context.WorkspaceId} "
            + $"revision={freshness.Revision} stale={observation.StaleCount}");
    }

    /// <summary>
    /// The owed-work signal for the idle drain, read from the same status aggregate every other
    /// projection uses. An unreadable <c>ct.db</c> answers zero: nothing is provably owed, so
    /// nothing drains.
    /// </summary>
    private static int ReadStaleCount(ContinuousTestWorkspaceContext context, CtFreshnessKey freshness)
    {
        try
        {
            return context.Store!.AggregateContinuousTestStatuses(context.WorkspaceId, freshness).Stale;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// One attach/detach pass over the family. Runs only from the LIVE loop - the kill switch and
    /// the disabled branch return before the loop starts, so <c>MILLER_CT=off</c> performs zero
    /// registry or filesystem reads here - and skips while the daemon is paused on a held budget.
    /// </summary>
    private void ScanWorktrees()
    {
        if (_adoption is null)
            return;
        if (_publishedState == CtDaemonLifecycleState.Paused)
            return;
        DateTimeOffset now = _options.Clock();
        if (_lastWorktreeScanAt is { } last && now - last < _adoption.ScanInterval)
            return;
        _lastWorktreeScanAt = now;

        // Registration is scan state: the detach pass and the attach pass read the SAME successful
        // registry snapshot. A FAILED discovery ends the pass before either - a transient registry
        // error must never read as "nothing registered" and detach the whole family.
        HashSet<string>? registered = TryReadRegisteredRoots();
        if (registered is null)
            return;

        // Detach pass: a root that disappeared, stopped qualifying, or left the registry
        // (workspace remove) releases its context. A MISSING root is a detach, never an error
        // loop - the registry row may simply be stale.
        foreach ((string key, ContinuousTestWorkspaceContext context) in _adopted.ToArray())
        {
            if (!registered.Contains(key) || !QualifiesForAdoption(context.WorkspaceRoot))
                DetachWorktree(key, context, "detached");
        }

        // AFTER the detach pass, never before it. An owed record is always an ATTACH record, which
        // writes in create mode — so retrying it on a root the detach pass was about to drop would
        // re-mint the control plane of a worktree that had just been removed, which is the very
        // resurrect this change exists to stop. Everything still in _adopted here passed BOTH detach
        // triggers on this same pass: it is registered AND it still qualifies.
        RetryOwedAdoptedStatus();

        foreach (string root in registered)
        {
            if (PathsEqual(root, _workspaceRoot) || _adopted.ContainsKey(root) || _stopDetached.Contains(root))
                continue;
            if (!QualifiesForAdoption(root))
                continue;
            if (AttachWorktree(root) is { } context)
                RequestAdoptedStatus(context, CtDaemonLifecycleState.Running, AdoptedReason());
        }
    }

    /// <summary>
    /// The registration clause's one read, shared by the scan and the routed-run attach so the two
    /// cannot drift. Returns the registered roots normalized to <see cref="RootKey"/> form, or
    /// null when discovery FAILED - the caller must treat null as "cannot read the registry",
    /// never as "nothing registered".
    /// </summary>
    private HashSet<string>? TryReadRegisteredRoots()
    {
        if (_adoption is null)
            return null;
        IReadOnlyList<string> roots;
        try
        {
            roots = _adoption.DiscoverRegisteredRoots();
        }
        catch (Exception exception)
        {
            Diagnostic($"ct worktree discovery error {CtDaemonLog.FailureDetail(exception)}");
            return null;
        }

        var registered = new HashSet<string>(PathKeyComparer);
        foreach (string candidate in roots)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            try
            {
                registered.Add(RootKey(candidate));
            }
            catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
            {
                // One malformed registry row must not fail the whole pass or kill the daemon.
                Diagnostic($"ct worktree discovery skipped malformed root {CtDaemonLog.FailureDetail(exception)}");
            }
        }

        return registered;
    }

    /// <summary>
    /// The adoption predicate, every clause required: the root exists, it is a linked worktree of
    /// THIS daemon's repo, it is opted in (inheritance and tombstone included), and it does not run
    /// its own daemon. Every probe is a read; nothing is created.
    /// </summary>
    private bool QualifiesForAdoption(string root)
    {
        if (!Directory.Exists(root))
            return false;
        GitWorktreeLayout? layout = _resolveLayout(root);
        if (layout is not { IsLinkedWorktree: true, MainCheckoutRoot: { } main })
            return false;
        if (!PathsEqual(Path.GetFullPath(main), _workspaceRoot))
            return false;
        if (!_isOptedIn(root))
            return false;
        return !_hasOwnLiveDaemon(root);
    }

    private ContinuousTestWorkspaceContext? AttachWorktree(string root)
    {
        if (_adoption is null)
            return null;
        ContinuousTestWorkspaceContext? context;
        try
        {
            context = _adoption.CreateContext(root);
        }
        catch (Exception exception)
        {
            Diagnostic($"ct worktree attach error root={root} {CtDaemonLog.FailureDetail(exception)}");
            return null;
        }

        if (context is null)
            return null;
        _adopted[root] = context;
        return context;
    }

    private void DetachWorktree(string key, ContinuousTestWorkspaceContext context, string reason)
    {
        _adopted.Remove(key);
        try
        {
            context.Dispose();
        }
        catch (Exception exception)
        {
            // A detach must never kill the daemon; the remaining contexts still depend on it.
            Diagnostic($"ct worktree detach error root={context.WorkspaceRoot} {CtDaemonLog.FailureDetail(exception)}");
        }

        if (!TryWriteAdoptedStatus(context, CtDaemonLifecycleState.Stopped, reason))
            TryClearAdoptedStatus(context);
    }

    /// <summary>
    /// The fallback when a DETACH record cannot be written: remove the stale record instead.
    ///
    /// <para>A detach is un-retryable by construction — the context is already out of
    /// <see cref="_adopted"/> and disposed, so no later pass can reach it. Without this, one failed
    /// write left the worktree holding an <c>adopted by …</c> record naming a daemon that is still
    /// ALIVE, so the liveness probe cannot contradict it and <c>tests status</c> reports a running
    /// daemon for a worktree nothing watches — the same dishonest reading, from the other side.</para>
    ///
    /// <para>Deleting is the honest repair: an absent record reads as <c>stopped</c>, which is exactly
    /// what a detached worktree is. It also cannot resurrect anything, because it only ever removes.</para>
    /// </summary>
    private void TryClearAdoptedStatus(ContinuousTestWorkspaceContext context)
    {
        try
        {
            File.Delete(CtDaemonProtocol.StatusPath(context.WorkspaceRoot));
        }
        catch (Exception ex)
        {
            Diagnostic(
                $"ct worktree status clear failed root={context.WorkspaceRoot} {CtDaemonLog.FailureDetail(ex)}");
        }
    }

    /// <summary>
    /// A routed <c>run</c> may name a worktree the scan has not attached yet - or one an earlier
    /// stop detached. It is the user asking for that worktree explicitly, so it clears the stop
    /// suppression and attaches on the spot when the root qualifies AND is registered - the
    /// registry (<c>workspace open</c>) is adoption's authorization gate, and a routed run must
    /// not attach a directory the registry has never seen. A failed registry read refuses the
    /// attach: "cannot authorize" is not "not registered".
    /// </summary>
    private ContinuousTestWorkspaceContext? ResolveRoutedRunTarget(string root)
    {
        if (_adopted.TryGetValue(root, out ContinuousTestWorkspaceContext? adopted))
        {
            _stopDetached.Remove(root);
            return adopted;
        }

        if (_adoption is null || !QualifiesForAdoption(root))
            return null;
        HashSet<string>? registered = TryReadRegisteredRoots();
        if (registered is null || !registered.Contains(root))
            return null;
        _stopDetached.Remove(root);
        if (AttachWorktree(root) is not { } context)
            return null;
        RequestAdoptedStatus(context, CtDaemonLifecycleState.Running, AdoptedReason());
        return context;
    }

    /// <summary>
    /// The per-worktree status record: state plus a reason NAMING the serving daemon's root, so a
    /// foreground <c>tests status</c> on the worktree reads an honest answer from its own
    /// <c>.miller/ct/</c>. Written on transitions only (attach, detach, shutdown), guarded like
    /// every other control-plane write, and skipped entirely when the root is gone.
    /// </summary>
    private bool TryWriteAdoptedStatus(
        ContinuousTestWorkspaceContext context,
        CtDaemonLifecycleState state,
        string reason)
    {
        // The guard means "this record is already ON DISK", which is only true because the fields
        // below are set after the write returns.
        if (context.PublishedState == state
            && string.Equals(context.PublishedReason, reason, StringComparison.Ordinal))
        {
            return true;
        }

        if (!Directory.Exists(context.WorkspaceRoot))
            return false;

        // An ATTACH record legitimately creates a newly adopted worktree's control plane. A DETACH
        // record says nothing serves this root any more, so it may only replace an existing file —
        // creating one would re-mint the tree the detach is reacting to.
        CtDaemonWriteMode mode = state == CtDaemonLifecycleState.Running
            ? CtDaemonWriteMode.CreateIfMissing
            : CtDaemonWriteMode.ReplaceExistingOnly;
        string? pauseReason = state == CtDaemonLifecycleState.Stopped
            ? null
            : PauseReasonOf(context.Unavailable.StuckReason);
        var record = new CtDaemonStatusRecord(
            state,
            reason,
            _leaseIdentity,
            _options.Clock(),
            AutoRunsPaused: pauseReason is not null,
            PauseReason: pauseReason);
        try
        {
            if (_options.AdoptedStatusWriter is { } writer)
            {
                if (!writer(context.WorkspaceRoot, record, mode))
                    return false;
            }
            else
            {
                CtDaemonLease.WriteStatus(context.WorkspaceRoot, record, mode);
            }
        }
        // Wider than the IOException/UnauthorizedAccessException pair this used to catch. ScanWorktrees
        // runs in the main loop with no surrounding try, so a PathTooLongException or a JsonException
        // from one malformed root ended the loop while the lease still held the daemon lock. The
        // Diagnostic line is not optional: without it this failure left no trace anywhere.
        catch (Exception ex)
        {
            Diagnostic(
                $"ct worktree status write failed root={context.WorkspaceRoot} {CtDaemonLog.FailureDetail(ex)}");
            return false;
        }

        context.PublishedState = state;
        context.PublishedReason = reason;
        return true;
    }

    /// <summary>
    /// The attach half of <see cref="TryWriteAdoptedStatus"/>: a record that does NOT land is
    /// remembered as owed and retried on later scan passes.
    ///
    /// <para>Only an attach is retryable. A detach record is un-retryable by construction —
    /// <see cref="DetachWorktree"/> removes the context from <see cref="_adopted"/> before it writes,
    /// so the retry pass can never reach it again.</para>
    ///
    /// <para>Why this matters: the record names the daemon that serves the worktree, a one-shot
    /// <c>tests status</c> probes that identity for liveness, and the attach loop skips a root already
    /// in <see cref="_adopted"/>. So one lost write left a DEAD predecessor's record in place and a
    /// live family daemon read as "daemon gone" from that worktree, permanently.</para>
    /// </summary>
    private void RequestAdoptedStatus(
        ContinuousTestWorkspaceContext context,
        CtDaemonLifecycleState state,
        string reason)
    {
        if (TryWriteAdoptedStatus(context, state, reason))
        {
            context.OwedState = null;
            context.OwedReason = null;
            context.OwedAttempts = 0;
            return;
        }

        context.OwedState = state;
        context.OwedReason = reason;
    }

    /// <summary>
    /// Retries the attach records earlier passes failed to write, bounded by
    /// <see cref="MaxAdoptedStatusAttempts"/> so an unwritable root cannot spin forever.
    ///
    /// <para>Runs after the scan throttle, so it inherits the scan interval rather than the much
    /// faster poll interval, and after the DETACH pass, so it can only write for a worktree that is
    /// still registered and still qualifies.</para>
    /// </summary>
    private void RetryOwedAdoptedStatus()
    {
        foreach (ContinuousTestWorkspaceContext context in _adopted.Values)
        {
            if (context.OwedState is not { } state || context.OwedReason is not { } reason)
                continue;
            if (context.OwedAttempts >= MaxAdoptedStatusAttempts)
                continue;

            context.OwedAttempts++;
            if (!TryWriteAdoptedStatus(context, state, reason))
                continue;

            context.OwedState = null;
            context.OwedReason = null;
            context.OwedAttempts = 0;
        }
    }

    private string AdoptedReason() => $"adopted by {_workspaceRoot}";

    /// <summary>The shutdown tail's half of adoption: every context released, every record honest.</summary>
    private void ReleaseAdoptedContexts()
    {
        foreach ((string key, ContinuousTestWorkspaceContext context) in _adopted.ToArray())
            DetachWorktree(key, context, "stopped");
    }

    /// <summary>
    /// The canonical form every root takes before it becomes an <see cref="_adopted"/> key, a
    /// <see cref="_stopDetached"/> entry, or a registered-set member, so the three always compare
    /// under the same <see cref="PathKeyComparer"/> semantics as <see cref="PathsEqual"/>.
    /// </summary>
    private static string RootKey(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathKeyComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// Reports a failure the loop would otherwise discard. Called only from the LIVE loop, never from a
    /// disabled branch, so <c>MILLER_CT=off</c> stays zero-work even when a sink is supplied.
    /// </summary>
    private void Diagnostic(string message) => _options.Diagnostic?.Invoke(message);

    private void Publish(ContinuousTestDaemonSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        _options.StatusSink?.Invoke(snapshot);
    }
}
