namespace Miller.Indexing;

/// <summary>
/// The <see cref="ScanGovernorSnapshot.State"/> vocabulary. There is no idle or disabled member: an idle
/// workspace, a disabled governor, and an unreadable owner record all produce a NULL snapshot, which is what the
/// renderers key their "emit nothing" rule on.
/// </summary>
public static class ScanGovernorStates
{
    /// <summary>This process is queued behind another scan.</summary>
    public const string Waiting = "waiting";

    /// <summary>This process holds admission and is scanning.</summary>
    public const string Holding = "holding";

    /// <summary>Another process holds admission, read from the advisory owner record.</summary>
    public const string HoldingElsewhere = "holding_elsewhere";
}

/// <summary>
/// One workspace's scan-admission position, as <c>workspace status</c>/<c>health</c> report it.
/// </summary>
/// <param name="State">One of <see cref="ScanGovernorStates"/>.</param>
/// <param name="Reason">The short reason token the request carried, when known.</param>
/// <param name="SinceUtc">When the reported state began.</param>
/// <param name="HolderPid">The pid recorded by the current holder, when this process is not it.</param>
/// <param name="HolderWorkspaceRoot">The workspace root the recorded holder is scanning.</param>
public sealed record ScanGovernorSnapshot(
    string State,
    string? Reason,
    DateTimeOffset? SinceUtc,
    int? HolderPid,
    string? HolderWorkspaceRoot);

/// <summary>
/// This process's own scan-admission position, keyed BY WORKSPACE ROOT so status/health can report it without a
/// new agent-facing tool. Per-workspace rather than a single slot because one process legitimately waits on
/// workspace A while holding for workspace B, and a single slot would report the wrong one.
/// </summary>
public sealed class ScanGovernorState
{
    /// <summary>The process-wide instance the tools and the governed scan paths share.</summary>
    public static ScanGovernorState Shared { get; } = new();

    // Keyed by workspace root, but each root holds a LIST keyed by admission id: the debounce drain and an
    // on-demand TryScanAsLeader can request admission for the same root at once, and a single-entry map let the
    // waiter overwrite the holder's entry and its refusal delete the holder's outright — reporting the
    // machine-wide holder as queued behind itself.
    private readonly Dictionary<string, List<Admission>> _byWorkspaceRoot = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private long _nextAdmissionId;

    private sealed record Admission(long Id, ScanGovernorSnapshot Snapshot);

    public ScanGovernorState()
        : this(static () => DateTimeOffset.UtcNow)
    {
    }

    internal ScanGovernorState(Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(utcNow);
        _utcNow = utcNow;
    }

    /// <summary>
    /// Record that this process is queued for <paramref name="request"/>, behind <paramref name="holder"/>.
    /// Returns the admission id every later <see cref="EnterHolding"/>/<see cref="Exit"/> for this request must
    /// carry, so concurrent admissions for one root cannot clobber each other.
    /// </summary>
    public long EnterWaiting(ScanGovernorRequest request, ScanGovernorOwner? holder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceRoot);
        return Add(request.WorkspaceRoot, new ScanGovernorSnapshot(
            ScanGovernorStates.Waiting, request.Reason, _utcNow(), holder?.Pid, holder?.WorkspaceRoot));
    }

    /// <summary>Promote the admission <paramref name="admissionId"/> to holding. A dropped id is re-added.</summary>
    public void EnterHolding(long admissionId, ScanGovernorRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceRoot);
        var holding = new ScanGovernorSnapshot(
            ScanGovernorStates.Holding, request.Reason, _utcNow(), HolderPid: null, HolderWorkspaceRoot: null);

        lock (_gate)
        {
            List<Admission> admissions = _byWorkspaceRoot.TryGetValue(request.WorkspaceRoot, out List<Admission>? existing)
                ? existing
                : _byWorkspaceRoot[request.WorkspaceRoot] = [];
            int index = admissions.FindIndex(a => a.Id == admissionId);
            if (index < 0)
                admissions.Add(new Admission(admissionId, holding));
            else
                admissions[index] = new Admission(admissionId, holding);
        }
    }

    /// <summary>Drop ONLY the entry <paramref name="admissionId"/> created; a sibling admission is untouched.</summary>
    public void Exit(long admissionId, string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        lock (_gate)
        {
            if (!_byWorkspaceRoot.TryGetValue(workspaceRoot, out List<Admission>? admissions))
                return;
            admissions.RemoveAll(a => a.Id == admissionId);
            if (admissions.Count == 0)
                _byWorkspaceRoot.Remove(workspaceRoot);
        }
    }

    /// <summary>
    /// This process's position for <paramref name="workspaceRoot"/>, or null when idle. A live holding entry
    /// always wins over a concurrent waiter's, so status never reports the holder as queued.
    /// </summary>
    public ScanGovernorSnapshot? Snapshot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        lock (_gate)
        {
            if (!_byWorkspaceRoot.TryGetValue(workspaceRoot, out List<Admission>? admissions) ||
                admissions.Count == 0)
                return null;

            foreach (Admission admission in admissions)
            {
                if (string.Equals(admission.Snapshot.State, ScanGovernorStates.Holding, StringComparison.Ordinal))
                    return admission.Snapshot;
            }

            return admissions[0].Snapshot;
        }
    }

    private long Add(string workspaceRoot, ScanGovernorSnapshot snapshot)
    {
        lock (_gate)
        {
            long id = ++_nextAdmissionId;
            if (!_byWorkspaceRoot.TryGetValue(workspaceRoot, out List<Admission>? admissions))
                _byWorkspaceRoot[workspaceRoot] = admissions = [];
            admissions.Add(new Admission(id, snapshot));
            return id;
        }
    }
}

/// <summary>
/// One governed scan's admission: the OS lease from <see cref="ScanGovernor"/> paired with the process-local
/// <see cref="ScanGovernorState"/> bookkeeping status/health render. It exists so the enter/exit pairing is
/// exception-safe in ONE place rather than repeated at every governed call site; a refused or throwing acquire
/// leaves no orphan entry.
/// </summary>
public sealed class ScanGovernorAdmission : IDisposable
{
    private readonly ScanGovernorLease _lease;
    private readonly ScanGovernorState? _state;
    private readonly long _admissionId;
    private readonly string _workspaceRoot;
    private bool _disposed;

    private ScanGovernorAdmission(
        ScanGovernorLease lease, ScanGovernorState? state, long admissionId, string workspaceRoot)
    {
        _lease = lease;
        _state = state;
        _admissionId = admissionId;
        _workspaceRoot = workspaceRoot;
    }

    /// <summary>
    /// Wait up to <paramref name="timeout"/> for admission, publishing waiting/holding state for the request's
    /// workspace root. Returns null when the budget expired (the caller must degrade, never scan ungoverned).
    /// Pass a null <paramref name="state"/> to take the lease WITHOUT publishing a position — the right call
    /// when the caller cannot name a workspace root readers key by, since a position filed under a key nobody
    /// looks up is invisible anyway. A disabled governor admits immediately and publishes nothing.
    /// </summary>
    public static ScanGovernorAdmission? TryAcquire(
        ScanGovernor governor,
        ScanGovernorState? state,
        ScanGovernorRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(governor);

        if (!governor.Enabled || state is null)
        {
            ScanGovernorLease? unpublished = governor.TryAcquire(request, timeout, cancellationToken);
            return unpublished is null
                ? null
                : new ScanGovernorAdmission(unpublished, state: null, admissionId: 0, request.WorkspaceRoot);
        }

        long admissionId = state.EnterWaiting(request, governor.TryReadOwner());
        ScanGovernorLease? lease;
        try
        {
            lease = governor.TryAcquire(request, timeout, cancellationToken);
        }
        catch
        {
            state.Exit(admissionId, request.WorkspaceRoot);
            throw;
        }

        if (lease is null)
        {
            state.Exit(admissionId, request.WorkspaceRoot);
            return null;
        }

        try
        {
            state.EnterHolding(admissionId, request);
        }
        catch
        {
            lease.Dispose();
            state.Exit(admissionId, request.WorkspaceRoot);
            throw;
        }

        return new ScanGovernorAdmission(lease, state, admissionId, request.WorkspaceRoot);
    }

    /// <summary>Release the lease, then drop this process's recorded position. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lease.Dispose();
        _state?.Exit(_admissionId, _workspaceRoot);
    }
}
