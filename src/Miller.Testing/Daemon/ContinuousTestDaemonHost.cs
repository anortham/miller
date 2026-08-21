using System.Globalization;
using Miller.Indexing;

namespace Miller.Testing;

/// <summary>
/// <paramref name="Activity"/> and <paramref name="Run"/> are trailing optionals so every existing positional
/// construction keeps compiling. <paramref name="Executing"/> stays because callers read it; it answers "is a
/// drain in flight", while <paramref name="Run"/> names the project that drain is on.
/// </summary>
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
    CtDaemonRunProgress? Run = null);

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

    public IContinuousTestDaemonEnqueuer? Enqueuer { get; init; }

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

    internal CtFreshnessKey? StartedAt;

    internal CtFreshnessKey? LatestFreshness;

    internal CtDaemonLifecycleState? PublishedState;

    internal string? PublishedReason;

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
    /// <summary>Registered workspace roots, from the registry's non-creating read path.</summary>
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
    private DateTimeOffset _runStartedAtUtc;

    // Read by the pulse task while the main loop writes them. Volatile rather than locked: a republish that
    // reads a state one poll old costs nothing, where a lock would put the pulse behind the main loop.
    private volatile CtDaemonLifecycleState _publishedState = CtDaemonLifecycleState.Running;
    private volatile string _publishedReason = "starting";

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
        _adoption = _options.WorktreeAdoption;
        _hasOwnLiveDaemon = _adoption?.HasOwnLiveDaemon
            ?? (root => CtDaemonLease.TryReadLive(root) is not null);
        _isOptedIn = _adoption?.IsOptedIn ?? (root => ContinuousTestPolicy.IsWorkspaceOptedIn(root));
        _resolveLayout = _adoption?.ResolveLayout ?? GitWorktreeLayout.Resolve;
        _adopted = new Dictionary<string, ContinuousTestWorkspaceContext>(PathKeyComparer);
        _stopDetached = new HashSet<string>(PathKeyComparer);
    }

    public ContinuousTestDaemonSnapshot? LastSnapshot { get; private set; }

    public static ContinuousTestDaemonSnapshot ReadStatus(string workspaceRoot)
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
            Run: record?.Run);
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
        // blocking the exit for a whole heartbeat interval or leaving the task unobserved.
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeat = lease is null
            ? Task.CompletedTask
            : PulseHeartbeatAsync(lease, heartbeatCancellation.Token);

        while (!cancellationToken.IsCancellationRequested)
        {
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
                    _runActivity?.BeginDrain();
                    TryWriteStatus(lease, CtDaemonLifecycleState.Running, "executing");
                    try
                    {
                        // Every ready context drains under the ONE budget lease taken above: N family
                        // worktrees never mean N concurrent suites.
                        foreach (ContinuousTestWorkspaceContext context in ready)
                            await context.Queue!.DrainReadyAsync(now, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    finally
                    {
                        // One drain runs every ready project. Cleared only when the whole drain returns, so a
                        // waiting caller cannot slip through the gap between two of its projects.
                        _runActivity?.EndDrain();
                    }
                }
            }
            else
            {
                _runActivity?.EnterIdle();
                string reason = _primary.StartedAt is null ? "status-only" : "idle";
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
        // and abandoning it would leave both an unobserved exception and a heartbeat that can still
        // land after the lease below is released.
        await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
        await heartbeat.ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps <c>daemon.heartbeat.json</c> fresh for the life of the loop, including while a long
    /// drain blocks the main loop. Never throws: liveness is carried by the OS lock on
    /// <c>daemon-v1.lock</c>; the heartbeat file is an observable freshness signal only.
    /// </summary>
    private async Task PulseHeartbeatAsync(CtDaemonLease lease, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _options.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                lease.Heartbeat();

                // Republish the status too. The main loop is BLOCKED for the whole drain, so without this the
                // status file froze at "executing" until the run ended - which is exactly how a 12-minute run
                // and a wedged one looked identical. The lifecycle state and reason are the last ones the main
                // loop chose; only the activity and the child's liveness are refreshed here.
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
    /// reason <see cref="PulseHeartbeatAsync"/> never throws.
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
        // must never invent a state of its own.
        _publishedState = state;
        _publishedReason = reason;
        PublishStatus(lease, state, reason);
    }

    /// <summary>
    /// Writes one status record, attaching whatever the activity cell currently reports. Called by the main
    /// loop on every poll and by the pulse task while a drain blocks that loop.
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

            (CtDaemonActivity activity, CtDaemonRunProgress? run) =
                _runActivity?.Read() ?? (CtDaemonActivity.Idle, null);
            lease.WriteStatus(state, reason, activity, run);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Drains the file command channel. Returns <c>true</c> when a live stop request asked this
    /// daemon to exit.
    ///
    /// A stop used to leave through <c>throw new OperationCanceledException()</c>. Production never
    /// cancels the loop token, so that throw was the daemon's only exit, and it jumped over the whole
    /// shutdown tail: the final <c>Stopped</c> status, the final snapshot, and the heartbeat await
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
        foreach (string path in Directory.EnumerateFiles(commandDir, "*.request.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = Path.GetFileName(path)[..^".request.json".Length];
            seen.Add(id);
            if (_acknowledged.Contains(id) || CtCommandChannel.TryReadAck(_workspaceRoot, id) is not null)
                continue;
            CtDaemonCommandRequest? request = CtCommandChannel.TryReadRequest(_workspaceRoot, id);
            if (request is null)
                continue;
            string? targetRoot = string.IsNullOrWhiteSpace(request.WorkspaceRoot)
                ? null
                : Path.GetFullPath(request.WorkspaceRoot);
            bool targetsPrimary = targetRoot is null || PathsEqual(targetRoot, _workspaceRoot);
            if (request.Kind == CtDaemonCommandKind.Stop)
            {
                // A stop request targets the daemon that was alive when it was written. One left
                // unacknowledged by a dead predecessor must not kill this instance at startup.
                if (request.RequestedAtUtc < _runStartedAtUtc)
                {
                    TryWriteAck(id, "stale-stop-ignored");
                    continue;
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

                    continue;
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
                    continue;
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
                        continue;
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
        }

        _acknowledged.IntersectWith(seen);
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
        IReadOnlyList<ContinuousTestStatus> statuses = store?.ListContinuousTestStatuses(_workspaceId) ?? [];

        // Judge at the LATEST observed cursor — the same live key foreground status judges at —
        // and through the SAME projection, with the per-case watermarks threaded in, so the daemon
        // snapshot and `tests status` cannot disagree about the identical store state.
        CtFreshnessKey? selected = _primary.LatestFreshness ?? _primary.StartedAt;
        IReadOnlyDictionary<string, CtFreshnessKey>? watermarks = selected is { } key && store is not null
            ? store.ListContinuousTestFreshWatermarks(_workspaceId, key.IndexIdentity)
            : null;
        ContinuousTestProjectedStatus projected = ContinuousTestStatusProjection.Project(
            selected,
            statuses,
            watermarks,
            watchHealthy: _primary.Watch.IsHealthy);
        (CtDaemonActivity activity, CtDaemonRunProgress? run) =
            _runActivity?.Read() ?? (CtDaemonActivity.Idle, null);
        return new ContinuousTestDaemonSnapshot(
            state,
            reason,
            projected.Verdict,
            selected,
            projected.StaleCount,
            statuses.Count,
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
            if (poll.Freshness is { } freshness)
            {
                context.StartedAt ??= freshness;
                context.LatestFreshness = freshness;
                context.Queue?.ObserveFreshRevision(context.WorkspaceId, freshness);
                if (string.Equals(poll.Reason, "degraded", StringComparison.Ordinal))
                {
                    context.Backoff.RecordDegraded();
                    context.Watch.RecordError("degraded");
                }
                else
                {
                    context.Backoff.RecordHealthy();
                    context.Watch.RecordSuccess(freshness.ToString());
                }
            }
            else
            {
                context.Backoff.RecordDegraded();
                context.Watch.RecordError(poll.Reason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
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

        // Detach pass: a root that disappeared or stopped qualifying releases its context. A
        // MISSING root is a detach, never an error loop - the registry row may simply be stale.
        foreach ((string key, ContinuousTestWorkspaceContext context) in _adopted.ToArray())
        {
            if (!QualifiesForAdoption(context.WorkspaceRoot))
                DetachWorktree(key, context, "detached");
        }

        IReadOnlyList<string> roots;
        try
        {
            roots = _adoption.DiscoverRegisteredRoots();
        }
        catch (Exception exception)
        {
            Diagnostic($"ct worktree discovery error {CtDaemonLog.FailureDetail(exception)}");
            return;
        }

        foreach (string candidate in roots)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            string root = Path.GetFullPath(candidate);
            if (PathsEqual(root, _workspaceRoot) || _adopted.ContainsKey(root) || _stopDetached.Contains(root))
                continue;
            if (!QualifiesForAdoption(root))
                continue;
            if (AttachWorktree(root) is { } context)
                TryWriteAdoptedStatus(context, CtDaemonLifecycleState.Running, AdoptedReason());
        }
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

        TryWriteAdoptedStatus(context, CtDaemonLifecycleState.Stopped, reason);
    }

    /// <summary>
    /// A routed <c>run</c> may name a worktree the scan has not attached yet - or one an earlier
    /// stop detached. It is the user asking for that worktree explicitly, so it clears the stop
    /// suppression and attaches on the spot when the root qualifies.
    /// </summary>
    private ContinuousTestWorkspaceContext? ResolveRoutedRunTarget(string root)
    {
        _stopDetached.Remove(root);
        if (_adopted.TryGetValue(root, out ContinuousTestWorkspaceContext? adopted))
            return adopted;
        if (_adoption is null || !QualifiesForAdoption(root))
            return null;
        if (AttachWorktree(root) is not { } context)
            return null;
        TryWriteAdoptedStatus(context, CtDaemonLifecycleState.Running, AdoptedReason());
        return context;
    }

    /// <summary>
    /// The per-worktree status record: state plus a reason NAMING the serving daemon's root, so a
    /// foreground <c>tests status</c> on the worktree reads an honest answer from its own
    /// <c>.miller/ct/</c>. Written on transitions only (attach, detach, shutdown), guarded like
    /// every other control-plane write, and skipped entirely when the root is gone.
    /// </summary>
    private void TryWriteAdoptedStatus(
        ContinuousTestWorkspaceContext context,
        CtDaemonLifecycleState state,
        string reason)
    {
        if (context.PublishedState == state
            && string.Equals(context.PublishedReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        context.PublishedState = state;
        context.PublishedReason = reason;
        try
        {
            if (!Directory.Exists(context.WorkspaceRoot))
                return;
            CtDaemonLease.WriteStatus(
                context.WorkspaceRoot,
                new CtDaemonStatusRecord(state, reason, _leaseIdentity, _options.Clock()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string AdoptedReason() => $"adopted by {_workspaceRoot}";

    /// <summary>The shutdown tail's half of adoption: every context released, every record honest.</summary>
    private void ReleaseAdoptedContexts()
    {
        foreach ((string key, ContinuousTestWorkspaceContext context) in _adopted.ToArray())
            DetachWorktree(key, context, "stopped");
    }

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
