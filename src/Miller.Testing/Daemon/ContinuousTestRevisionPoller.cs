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
/// Polls the live artifact (reopen per poll) and enqueues only a complete changed delta.
/// Unavailable impact never enqueues and never falls back to workspace scope.
/// </summary>
public sealed class ContinuousTestRevisionPoller
{
    private readonly IContinuousTestRevisionSource _source;
    private readonly IContinuousTestImpactSource? _impactSource;
    private CtFreshnessKey? _lastFresh;

    public ContinuousTestRevisionPoller(
        IContinuousTestRevisionSource source,
        IContinuousTestImpactSource? impactSource = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _impactSource = impactSource;
    }

    public async Task<ContinuousTestRevisionPollResult> PollAsync(
        ContinuousTestRevisionPollRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
            request.OnRebuild?.Invoke(freshness);
            _lastFresh = freshness;
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "rebuild");
        }

        if (_lastFresh is { } last && last == freshness)
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "same_revision");

        if (!request.EnqueueArmed || _lastFresh is null)
        {
            _lastFresh = freshness;
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "status-only");
        }

        ContinuousTestImpactResult? impact = await ResolveImpactAsync(request, freshness, cancellationToken)
            .ConfigureAwait(false);
        ContinuousTestImpactOutcome outcome = impact?.Outcome ?? ContinuousTestImpactOutcome.Unavailable;
        if (outcome != ContinuousTestImpactOutcome.Changed)
        {
            string reason = outcome == ContinuousTestImpactOutcome.Empty ? "no_source_delta" : "unavailable_delta";
            if (outcome == ContinuousTestImpactOutcome.Empty)
                _lastFresh = freshness;
            return Result(request.WorkspaceId, freshness, observation.Status, 0, reason) with
            {
                DeltaReason = impact?.Reason ?? (outcome == ContinuousTestImpactOutcome.Empty ? "no_source_delta" : impact?.Reason),
                DeltaFromRevision = impact?.FromRevision,
                DeltaToRevision = impact?.ToRevision,
            };
        }

        if (impact is null
            || impact.FromRevision is not { } from
            || impact.ToRevision is not { } to
            || to != freshness.Revision
            || from >= to
            || impact.ChangedPaths.Count == 0)
        {
            return Result(request.WorkspaceId, freshness, observation.Status, 0, "unavailable_delta") with
            {
                DeltaReason = impact?.Reason ?? "delta_interval_incomplete",
            };
        }

        IReadOnlyList<ContinuousTestProjectWorkItem> workItems =
            ContinuousTestProjectInventory.MaterializeProjectWorkItems(request.Projects, request.WorkspaceRoot);
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
                DebounceDelay: request.DebounceDelay ?? TimeSpan.Zero,
                ObservedAt: observation.ObservedAt,
                Command: workItem.Project.Command,
                Framework: workItem.Project.Framework,
                DeltaCompleteness: ContinuousTestDeltaCompleteness.Complete,
                DeltaFromRevision: from,
                DeltaToRevision: to));
            selected += enqueue.Selection.SelectedTestCaseIds.Count;
            enqueued++;
        }

        _lastFresh = freshness;
        return new ContinuousTestRevisionPollResult(
            request.WorkspaceId,
            freshness,
            observation.Status,
            enqueued,
            enqueued > 0 ? "enqueued" : "no_projects")
        {
            SelectedTests = selected,
            DeltaFromRevision = from,
            DeltaToRevision = to,
        };
    }

    private bool IdentityChanged(CtFreshnessKey freshness) =>
        _lastFresh is { } last
        && !string.Equals(last.IndexIdentity, freshness.IndexIdentity, StringComparison.Ordinal);

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
/// Reopens the live Miller artifact each poll. A new index identity is a rebuild, never a
/// revision-only advance.
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
            using WorkspaceReadHandle handle = WorkspaceReadSessionFactory.Open(
                dbPath, workspaceRoot, workspaceId);
            WorkspaceReadSnapshot snapshot = handle.Snapshot;
            long revision = snapshot.Mode == WorkspaceReadMode.FamilyStore
                ? snapshot.Freshness.StoreLogSequence ?? snapshot.Freshness.Revision
                : snapshot.Freshness.Revision;
            var freshness = new CtFreshnessKey(snapshot.IndexIdentity, revision);
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
/// deltas stay unavailable.
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

        string dbPath = Path.Combine(workspaceRoot, CtSchema.MillerDirectoryName, "symbols.db");
        try
        {
            using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(dbPath, workspaceRoot, workspaceId: null);
            RevisionDeltaResult delta = RevisionDeltaReader.Read(session, fromKey.Revision, fromKey.IndexIdentity);
            if (delta.Status != RevisionDeltaStatus.Complete || delta.ToRevision != current.Revision)
            {
                return Task.FromResult<ContinuousTestImpactResult?>(new ContinuousTestImpactResult("", [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Unavailable,
                    Reason = delta.Reason,
                    FromRevision = delta.FromRevision,
                    ToRevision = delta.ToRevision,
                });
            }

            if (delta.ChangedPaths.Count == 0)
            {
                return Task.FromResult<ContinuousTestImpactResult?>(new ContinuousTestImpactResult("", [], [], [])
                {
                    Outcome = ContinuousTestImpactOutcome.Empty,
                    Reason = "no_source_delta",
                    FromRevision = delta.FromRevision,
                    ToRevision = delta.ToRevision,
                });
            }

            ICtFactSource facts = _openFacts?.Invoke(workspaceRoot) ?? new CtFactAdapter(session);
            try
            {
                IReadOnlyList<CtSymbolFact> symbols = facts.SymbolsForChangedFiles(delta.ChangedPaths);
                string[] seedIds = symbols.Select(row => row.SymbolId).Distinct(StringComparer.Ordinal).ToArray();
                CtImpactResult impact = facts.Impact(seedIds);
                return Task.FromResult<ContinuousTestImpactResult?>(new ContinuousTestImpactResult(
                    "",
                    delta.ChangedPaths,
                    impact.Impacted.Select(ToSymbol).ToArray(),
                    impact.Tests.Select(ToTest).ToArray())
                {
                    Outcome = ContinuousTestImpactOutcome.Changed,
                    FromRevision = delta.FromRevision,
                    ToRevision = delta.ToRevision,
                });
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
            return Task.FromResult<ContinuousTestImpactResult?>(new ContinuousTestImpactResult("", [], [], [])
            {
                Outcome = ContinuousTestImpactOutcome.Unavailable,
                Reason = "bridge_error",
            });
        }
    }

    private static ContinuousTestImpactedSymbol ToSymbol(CtImpactedSymbol row) =>
        new(SymbolId: row.SymbolId, Path: row.FilePath, Name: row.Name);

    private static ContinuousTestImpactedTest ToTest(CtImpactedSymbol row) =>
        new(SymbolId: row.SymbolId, Path: row.FilePath, Name: row.Name, Hop: row.Hop, TestCase: row.IsTest);
}
