using System.Collections.Concurrent;

namespace Miller.Server.Workspaces;

public enum BackgroundRefreshActivityState
{
    Queued,
    Running,
    Finished,
    Failed,
    Unknown,
}

public sealed record BackgroundRefreshOperationSnapshot(
    string WorkspaceId,
    string? ScheduledGeneration,
    long? ScheduledRevision,
    BackgroundRefreshActivityState State,
    DateTimeOffset ObservedAtUtc,
    WorkspaceRefreshResult? Result = null,
    string? FailureMessage = null);

/// <summary>
/// The PROCESS-WIDE coalescing guard behind serve-then-refresh cross-workspace reads
/// (<see cref="WorkspaceRefreshMode.Background"/>).
///
/// <para>The blocking arm throttled itself because every caller queued behind the same scan; a fire-and-forget arm
/// has no such queue, so this gate IS the throttle. Ten cross-workspace reads must start ONE refresh, not ten.</para>
///
/// <para>It is a SEPARATE singleton, not a field on <see cref="WorkspaceIndexProvider"/>, because that provider is
/// registered <c>AddTransient</c> for the concrete type and for all seven provider interfaces
/// (<c>MillerServiceRegistration.AddMillerServices</c>). Every tool call therefore builds fresh provider instances —
/// <c>SearchTool</c> alone injects two of them — so an instance field would coalesce nothing in production while a
/// single-instance test looked green.</para>
///
/// <para>A refresh that has just FINISHED also holds the gate for <see cref="DefaultCooldown"/>. One tool call
/// resolves several read contexts in a row (SearchTool up to three, ContextTool two); with the real thread pool an
/// early refresh can complete between two of them and re-arm the guard, so the same call would start a second scan.
/// The cooldown covers that window. It never touches <see cref="WorkspaceRefreshMode.Blocking"/>: an explicit
/// <c>ensure_fresh=true</c> always refreshes.</para>
/// </summary>
public sealed class BackgroundRefreshGate
{
    /// <summary>
    /// How long a just-finished refresh keeps holding the gate. Long enough to cover one tool call's several
    /// resolves, short enough that a follow-up read still picks up a real change promptly.
    /// </summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, WorkspaceGateState> _state = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMilliseconds;
    private readonly long _cooldownMilliseconds;

    public BackgroundRefreshGate()
        : this(DefaultCooldown)
    {
    }

    /// <param name="cooldown">How long a completed refresh keeps holding the gate.</param>
    /// <param name="nowMilliseconds">
    /// A MONOTONIC millisecond source; defaults to <see cref="Environment.TickCount64"/>. Never the wall clock — a
    /// clock correction must not open or extend the cooldown.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cooldown"/> is negative.</exception>
    internal BackgroundRefreshGate(TimeSpan cooldown, Func<long>? nowMilliseconds = null)
    {
        if (cooldown < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cooldown), cooldown, "Cooldown must be non-negative.");
        _cooldownMilliseconds = (long)cooldown.TotalMilliseconds;
        _nowMilliseconds = nowMilliseconds ?? (static () => Environment.TickCount64);
    }

    /// <summary>
    /// Claim the right to start a background refresh for this workspace. False means one is already running, or one
    /// finished inside the cooldown — either way the caller starts nothing and serves the pinned view.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceId"/> is null, empty, or whitespace.</exception>
    public bool TryEnter(string workspaceId) =>
        TryEnter(workspaceId, scheduledGeneration: null, scheduledRevision: null);

    /// <summary>
    /// Claim the right to start a background refresh for this workspace with scheduled revision/generation metadata.
    /// </summary>
    public bool TryEnter(string workspaceId, string? scheduledGeneration, long? scheduledRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        long now = _nowMilliseconds();
        WorkspaceGateState entry = _state.GetOrAdd(workspaceId, static id => new WorkspaceGateState(id));
        lock (entry)
        {
            if (entry.InFlight || (entry.LastFinishedMilliseconds.HasValue && now - entry.LastFinishedMilliseconds.Value < _cooldownMilliseconds))
            {
                return false;
            }

            entry.InFlight = true;
            entry.Snapshot = new BackgroundRefreshOperationSnapshot(
                workspaceId,
                scheduledGeneration,
                scheduledRevision,
                BackgroundRefreshActivityState.Queued,
                DateTimeOffset.UtcNow);
            return true;
        }
    }

    /// <summary>
    /// Transition the operation state to running once the background worker begins execution.
    /// </summary>
    public void RecordRunning(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (_state.TryGetValue(workspaceId, out WorkspaceGateState? entry))
        {
            lock (entry)
            {
                if (entry.InFlight)
                {
                    entry.Snapshot = entry.Snapshot with
                    {
                        State = BackgroundRefreshActivityState.Running,
                        ObservedAtUtc = DateTimeOffset.UtcNow,
                    };
                }
            }
        }
    }

    /// <summary>
    /// Record successful completion of a background refresh and retain its result evidence.
    /// </summary>
    public void RecordFinished(string workspaceId, WorkspaceRefreshResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        long now = _nowMilliseconds();
        WorkspaceGateState entry = _state.GetOrAdd(workspaceId, static id => new WorkspaceGateState(id));
        lock (entry)
        {
            entry.InFlight = false;
            entry.LastFinishedMilliseconds = now;
            entry.Snapshot = entry.Snapshot with
            {
                State = BackgroundRefreshActivityState.Finished,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Result = result,
                FailureMessage = null,
            };
        }
    }

    /// <summary>
    /// Record failure of a background refresh and retain its failure detail for subsequent reads.
    /// </summary>
    public void RecordFailed(string workspaceId, string failureMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        long now = _nowMilliseconds();
        WorkspaceGateState entry = _state.GetOrAdd(workspaceId, static id => new WorkspaceGateState(id));
        lock (entry)
        {
            entry.InFlight = false;
            entry.LastFinishedMilliseconds = now;
            entry.Snapshot = entry.Snapshot with
            {
                State = BackgroundRefreshActivityState.Failed,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                FailureMessage = failureMessage,
            };
        }
    }

    /// <summary>
    /// Report that the refresh finished — successfully or not — and start its cooldown. A failed refresh gets the
    /// same cooldown as a successful one: re-running a scan that just failed is the worse of the two mistakes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceId"/> is null, empty, or whitespace.</exception>
    public void Release(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        long now = _nowMilliseconds();
        WorkspaceGateState entry = _state.GetOrAdd(workspaceId, static id => new WorkspaceGateState(id));
        lock (entry)
        {
            entry.InFlight = false;
            entry.LastFinishedMilliseconds = now;
            if (entry.Snapshot.State is BackgroundRefreshActivityState.Queued or BackgroundRefreshActivityState.Running)
            {
                entry.Snapshot = entry.Snapshot with
                {
                    State = BackgroundRefreshActivityState.Finished,
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                };
            }
        }
    }

    /// <summary>
    /// Return the bounded operation snapshot for this workspace. Returns unknown state if unobserved.
    /// </summary>
    public BackgroundRefreshOperationSnapshot GetSnapshot(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (_state.TryGetValue(workspaceId, out WorkspaceGateState? entry))
        {
            lock (entry)
            {
                return entry.Snapshot;
            }
        }

        return new BackgroundRefreshOperationSnapshot(
            workspaceId,
            ScheduledGeneration: null,
            ScheduledRevision: null,
            BackgroundRefreshActivityState.Unknown,
            DateTimeOffset.UtcNow);
    }

    private sealed class WorkspaceGateState
    {
        public WorkspaceGateState(string workspaceId)
        {
            WorkspaceId = workspaceId;
            Snapshot = new BackgroundRefreshOperationSnapshot(
                workspaceId,
                ScheduledGeneration: null,
                ScheduledRevision: null,
                BackgroundRefreshActivityState.Unknown,
                DateTimeOffset.UtcNow);
        }

        public string WorkspaceId { get; }
        public bool InFlight { get; set; }
        public long? LastFinishedMilliseconds { get; set; }
        public BackgroundRefreshOperationSnapshot Snapshot { get; set; }
    }
}

