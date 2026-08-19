using System.Globalization;
using Miller.Indexing;

namespace Miller.Testing;

public sealed record ContinuousTestDaemonSnapshot(
    CtDaemonLifecycleState State,
    string Reason,
    ContinuousTestVerdict Verdict,
    CtFreshnessKey? Selected,
    int StaleCount,
    int SelectedCount,
    bool Enabled,
    bool Executing);

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

    public IContinuousTestDaemonEnqueuer? Enqueuer { get; init; }
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
    private CtFreshnessKey? _startedAt;
    private DateTimeOffset _runStartedAtUtc;

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
            Executing: false);
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

        lease?.WriteStatus(CtDaemonLifecycleState.Running, "status-only");
        Publish(Evaluate("status-only", CtDaemonLifecycleState.Running, executing: false));

        IContinuousTestDaemonEnqueuer enqueuer = _options.Enqueuer ?? _queue
            ?? throw new InvalidOperationException("CT daemon host requires a queue or enqueuer");

        Task heartbeat = lease is null
            ? Task.CompletedTask
            : PulseHeartbeatAsync(lease, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            ProcessCommands(lease, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
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
                catch (Exception)
                {
                    _backoff.RecordDegraded();
                    _watch.RecordError("poll_error");
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
                    Publish(Evaluate("execution budget held", CtDaemonLifecycleState.Paused, executing: false));
                    lease?.WriteStatus(CtDaemonLifecycleState.Paused, "execution budget held");
                }
                else
                {
                    executing = true;
                    lease?.WriteStatus(CtDaemonLifecycleState.Running, "executing");
                    try
                    {
                        await _queue.DrainReadyAsync(now, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
            else
            {
                string reason = _startedAt is null ? "status-only" : "idle";
                lease?.WriteStatus(CtDaemonLifecycleState.Running, reason);
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

        lease?.WriteStatus(CtDaemonLifecycleState.Stopped, "stopped");
        Publish(Evaluate("stopped", CtDaemonLifecycleState.Stopped, executing: false));
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

    private void ProcessCommands(CtDaemonLease? lease, CancellationToken cancellationToken)
    {
        string commandDir = CtDaemonProtocol.CommandDirectory(_workspaceRoot);
        if (!Directory.Exists(commandDir))
            return;
        foreach (string path in Directory.EnumerateFiles(commandDir, "*.request.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = Path.GetFileName(path)[..^".request.json".Length];
            if (CtCommandChannel.TryReadAck(_workspaceRoot, id) is not null)
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
                    CtCommandChannel.WriteAck(
                        _workspaceRoot,
                        new CtDaemonCommandAck(id, CtDaemonCommandState.Acknowledged, DateTimeOffset.UtcNow, "stale-stop-ignored"));
                    continue;
                }

                CtCommandChannel.WriteAck(
                    _workspaceRoot,
                    new CtDaemonCommandAck(id, CtDaemonCommandState.Acknowledged, DateTimeOffset.UtcNow, "stopping"));
                lease?.WriteStatus(CtDaemonLifecycleState.Stopped, "stop");
                throw new OperationCanceledException();
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

            CtCommandChannel.WriteAck(
                _workspaceRoot,
                new CtDaemonCommandAck(id, CtDaemonCommandState.Acknowledged, DateTimeOffset.UtcNow, "run"));
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
        return new ContinuousTestDaemonSnapshot(
            state,
            reason,
            verdict,
            selected,
            stale,
            statuses.Count,
            Enabled: true,
            Executing: executing);
    }

    private ContinuousTestDaemonSnapshot DisabledSnapshot() =>
        new(CtDaemonLifecycleState.Stopped, "disabled", ContinuousTestVerdict.Unknown, null, 0, 0, false, false);

    private void Publish(ContinuousTestDaemonSnapshot snapshot)
    {
        LastSnapshot = snapshot;
        _options.StatusSink?.Invoke(snapshot);
    }
}
