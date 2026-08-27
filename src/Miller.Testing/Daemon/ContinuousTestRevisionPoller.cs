using System.Globalization;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Testing;

namespace Miller.Testing;

public interface IContinuousTestRevisionSource
{
    Task<ContinuousTestRevisionObservation?> RefreshAsync(
        string workspaceId,
        string workspaceRoot,
        CancellationToken cancellationToken = default);
}

public interface IContinuousTestImpactSource
{
    Task<ContinuousTestImpactResult?> ImpactAsync(
        string workspaceRoot,
        CtFreshnessKey current,
        CtFreshnessKey? from,
        CancellationToken cancellationToken = default);
}

public enum ContinuousTestImpactOutcome
{
    Unavailable,
    Empty,
    Changed,
}

public sealed record ContinuousTestImpactResult(
    string WorkspaceId,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<ContinuousTestImpactedSymbol> ImpactedSymbols,
    IReadOnlyList<ContinuousTestImpactedTest> ImpactedTests)
{
    public ContinuousTestImpactOutcome Outcome { get; init; } = ContinuousTestImpactOutcome.Changed;

    public string? Reason { get; init; }

    public long? FromRevision { get; init; }

    public long? ToRevision { get; init; }
}

public sealed record ContinuousTestRevisionObservation(
    string WorkspaceId,
    CtFreshnessKey? Freshness,
    bool IndexFresh,
    string Status,
    DateTimeOffset ObservedAt,
    bool Rebuild = false);

public sealed record ContinuousTestRevisionPollRequest
{
    public string WorkspaceId { get; init; }
    public string WorkspaceRoot { get; init; }
    public IReadOnlyList<ContinuousTestProject> Projects { get; init; }
    public IContinuousTestDaemonEnqueuer Enqueuer { get; init; }
    public TimeSpan? DebounceDelay { get; init; }
    public bool EnqueueArmed { get; init; }
    public Action<CtFreshnessKey>? OnRebuild { get; init; }

    public ContinuousTestRevisionPollRequest(
        string WorkspaceId,
        string WorkspaceRoot,
        IReadOnlyList<ContinuousTestProject> Projects,
        IContinuousTestDaemonEnqueuer Enqueuer,
        TimeSpan? DebounceDelay = null,
        bool EnqueueArmed = false,
        Action<CtFreshnessKey>? OnRebuild = null)
    {
        if (string.IsNullOrWhiteSpace(WorkspaceId))
            throw new ArgumentException("must not be empty", nameof(WorkspaceId));
        if (string.IsNullOrWhiteSpace(WorkspaceRoot))
            throw new ArgumentException("must not be empty", nameof(WorkspaceRoot));
        ArgumentNullException.ThrowIfNull(Projects);
        ArgumentNullException.ThrowIfNull(Enqueuer);
        if (DebounceDelay is { } delay && delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(DebounceDelay));

        this.WorkspaceId = WorkspaceId;
        this.WorkspaceRoot = WorkspaceRoot;
        this.Projects = Projects;
        this.Enqueuer = Enqueuer;
        this.DebounceDelay = DebounceDelay;
        this.EnqueueArmed = EnqueueArmed;
        this.OnRebuild = OnRebuild;
    }
}

public sealed record ContinuousTestRevisionPollResult(
    string WorkspaceId,
    CtFreshnessKey? Freshness,
    string Status,
    int EnqueuedProjects,
    string Reason)
{
    public bool Enqueued => EnqueuedProjects > 0;

    public string? DeltaReason { get; init; }

    public long? DeltaFromRevision { get; init; }

    public long? DeltaToRevision { get; init; }

    public int SelectedTests { get; init; }
}

/// <summary>
/// Polls the live artifact through its cheap freshness probe and enqueues only a complete delta: a changed delta
/// selects impacted tests, an empty delta becomes a pure watermark advance (known-empty in the
/// queue — nothing stales, nothing executes). Unavailable impact never enqueues and never falls
/// back to workspace scope.
/// </summary>
public sealed class ContinuousTestRevisionPoller
{
    public const string DebounceEnvironmentVariable = "MILLER_CT_DEBOUNCE";
    private const int MaxRevisionReconcileAttempts = 3;

    /// <summary>
    /// Default quiet period between an observed change and its automatic run. The daemon polls the
    /// artifact every 250 ms (<see cref="ContinuousTestDaemonHostOptions.PollInterval"/>), so two
    /// seconds spans eight poll ticks: long enough that a multi-file save burst whose files land
    /// across consecutive index revisions coalesces into ONE run (the queue resets the timer on
    /// each newly enqueued change), short enough to keep the edit-to-verdict loop tight. Low
    /// single digits per the 2026-08-21 watermark-freshness design, step 5.
    /// </summary>
    public static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromSeconds(2);

    private readonly IContinuousTestRevisionSource _source;
    private readonly IContinuousTestImpactSource? _impactSource;
    private readonly ContinuousTestStore? _cursorStore;
    private readonly TimeSpan _debounceDelay;
    private CtFreshnessKey? _lastFresh;
    private CtFreshnessKey? _lastObserved;
    private string? _cursorWorkspaceId;
    private bool _cursorLoaded;

    public ContinuousTestRevisionPoller(
        IContinuousTestRevisionSource source,
        IContinuousTestImpactSource? impactSource = null,
        TimeSpan? debounceDelay = null,
        ContinuousTestStore? cursorStore = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _impactSource = impactSource;
        _cursorStore = cursorStore;
        if (debounceDelay is { } explicitDelay && explicitDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceDelay));
        _debounceDelay = debounceDelay
            ?? ResolveDebounceDelay(Environment.GetEnvironmentVariable(DebounceEnvironmentVariable));
    }

    /// <summary>
    /// Parses <c>MILLER_CT_DEBOUNCE</c> (seconds). A non-negative integer or decimal is honored
    /// verbatim — <c>0</c> means run immediately. Unset, invalid, negative, or absurd (over an
    /// hour) values fall back to <see cref="DefaultDebounceDelay"/>: a broken override must
    /// degrade to the default, never disable or wedge the auto-run loop.
    /// </summary>
    public static TimeSpan ResolveDebounceDelay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultDebounceDelay;
        if (!double.TryParse(
                raw.Trim(),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double seconds))
        {
            return DefaultDebounceDelay;
        }

        return seconds <= 3600d ? TimeSpan.FromSeconds(seconds) : DefaultDebounceDelay;
    }

    public async Task<ContinuousTestRevisionPollResult> PollAsync(
        ContinuousTestRevisionPollRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LoadCursor(request.WorkspaceId);

        ContinuousTestRevisionObservation? observation = await _source
            .RefreshAsync(request.WorkspaceId, request.WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);
        if (observation is null)
            return Result(request.WorkspaceId, null, "missing", 0, "missing_revision");

        if (!string.Equals(observation.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"revision source returned workspace '{observation.WorkspaceId}' for requested workspace '{request.WorkspaceId}'");
        }

        if (observation.Freshness is not { } freshness || !observation.IndexFresh)
            return Result(request.WorkspaceId, observation.Freshness, observation.Status, 0, "degraded");

        if (observation.Rebuild || IdentityChanged(freshness))
        {
            if (_lastObserved != freshness)
                request.OnRebuild?.Invoke(freshness);
            _lastObserved = freshness;
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "rebuild");
        }

        if (_lastFresh is { } last && last == freshness)
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "same_revision");

        if (_lastFresh is null)
        {
            _lastObserved = freshness;
            _lastFresh = freshness;
            SaveCursor(freshness);
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "status-only");
        }

        ContinuousTestImpactResult? impact = null;
        for (int attempt = 0; attempt < MaxRevisionReconcileAttempts; attempt++)
        {
            impact = await ResolveImpactAsync(request, freshness, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(impact?.Reason, "moving_cursor", StringComparison.Ordinal)
                || attempt == MaxRevisionReconcileAttempts - 1)
            {
                break;
            }

            observation = await _source
                .RefreshAsync(request.WorkspaceId, request.WorkspaceRoot, cancellationToken)
                .ConfigureAwait(false);
            if (observation is null)
                return Result(request.WorkspaceId, null, "missing", 0, "missing_revision");
            if (!string.Equals(observation.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"revision source returned workspace '{observation.WorkspaceId}' for requested workspace '{request.WorkspaceId}'");
            }

            if (observation.Freshness is not { } retriedFreshness || !observation.IndexFresh)
                return Result(request.WorkspaceId, observation.Freshness, observation.Status, 0, "degraded");
            if (observation.Rebuild || IdentityChanged(retriedFreshness))
            {
                if (_lastObserved != retriedFreshness)
                    request.OnRebuild?.Invoke(retriedFreshness);
                _lastObserved = retriedFreshness;
                return Result(request.WorkspaceId, retriedFreshness, observation.Status, 0, "rebuild");
            }

            freshness = retriedFreshness;
        }
        ContinuousTestImpactOutcome outcome = impact?.Outcome ?? ContinuousTestImpactOutcome.Unavailable;
        if (outcome == ContinuousTestImpactOutcome.Unavailable)
        {
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "unavailable_delta") with
            {
                DeltaReason = impact?.Reason,
                DeltaFromRevision = impact?.FromRevision,
                DeltaToRevision = impact?.ToRevision,
            };
        }

        // An EMPTY outcome flows through the enqueuer like a known-empty change (defect D3): the
        // queue's ApplyRevisionAdvance is the ONE watermark writer, so absorbing the advance here
        // would strand every green watermark at the old revision. A changed outcome must name at
        // least one path; an empty one must name none.
        bool empty = outcome == ContinuousTestImpactOutcome.Empty;
        if (impact is null
            || impact.FromRevision is not { } from
            || impact.ToRevision is not { } to
            || to != freshness.Revision
            || from >= to
            || _lastFresh is not { } lastFresh
            || from != lastFresh.Revision
            || (impact.ChangedPaths.Count == 0) != empty)
        {
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "unavailable_delta") with
            {
                DeltaReason = impact?.Reason ?? "delta_interval_incomplete",
            };
        }

        IReadOnlyList<ContinuousTestProjectWorkItem> workItems =
            ContinuousTestProjectInventory.MaterializeProjectWorkItems(request.Projects, request.WorkspaceRoot);
        if (workItems.Count == 0)
        {
            // The enqueuer's ApplyRevisionAdvance is what makes an interval's staleness land, and
            // it runs once per work item. With zero work items nothing landed, so the cursor must
            // stay put: saving here would let the next advance seed green watermarks across an
            // interval nobody reconciled.
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "no_projects") with
            {
                DeltaFromRevision = from,
                DeltaToRevision = to,
            };
        }

        int enqueued = 0;
        int selected = 0;
        IReadOnlyList<string> normalized = ContinuousTestDurableFreshness.NormalizeDeltaPaths(impact.ChangedPaths);
        foreach (ContinuousTestProjectWorkItem workItem in workItems)
        {
            ContinuousTestDaemonEnqueueResult enqueue = request.Enqueuer.Enqueue(new ContinuousTestDaemonChange(
                Workspace: workItem.Workspace,
                CurrentRevision: freshness.Revision.ToString(CultureInfo.InvariantCulture),
                IndexIdentity: freshness.IndexIdentity,
                ChangedPaths: normalized,
                ImpactedSymbols: impact.ImpactedSymbols,
                ImpactedTests: impact.ImpactedTests,
                WorkspaceScope: false,
                DebounceDelay: request.DebounceDelay ?? _debounceDelay,
                ObservedAt: observation.ObservedAt,
                Command: workItem.Project.Command,
                Framework: workItem.Project.Framework,
                DeltaCompleteness: ContinuousTestDeltaCompleteness.Complete,
                DeltaFromRevision: from,
                DeltaToRevision: to));
            selected += enqueue.Selection.SelectedTestCaseIds.Count;
            enqueued++;
        }

        SaveCursor(freshness);
        _lastObserved = freshness;
        _lastFresh = freshness;
        return new ContinuousTestRevisionPollResult(
            request.WorkspaceId,
            freshness,
            observation.Status,
            enqueued,
            empty ? "no_source_delta" : "enqueued")
        {
            SelectedTests = selected,
            DeltaReason = empty ? impact.Reason ?? "no_source_delta" : null,
            DeltaFromRevision = from,
            DeltaToRevision = to,
        };
    }

    private bool IdentityChanged(CtFreshnessKey freshness) =>
        _lastFresh is { } last
        && !string.Equals(last.IndexIdentity, freshness.IndexIdentity, StringComparison.Ordinal);

    private void LoadCursor(string workspaceId)
    {
        if (_cursorLoaded && string.Equals(_cursorWorkspaceId, workspaceId, StringComparison.Ordinal))
            return;

        _lastObserved = null;
        _cursorWorkspaceId = workspaceId;
        _cursorLoaded = true;
        _lastFresh = _cursorStore?.ReadLastReconciledCursor(workspaceId);
    }

    private void SaveCursor(CtFreshnessKey freshness) =>
        _cursorStore?.SaveLastReconciledCursor(_cursorWorkspaceId!, freshness);

    private async Task<ContinuousTestImpactResult?> ResolveImpactAsync(
        ContinuousTestRevisionPollRequest request,
        CtFreshnessKey current,
        CancellationToken cancellationToken)
    {
        if (_impactSource is null)
        {
            return new ContinuousTestImpactResult(request.WorkspaceId, [], [], [])
            {
                Outcome = ContinuousTestImpactOutcome.Unavailable,
                Reason = "no_capability",
            };
        }

        try
        {
            return await _impactSource
                .ImpactAsync(request.WorkspaceRoot, current, _lastFresh, cancellationToken)
                .ConfigureAwait(false)
                ?? new ContinuousTestImpactResult(request.WorkspaceId, [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Unavailable,
                    Reason = "bridge_null",
                };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ContinuousTestImpactResult(request.WorkspaceId, [], [], [])
            {
                Outcome = ContinuousTestImpactOutcome.Unavailable,
                Reason = "bridge_error",
            };
        }
    }

    private static ContinuousTestRevisionPollResult Result(
        string workspaceId,
        CtFreshnessKey? freshness,
        string status,
        int enqueued,
        string reason) =>
        new(workspaceId, freshness, status, enqueued, reason);
}

/// <summary>
/// Probes the live Miller artifact each poll. A new generation identity is a rebuild; a routine
/// write or a revision-only advance never is. Full sessions remain in the impact path only.
/// </summary>
public sealed class MillerArtifactRevisionSource : IContinuousTestRevisionSource
{
    private string? _lastIdentity;

    public Task<ContinuousTestRevisionObservation?> RefreshAsync(
        string workspaceId,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string dbPath = Path.Combine(workspaceRoot, CtSchema.MillerDirectoryName, "symbols.db");
        try
        {
            WorkspaceFreshnessProbe probe = WorkspaceReadSessionFactory.Probe(
                dbPath, workspaceRoot, workspaceId);
            var freshness = new CtFreshnessKey(
                probe.IndexGenerationIdentity
                    ?? throw new InvalidOperationException("freshness probe did not provide a CT identity"),
                probe.Revision);
            bool rebuild = _lastIdentity is not null
                && !string.Equals(_lastIdentity, freshness.IndexIdentity, StringComparison.Ordinal);
            _lastIdentity = freshness.IndexIdentity;
            return Task.FromResult<ContinuousTestRevisionObservation?>(new ContinuousTestRevisionObservation(
                workspaceId,
                freshness,
                IndexFresh: true,
                Status: "fresh",
                ObservedAt: DateTimeOffset.UtcNow,
                Rebuild: rebuild));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or FamilyStoreReadException)
        {
            return Task.FromResult<ContinuousTestRevisionObservation?>(new ContinuousTestRevisionObservation(
                workspaceId,
                Freshness: null,
                IndexFresh: false,
                Status: "degraded",
                ObservedAt: DateTimeOffset.UtcNow));
        }
    }
}

/// <summary>
/// Impact from <see cref="RevisionDeltaReader"/> plus <see cref="CtFactAdapter"/>. Unavailable
/// deltas stay unavailable; a truncated impact read still delivers the complete delta as Changed
/// with the <c>impact_truncated</c> reason, so the selector resolves it to Unknown instead of the
/// poller stalling on an unadvanceable cursor.
/// </summary>
public sealed class MillerFactImpactSource : IContinuousTestImpactSource
{
    private readonly Func<string, ICtFactSource>? _openFacts;

    public MillerFactImpactSource(Func<string, ICtFactSource>? openFacts = null)
    {
        _openFacts = openFacts;
    }

    public Task<ContinuousTestImpactResult?> ImpactAsync(
        string workspaceRoot,
        CtFreshnessKey current,
        CtFreshnessKey? from,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (from is not { } fromKey)
        {
            return Task.FromResult<ContinuousTestImpactResult?>(new ContinuousTestImpactResult("", [], [], [])
            {
                Outcome = ContinuousTestImpactOutcome.Unavailable,
                Reason = "no_delta_base",
            });
        }

        if (!string.Equals(fromKey.IndexIdentity, current.IndexIdentity, StringComparison.Ordinal))
        {
            return Task.FromResult<ContinuousTestImpactResult?>(new ContinuousTestImpactResult("", [], [], [])
            {
                Outcome = ContinuousTestImpactOutcome.Unavailable,
                Reason = "identity_changed",
            });
        }

        return Task.FromResult<ContinuousTestImpactResult?>(ReadAttempt(workspaceRoot, current, fromKey));
    }

    private ContinuousTestImpactResult ReadAttempt(
        string workspaceRoot,
        CtFreshnessKey current,
        CtFreshnessKey from)
    {
        string dbPath = Path.Combine(workspaceRoot, CtSchema.MillerDirectoryName, "symbols.db");
        try
        {
            using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                dbPath,
                workspaceRoot,
                workspaceId: null);
            CtIndexCursor cursor = CtIndexCursor.FromSnapshot(session.Snapshot);
            if (!string.Equals(cursor.IndexIdentity, current.IndexIdentity, StringComparison.Ordinal)
                || cursor.Revision != current.Revision)
            {
                return new ContinuousTestImpactResult("", [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Unavailable,
                    Reason = "moving_cursor",
                };
            }

            // The delta reader compares this against the artifact's own family/artifact id, so it
            // gets the cursor's family id, never the composed generation-identity string.
            RevisionDeltaResult delta = RevisionDeltaReader.Read(session, from.Revision, cursor.FamilyId);
            if (delta.Status != RevisionDeltaStatus.Complete)
            {
                return new ContinuousTestImpactResult("", [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Unavailable,
                    Reason = delta.Reason,
                    FromRevision = delta.FromRevision,
                    ToRevision = delta.ToRevision,
                };
            }

            if (delta.ToRevision != current.Revision)
            {
                return new ContinuousTestImpactResult("", [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Unavailable,
                    Reason = "moving_cursor",
                    FromRevision = delta.FromRevision,
                    ToRevision = delta.ToRevision,
                };
            }

            if (delta.ChangedPaths.Count == 0)
            {
                return new ContinuousTestImpactResult("", [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Empty,
                    Reason = "no_source_delta",
                    FromRevision = delta.FromRevision,
                    ToRevision = delta.ToRevision,
                };
            }

            ICtFactSource facts = _openFacts?.Invoke(workspaceRoot) ?? new CtFactAdapter(session);
            try
            {
                IReadOnlyList<CtSymbolFact> symbols = facts.SymbolsForChangedFiles(delta.ChangedPaths);
                string[] seedIds = symbols.Select(row => row.SymbolId).Distinct(StringComparer.Ordinal).ToArray();
                CtImpactResult impact = facts.Impact(seedIds);

                // A truncated read is an incomplete blast radius, but the DELTA itself is complete:
                // the changed paths come from the journal, not the impact traversal. Answering
                // Unavailable here pinned the poller's cursor, grew the interval every poll, and
                // paused auto-runs after eight misses (2026-08-26 field report). The delta is
                // delivered as Changed instead; the selector re-runs the same bounded impact read
                // over the same seeds, hits the same truncation, and fails the selection closed to
                // Unknown — staleness lands via ApplyRevisionAdvance, nothing executes, and the
                // cursor may legally advance.
                bool truncated = impact.TruncatedByDepth || impact.TruncatedByLimit;
                return new ContinuousTestImpactResult(
                    "",
                    delta.ChangedPaths,
                    impact.Impacted.Select(ToSymbol).ToArray(),
                    impact.Tests.Select(ToTest).ToArray())
                {
                    Outcome = ContinuousTestImpactOutcome.Changed,
                    Reason = truncated ? "impact_truncated" : null,
                    FromRevision = delta.FromRevision,
                    ToRevision = delta.ToRevision,
                };
            }
            finally
            {
                if (_openFacts is not null)
                    (facts as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException or FamilyStoreReadException)
        {
            return new ContinuousTestImpactResult("", [], [], [])
            {
                Outcome = ContinuousTestImpactOutcome.Unavailable,
                Reason = "bridge_error",
            };
        }
    }

    private static ContinuousTestImpactedSymbol ToSymbol(CtImpactedSymbol row) =>
        new(SymbolId: row.SymbolId, Path: row.FilePath, Name: row.Name);

    private static ContinuousTestImpactedTest ToTest(CtImpactedSymbol row) =>
        new(SymbolId: row.SymbolId, Path: row.FilePath, Name: row.Name, Hop: row.Hop, TestCase: row.IsTest);
}
