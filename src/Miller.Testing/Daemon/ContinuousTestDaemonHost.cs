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
}

/// <summary>
/// Long-running CT daemon loop. Task 12 wires this behind <c>tests serve</c>.
/// Status reads and kill-switch off construct no CT machinery.
/// </summary>
public sealed class ContinuousTestDaemonHost
{
    private readonly string _workspaceRoot;
    private readonly ContinuousTestDaemonHostOptions _options;
    private readonly ContinuousTestStore? _store;
    private readonly ContinuousTestDaemonQueue? _queue;
    private readonly ContinuousTestRevisionPoller? _poller;
    private readonly CtExecutionBudget _budget;
    private readonly CtWatchHealth _watch = new();
    private readonly CtDegradationBackoff _backoff = new();
    private readonly IReadOnlyList<ContinuousTestProject> _projects;
    private readonly string _workspaceId;
    private readonly CtRunActivityCell? _runActivity;

    /// <summary>
    /// Command ids this loop has already handled. The ack FILE is the durable record, but the write
    /// of that file is now guarded, so a workspace whose ack directory cannot be written would
    /// otherwise replay every request on every poll — and a replayed <c>run</c> re-enqueues and
    /// re-executes the suite forever. Pruned each pass to the requests still on disk, so it stays
    /// bounded by the command directory.
    /// </summary>
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);
    private CtFreshnessKey? _startedAt;
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
        _store = _options.Store;
        _queue = _options.Queue;
        _poller = _options.Poller;
        _budget = _options.Budget ?? CtExecutionBudget.FromEnvironment(MillerHome.ResolveMillerDirectory());
        _projects = _options.Projects ?? [];
        _runActivity = _options.RunActivity;
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

        TryWriteStatus(lease, CtDaemonLifecycleState.Running, "status-only");
        Publish(Evaluate("status-only", CtDaemonLifecycleState.Running, executing: false));

        IContinuousTestDaemonEnqueuer enqueuer = _options.Enqueuer ?? _queue
            ?? throw new InvalidOperationException("CT daemon host requires a queue or enqueuer");

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

            if (_poller is not null && _backoff.CanPoll)
            {
                try
                {
                    ContinuousTestRevisionPollResult poll = await _poller.PollAsync(
                        new ContinuousTestRevisionPollRequest(
                            _workspaceId,
                            _workspaceRoot,
                            _projects,
                            enqueuer,
                            EnqueueArmed: _startedAt is not null && _backoff.CanEnqueue,
                            OnRebuild: DemotePriorGreen),
                        cancellationToken).ConfigureAwait(false);
                    if (poll.Freshness is { } freshness)
                    {
                        _startedAt ??= freshness;
                        _queue?.ObserveFreshRevision(_workspaceId, freshness);
                        if (string.Equals(poll.Reason, "degraded", StringComparison.Ordinal))
                        {
                            _backoff.RecordDegraded();
                            _watch.RecordError("degraded");
                        }
                        else
                        {
                            _backoff.RecordHealthy();
                            _watch.RecordSuccess(freshness.ToString());
                        }
                    }
                    else
                    {
                        _backoff.RecordDegraded();
                        _watch.RecordError(poll.Reason);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _backoff.RecordDegraded();
                    _watch.RecordError("poll_error");

                    // The exception used to be discarded here, so a daemon that degraded on every poll
                    // reported only the word "poll_error" and never the reason. Safe inside this
                    // last-resort catch because CtDaemonLog.Write never throws for an I/O reason.
                    Diagnostic($"ct poll error workspace={_workspaceId} {CtDaemonLog.FailureDetail(exception)}");
                }
            }

            DateTimeOffset now = _options.Clock();
            bool executing = false;
            if (_queue is not null && _queue.HasReadyWork(now) && _backoff.CanEnqueue)
            {
                using CtExecutionBudgetLease? budget = _budget.TryAcquire(
                    new CtExecutionBudgetRequest(_workspaceRoot, "run"),
                    TimeSpan.Zero,
                    cancellationToken);
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
                        await _queue.DrainReadyAsync(now, cancellationToken).ConfigureAwait(false);
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
                string reason = _startedAt is null ? "status-only" : "idle";
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
            if (request.Kind == CtDaemonCommandKind.Stop)
            {
                // A stop request targets the daemon that was alive when it was written. One left
                // unacknowledged by a dead predecessor must not kill this instance at startup.
                if (request.RequestedAtUtc < _runStartedAtUtc)
                {
                    TryWriteAck(id, "stale-stop-ignored");
                    continue;
                }

                TryWriteAck(id, "stopping");
                TryWriteStatus(lease, CtDaemonLifecycleState.Stopped, "stop");
                return true;
            }

            if (request.Kind == CtDaemonCommandKind.Run && _queue is not null && _projects.Count > 0)
            {
                foreach (ContinuousTestProjectWorkItem item in ContinuousTestProjectInventory.MaterializeProjectWorkItems(
                             _projects, _workspaceRoot))
                {
                    CtFreshnessKey freshness = request.Freshness
                        ?? _startedAt
                        ?? new CtFreshnessKey("unspecified", 0);
                    _queue.EnqueueExplicit(new ContinuousTestDaemonChange(
                        item.Workspace,
                        freshness.Revision.ToString(CultureInfo.InvariantCulture),
                        freshness.IndexIdentity,
                        WorkspaceScope: true,
                        ObservedAt: DateTimeOffset.UtcNow,
                        Command: item.Project.Command,
                        Framework: item.Project.Framework));
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
    private void TryWriteAck(string commandId, string reason)
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
                    CtDaemonCommandState.Acknowledged,
                    DateTimeOffset.UtcNow,
                    reason));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void DemotePriorGreen(CtFreshnessKey rebuilt)
    {
        if (_store is null)
            return;
        string[] ids = _store.ListTestCases(_workspaceId).Select(row => row.Id).ToArray();
        if (ids.Length == 0)
            return;
        _store.MarkContinuousTestsStale(_workspaceId, ids, rebuilt);
    }

    private ContinuousTestDaemonSnapshot Evaluate(string reason, CtDaemonLifecycleState state, bool executing)
    {
        IReadOnlyList<ContinuousTestStatus> statuses = _store?.ListContinuousTestStatuses(_workspaceId) ?? [];
        CtFreshnessKey? selected = _startedAt;
        ContinuousTestVerdict verdict = selected is { } key
            ? ContinuousTestFreshness.Evaluate(statuses, key, _watch.IsHealthy)
            : ContinuousTestVerdict.Unknown;
        int stale = statuses.Count(row => row.State == ContinuousTestState.Stale);
        (CtDaemonActivity activity, CtDaemonRunProgress? run) =
            _runActivity?.Read() ?? (CtDaemonActivity.Idle, null);
        return new ContinuousTestDaemonSnapshot(
            state,
            reason,
            verdict,
            selected,
            stale,
            statuses.Count,
            Enabled: true,
            Executing: executing,
            Activity: activity,
            Run: run);
    }

    private ContinuousTestDaemonSnapshot DisabledSnapshot() =>
        new(CtDaemonLifecycleState.Stopped, "disabled", ContinuousTestVerdict.Unknown, null, 0, 0, false, false);

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
