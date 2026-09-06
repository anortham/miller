using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Reads;

namespace Miller.Server.Hosting;

internal sealed record StoreSidecarConvergenceOutcome(
    string Status,
    bool DidWork,
    bool Pending,
    bool LeaderRequired,
    string? Reason)
{
    public bool Failed => string.Equals(Status, StoreSidecarConvergenceStatuses.Failed, StringComparison.Ordinal);

    public string? FailureReason => Failed ? Reason : null;
}

internal sealed record StoreSidecarConvergenceResult(
    long TargetSequence,
    StoreSidecarConvergenceOutcome Content,
    StoreSidecarConvergenceOutcome Search,
    StoreSidecarConvergenceOutcome Vector,
    bool MetricsDidWork = false)
{
    public bool DidWork => Content.DidWork || Search.DidWork || Vector.DidWork || MetricsDidWork;

    public bool Pending => Content.Pending || Search.Pending || Vector.Pending;

    public bool LeaderRequired =>
        Content.LeaderRequired || Search.LeaderRequired || Vector.LeaderRequired;

    public string? FailureReason =>
        BoundReason(
            string.Join(
                "; ",
                new[] { Content, Search, Vector }
                    .Where(static outcome => outcome.FailureReason is not null)
                    .Select(static outcome => outcome.FailureReason)));

    public string? WarningText =>
        FailureReason is { Length: > 0 } failure
            ? $"Store sidecar convergence is incomplete: {failure}"
            : LeaderRequired
            ? "Store sidecar convergence requires a resident leader to drain the vector target."
            : Pending
            ? "Store sidecar convergence is pending completion."
            : null;

    private static string? BoundReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;
        return reason.Length <= IndexerSidecarConverger.MaxFailureReasonLength
            ? reason
            : reason[..IndexerSidecarConverger.MaxFailureReasonLength];
    }
}

internal static class StoreSidecarConvergenceStatuses
{
    public const string Disabled = "disabled";
    public const string Current = "current";
    public const string Repaired = "repaired";
    public const string Queued = "queued";
    public const string LeaderRequired = "leader_required";
    public const string Failed = "failed";
}

internal sealed class IndexerSidecarConverger
{
    internal const int MaxFailureReasonLength = 300;

    internal delegate bool SearchConvergence(
        string symbolsDbPath,
        long revision,
        string workspaceRoot,
        out string? corruptionReason);

    private readonly bool _searchEnabled;
    private readonly Func<string, string, string?, long, bool> _ensureContentBuilt;
    private readonly SearchConvergence _ensureSearchBuilt;
    private readonly SearchConvergence _ensureSearchCurrent;
    private readonly Func<string, string> _contentDbPathFor;
    private readonly Func<string, string> _searchDbPathFor;
    private readonly Func<Exception, string, Action, bool> _tryRecover;
    private readonly ILogger _logger;
    private readonly VectorConvergeSignal _vectorSignal;
    private readonly Func<string, IWorkspaceReadSession, SidecarConvergenceDetail>? _ensureStoreContent;
    private readonly Func<string, IWorkspaceReadSession, SidecarConvergenceDetail>? _ensureStoreSearch;
    private readonly Func<bool> _vectorDrainAvailable;
    private readonly IIndexerPhaseSink _phaseSink;

    public IndexerSidecarConverger(
        SymbolSearchSidecar searchSidecar,
        ContentCorpusSidecar contentSidecar,
        ILogger logger,
        VectorConvergeSignal? vectorSignal = null,
        IIndexerPhaseSink? phaseSink = null,
        Func<bool>? vectorDrainAvailable = null)
        : this(
            searchSidecar.Enabled,
            contentSidecar.EnsureBuilt,
            (string symbolsDbPath, long revision, string workspaceRoot, out string? corruptionReason) =>
                searchSidecar.EnsureBuilt(symbolsDbPath, revision, workspaceRoot, out corruptionReason),
            (string symbolsDbPath, long revision, string workspaceRoot, out string? corruptionReason) =>
                searchSidecar.EnsureCurrent(symbolsDbPath, revision, workspaceRoot, out corruptionReason),
            ContentCorpusSidecar.ContentDbPathFor,
            SymbolSearchSidecar.SearchDbPathFor,
            (ex, sidecarPath, rebuild) =>
                SidecarCorruptionRecovery.TryRebuildCorruptSidecar(ex, sidecarPath, rebuild, logger),
            logger,
            vectorSignal,
            contentSidecar.EnsureStoreCurrentDetailed,
            searchSidecar.EnsureStoreCurrentDetailed,
            phaseSink,
            vectorDrainAvailable)
    {
    }

    internal IndexerSidecarConverger(
        bool searchEnabled,
        Func<string, string, string?, long, bool> ensureContentBuilt,
        SearchConvergence ensureSearchBuilt,
        SearchConvergence ensureSearchCurrent,
        Func<string, string> contentDbPathFor,
        Func<string, string> searchDbPathFor,
        Func<Exception, string, Action, bool> tryRecover,
        ILogger logger,
        VectorConvergeSignal? vectorSignal = null,
        Func<string, IWorkspaceReadSession, SidecarConvergenceDetail>? ensureStoreContent = null,
        Func<string, IWorkspaceReadSession, SidecarConvergenceDetail>? ensureStoreSearch = null,
        IIndexerPhaseSink? phaseSink = null,
        Func<bool>? vectorDrainAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(ensureContentBuilt);
        ArgumentNullException.ThrowIfNull(ensureSearchBuilt);
        ArgumentNullException.ThrowIfNull(ensureSearchCurrent);
        ArgumentNullException.ThrowIfNull(contentDbPathFor);
        ArgumentNullException.ThrowIfNull(searchDbPathFor);
        ArgumentNullException.ThrowIfNull(tryRecover);
        ArgumentNullException.ThrowIfNull(logger);

        _searchEnabled = searchEnabled;
        _ensureContentBuilt = ensureContentBuilt;
        _ensureSearchBuilt = ensureSearchBuilt;
        _ensureSearchCurrent = ensureSearchCurrent;
        _contentDbPathFor = contentDbPathFor;
        _searchDbPathFor = searchDbPathFor;
        _tryRecover = tryRecover;
        _logger = logger;
        _vectorSignal = vectorSignal ?? VectorConvergeSignal.Shared;
        _ensureStoreContent = ensureStoreContent;
        _ensureStoreSearch = ensureStoreSearch;
        _vectorDrainAvailable = vectorDrainAvailable ?? (static () => false);
        _phaseSink = phaseSink ?? new LoggingIndexerPhaseSink(logger);
    }

    public void Converge(
        string? symbolsDbPath,
        string workspaceRoot,
        string? workspaceId,
        long revision,
        bool fullRebuild)
    {
        using var totalPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.SidecarTotal);
        bool didWork = false;
        if (symbolsDbPath is null || revision <= 0)
        {
            RecordSkippedLegacyPhases(null);
            totalPhase.Skip();
            return;
        }

        // Resolve the derived-artifact paths once for corrupt-escalation. A pathological symbols.db path simply
        // disables the escalation path; the converge calls below surface the path problem themselves.
        string? contentDbPath = TryResolveSidecarPath(symbolsDbPath, _contentDbPathFor);
        string? searchDbPath = TryResolveSidecarPath(symbolsDbPath, _searchDbPathFor);

        didWork |= ConvergeContentCorpus(symbolsDbPath, workspaceRoot, workspaceId, revision, contentDbPath);
        if (_searchEnabled)
            didWork |= ConvergeSearch(symbolsDbPath, workspaceRoot, revision, fullRebuild, searchDbPath);
        else
            _phaseSink.RecordSafely(IndexerPhaseNames.Search, TimeSpan.Zero, IndexerPhaseOutcomes.Skipped, revision, false);

        using (var metricsPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Metrics))
        {
            MetricHistoryWriteResult? metrics = RecordConvergeHistory(symbolsDbPath, workspaceId, revision);
            if (metrics is null)
                metricsPhase.Skip(revision);
            else
            {
                bool metricsDidWork = metrics == MetricHistoryWriteResult.Recorded;
                didWork |= metricsDidWork;
                metricsPhase.Complete(revision, metricsDidWork);
            }
        }

        using (var vectorPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Vector))
        {
            long previousTarget = _vectorSignal.TargetRevision;
            _vectorSignal.StampTarget(revision, fullRebuild);
            long currentTarget = _vectorSignal.TargetRevision;
            bool vectorDidWork = _vectorSignal.Enabled && (currentTarget > previousTarget || fullRebuild);
            didWork |= vectorDidWork;
            if (_vectorSignal.Enabled)
                vectorPhase.Complete(revision, vectorDidWork);
            else
                vectorPhase.Skip(revision);
        }
        totalPhase.Complete(revision, didWork);
    }

    internal StoreSidecarConvergenceResult ConvergeStore(string storeRoot, IWorkspaceReadSession session)
    {
        using var totalPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.SidecarTotal);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentNullException.ThrowIfNull(session);
        if (session.Snapshot.Mode != WorkspaceReadMode.FamilyStore)
            throw new ArgumentException("Store sidecar convergence requires a family-store read session.", nameof(session));

        long storeSequence = session.Snapshot.Freshness.StoreLogSequence
            ?? throw new InvalidOperationException("The family-store snapshot has no store_log sequence.");
        bool contentRecorded = false;
        bool searchRecorded = false;
        StoreSidecarConvergenceOutcome content = DisabledOutcome;
        StoreSidecarConvergenceOutcome search = DisabledOutcome;
        try
        {
            using (FamilyStoreSidecarWriteLease.AcquireFor(storeRoot))
            {
                if (_ensureStoreContent is not null)
                {
                    content = ConvergeStoreSidecar(
                        IndexerPhaseNames.Content,
                        () => _ensureStoreContent(storeRoot, session),
                        storeSequence);
                    contentRecorded = true;
                }
                else
                {
                    _phaseSink.RecordSafely(IndexerPhaseNames.Content, TimeSpan.Zero, IndexerPhaseOutcomes.Skipped, storeSequence, false);
                    content = DisabledOutcome;
                    contentRecorded = true;
                }

                if (_searchEnabled && _ensureStoreSearch is not null)
                {
                    search = ConvergeStoreSidecar(
                        IndexerPhaseNames.Search,
                        () => _ensureStoreSearch(storeRoot, session),
                        storeSequence);
                    searchRecorded = true;
                }
                else
                {
                    _phaseSink.RecordSafely(IndexerPhaseNames.Search, TimeSpan.Zero, IndexerPhaseOutcomes.Skipped, storeSequence, false);
                    search = DisabledOutcome;
                    searchRecorded = true;
                }
            }
        }
        catch (Exception ex) when (IsConvergenceException(ex))
        {
            if (_ensureStoreContent is not null)
            {
                if (!contentRecorded)
                    _phaseSink.RecordSafely(
                        IndexerPhaseNames.Content,
                        TimeSpan.Zero,
                        IndexerPhaseOutcomes.Failed,
                        storeSequence,
                        false);
                content = FailedOutcome(ex);
            }
            else
            {
                content = DisabledOutcome;
            }

            if (_searchEnabled && _ensureStoreSearch is not null)
            {
                if (!searchRecorded)
                    _phaseSink.RecordSafely(
                        IndexerPhaseNames.Search,
                        TimeSpan.Zero,
                        IndexerPhaseOutcomes.Failed,
                        storeSequence,
                        false);
                search = FailedOutcome(ex);
            }
            else
            {
                search = DisabledOutcome;
            }
            _logger.LogWarning(ex,
                "Family-store sidecar lease acquisition or release failed; derived sidecars will retry on the next convergence.");
        }

        bool metricsDidWork = false;

        using (var metricsPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Metrics))
        {
            MetricHistoryWriteResult? metrics = MetricSnapshotAggregates.RecordConverge(
                session,
                session.Snapshot.WorkspaceId,
                storeSequence,
                MillerVersion.Current,
                onError: ex => _logger.LogWarning(
                    ex,
                    "Metric-history store converge snapshot skipped; the trend will have a gap at store sequence {Sequence}.",
                    storeSequence));
            if (metrics is null)
                metricsPhase.Skip(storeSequence);
            else
            {
                metricsDidWork = metrics == MetricHistoryWriteResult.Recorded;
                metricsPhase.Complete(storeSequence, metricsDidWork);
            }
        }

        StoreSidecarConvergenceOutcome vector;
        using (var vectorPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Vector))
        {
            vector = ConvergeStoreVector(storeSequence, vectorPhase);
        }

        var result = new StoreSidecarConvergenceResult(storeSequence, content, search, vector, metricsDidWork);
        totalPhase.Complete(storeSequence, result.DidWork);
        return result;
    }

    private StoreSidecarConvergenceOutcome ConvergeStoreSidecar(
        string kind,
        Func<SidecarConvergenceDetail> converge,
        long? storeSequence)
    {
        using var phase = new IndexerPhaseScope(_phaseSink, kind);
        try
        {
            SidecarConvergenceDetail detail = converge();
            phase.Complete(storeSequence, detail.DidWork);
            RecordConvergenceDetailSafely(kind, detail);
            return detail.DidWork
                ? new(StoreSidecarConvergenceStatuses.Repaired, true, false, false, null)
                : new(StoreSidecarConvergenceStatuses.Current, false, false, false, null);
        }
        catch (Exception ex) when (IsConvergenceException(ex))
        {
            _logger.LogWarning(ex,
                "Family-store {SidecarKind} sidecar convergence failed; it will retry on the next convergence.",
                kind);
            phase.Fail(storeSequence);
            return FailedOutcome(ex);
        }
    }

    private void RecordConvergenceDetailSafely(string kind, SidecarConvergenceDetail detail)
    {
        try
        {
            _logger.LogInformation(
                "Family-store {SidecarKind} convergence used {ConvergencePath} because {ConvergenceReason}; did work: {DidWork}.",
                kind,
                detail.Path,
                detail.Reason,
                detail.DidWork);
        }
        catch
        {
        }
    }

    private StoreSidecarConvergenceOutcome ConvergeStoreVector(
        long storeSequence,
        IndexerPhaseScope phase)
    {
        if (!_vectorSignal.Enabled)
        {
            phase.Skip(storeSequence);
            return DisabledOutcome;
        }

        if (!_vectorDrainAvailable())
        {
            phase.Complete(storeSequence, false);
            return new(
                StoreSidecarConvergenceStatuses.LeaderRequired,
                false,
                true,
                true,
                "A resident vector drain is required to process the target.");
        }

        long previousTarget = _vectorSignal.TargetRevision;
        _vectorSignal.StampTarget(storeSequence, fullRebuild: false);
        long currentTarget = _vectorSignal.TargetRevision;
        bool targetChanged = currentTarget > previousTarget;
        phase.Complete(storeSequence, targetChanged);
        return targetChanged
            ? new(StoreSidecarConvergenceStatuses.Queued, true, true, false, null)
            : new(StoreSidecarConvergenceStatuses.Queued, false, true, false, "The resident vector drain already owns this target.");
    }

    private static StoreSidecarConvergenceOutcome DisabledOutcome { get; } =
        new(StoreSidecarConvergenceStatuses.Disabled, false, false, false, null);

    private static StoreSidecarConvergenceOutcome FailedOutcome(Exception ex) =>
        new(
            StoreSidecarConvergenceStatuses.Failed,
            false,
            true,
            false,
            BoundFailureReason(ex.Message));

    private static string BoundFailureReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "sidecar convergence failed";
        return reason.Length <= MaxFailureReasonLength
            ? reason
            : reason[..MaxFailureReasonLength];
    }

    // Metric-history cheap arm: append one source='converge' snapshot AFTER the sidecar converge steps, independent
    // of their success — the aggregates read symbols.db directly, not the sidecars. Best-effort by contract:
    // RecordConverge never throws and never blocks (skip-on-busy), so a history hiccup can never delay indexing.
    private MetricHistoryWriteResult? RecordConvergeHistory(string symbolsDbPath, string? workspaceId, long revision)
    {
        return MetricSnapshotAggregates.RecordConverge(
            symbolsDbPath,
            workspaceId,
            revision,
            MillerVersion.Current,
            onError: ex => _logger.LogWarning(
                ex, "Metric-history converge snapshot skipped; the trend will have a gap at revision {Revision}.",
                revision));
    }

    private bool ConvergeContentCorpus(
        string symbolsDbPath,
        string workspaceRoot,
        string? workspaceId,
        long revision,
        string? contentDbPath)
    {
        using var phase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Content);
        try
        {
            bool changed = _ensureContentBuilt(symbolsDbPath, workspaceRoot, workspaceId, revision);
            if (changed)
                _logger.LogInformation("Converged content corpus sidecar at revision {Revision}.", revision);
            phase.Complete(revision, changed);
            return changed;
        }
        catch (Exception ex) when (IsConvergenceException(ex))
        {
            bool recoveredDidWork = false;
            bool recovered = contentDbPath is not null && _tryRecover(
                ex,
                contentDbPath,
                () => recoveredDidWork = _ensureContentBuilt(symbolsDbPath, workspaceRoot, workspaceId, revision));
            if (!recovered)
            {
                _logger.LogWarning(ex,
                    "Content corpus sidecar convergence failed; source text search will remain unavailable or stale until the next successful convergence.");
                phase.Fail(revision);
                return false;
            }

            phase.Complete(revision, recoveredDidWork);
            return recoveredDidWork;
        }
    }

    private bool ConvergeSearch(
        string symbolsDbPath,
        string workspaceRoot,
        long revision,
        bool fullRebuild,
        string? searchDbPath)
    {
        using var phase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Search);
        try
        {
            bool changed = fullRebuild
                ? _ensureSearchBuilt(symbolsDbPath, revision, workspaceRoot, out string? corruptionReason)
                : _ensureSearchCurrent(symbolsDbPath, revision, workspaceRoot, out corruptionReason);
            // A corruption/malformed-meta rebuild must be visible; plain staleness convergence is normal operation.
            if (corruptionReason is not null)
                _logger.LogWarning(
                    "Search sidecar was corrupt and forced a full rebuild: {Reason}", corruptionReason);
            if (changed)
                _logger.LogInformation("Converged search sidecar at revision {Revision}.", revision);
            phase.Complete(revision, changed);
            return changed;
        }
        catch (Exception ex) when (IsConvergenceException(ex))
        {
            bool recoveredDidWork = false;
            bool recovered = searchDbPath is not null && _tryRecover(
                ex,
                searchDbPath,
                () => recoveredDidWork = _ensureSearchBuilt(symbolsDbPath, revision, workspaceRoot, out _));
            if (!recovered)
            {
                _logger.LogWarning(ex,
                    "Search sidecar convergence failed; the sidecar will remain unavailable or stale until the next successful convergence.");
                phase.Fail(revision);
                return false;
            }

            phase.Complete(revision, recoveredDidWork);
            return recoveredDidWork;
        }
    }

    private void RecordSkippedLegacyPhases(long? storeSequence)
    {
        _phaseSink.RecordSafely(IndexerPhaseNames.Content, TimeSpan.Zero, IndexerPhaseOutcomes.Skipped, storeSequence, false);
        _phaseSink.RecordSafely(IndexerPhaseNames.Search, TimeSpan.Zero, IndexerPhaseOutcomes.Skipped, storeSequence, false);
        _phaseSink.RecordSafely(IndexerPhaseNames.Metrics, TimeSpan.Zero, IndexerPhaseOutcomes.Skipped, storeSequence, false);
        _phaseSink.RecordSafely(IndexerPhaseNames.Vector, TimeSpan.Zero, IndexerPhaseOutcomes.Skipped, storeSequence, false);
    }

    private static string? TryResolveSidecarPath(string symbolsDbPath, Func<string, string> resolve)
    {
        try
        {
            return resolve(symbolsDbPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsConvergenceException(Exception ex) =>
        ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or TimeoutException or IncompatibleExtractException;
}
