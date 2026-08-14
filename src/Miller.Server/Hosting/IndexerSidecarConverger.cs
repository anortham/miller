using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Reads;

namespace Miller.Server.Hosting;

internal sealed class IndexerSidecarConverger
{
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
    private readonly Func<string, IWorkspaceReadSession, bool>? _ensureStoreContent;
    private readonly Func<string, IWorkspaceReadSession, bool>? _ensureStoreSearch;
    private readonly IIndexerPhaseSink _phaseSink;

    public IndexerSidecarConverger(
        SymbolSearchSidecar searchSidecar,
        ContentCorpusSidecar contentSidecar,
        ILogger logger,
        VectorConvergeSignal? vectorSignal = null,
        IIndexerPhaseSink? phaseSink = null)
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
            contentSidecar.EnsureStoreCurrent,
            searchSidecar.EnsureStoreCurrent,
            phaseSink)
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
        Func<string, IWorkspaceReadSession, bool>? ensureStoreContent = null,
        Func<string, IWorkspaceReadSession, bool>? ensureStoreSearch = null,
        IIndexerPhaseSink? phaseSink = null)
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
            bool vectorDidWork = currentTarget > previousTarget;
            didWork |= vectorDidWork;
            if (_vectorSignal.Enabled)
                vectorPhase.Complete(revision, vectorDidWork);
            else
                vectorPhase.Skip(revision);
        }
        totalPhase.Complete(revision, didWork);
    }

    public void ConvergeStore(string storeRoot, IWorkspaceReadSession session)
    {
        using var totalPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.SidecarTotal);
        bool didWork = false;
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentNullException.ThrowIfNull(session);
        if (session.Snapshot.Mode != WorkspaceReadMode.FamilyStore)
            throw new ArgumentException("Store sidecar convergence requires a family-store read session.", nameof(session));

        long target = session.Snapshot.Freshness.StoreLogSequence
            ?? throw new InvalidOperationException("The family-store snapshot has no store_log sequence.");
        bool contentRecorded = false;
        bool searchRecorded = false;
        try
        {
            using (FamilyStoreSidecarWriteLease.AcquireFor(storeRoot))
            {
                if (_ensureStoreContent is not null)
                {
                    didWork |= ConvergeStoreSidecar(
                        IndexerPhaseNames.Content,
                        () => _ensureStoreContent(storeRoot, session),
                        target);
                    contentRecorded = true;
                }
                else
                {
                    _phaseSink.RecordSafely(IndexerPhaseNames.Content, TimeSpan.Zero, IndexerPhaseOutcomes.Skipped, target, false);
                    contentRecorded = true;
                }

                if (_searchEnabled && _ensureStoreSearch is not null)
                {
                    didWork |= ConvergeStoreSidecar(
                        IndexerPhaseNames.Search,
                        () => _ensureStoreSearch(storeRoot, session),
                        target);
                    searchRecorded = true;
                }
                else
                {
                    _phaseSink.RecordSafely(IndexerPhaseNames.Search, TimeSpan.Zero, IndexerPhaseOutcomes.Skipped, target, false);
                    searchRecorded = true;
                }
            }
        }
        catch (Exception ex) when (IsConvergenceException(ex))
        {
            if (!contentRecorded)
                _phaseSink.RecordSafely(IndexerPhaseNames.Content, TimeSpan.Zero, IndexerPhaseOutcomes.Failed, target, false);
            if (!searchRecorded)
                _phaseSink.RecordSafely(IndexerPhaseNames.Search, TimeSpan.Zero, IndexerPhaseOutcomes.Failed, target, false);
            _logger.LogWarning(ex,
                "Family-store sidecar lease acquisition or release failed; derived sidecars will retry on the next convergence.");
        }

        using (var metricsPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Metrics))
        {
            MetricHistoryWriteResult? metrics = MetricSnapshotAggregates.RecordConverge(
                session,
                session.Snapshot.WorkspaceId,
                target,
                MillerVersion.Current,
                onError: ex => _logger.LogWarning(
                    ex,
                    "Metric-history store converge snapshot skipped; the trend will have a gap at store sequence {Sequence}.",
                    target));
            if (metrics is null)
                metricsPhase.Skip(target);
            else
            {
                bool metricsDidWork = metrics == MetricHistoryWriteResult.Recorded;
                didWork |= metricsDidWork;
                metricsPhase.Complete(target, metricsDidWork);
            }
        }

        using (var vectorPhase = new IndexerPhaseScope(_phaseSink, IndexerPhaseNames.Vector))
        {
            long previousTarget = _vectorSignal.TargetRevision;
            _vectorSignal.StampTarget(target, fullRebuild: false);
            long currentTarget = _vectorSignal.TargetRevision;
            bool vectorDidWork = currentTarget > previousTarget;
            didWork |= vectorDidWork;
            if (_vectorSignal.Enabled)
                vectorPhase.Complete(target, vectorDidWork);
            else
                vectorPhase.Skip(target);
        }
        totalPhase.Complete(target, didWork);
    }

    private bool ConvergeStoreSidecar(string kind, Func<bool> converge, long storeSequence)
    {
        using var phase = new IndexerPhaseScope(_phaseSink, kind);
        try
        {
            bool didWork = converge();
            phase.Complete(storeSequence, didWork);
            return didWork;
        }
        catch (Exception ex) when (IsConvergenceException(ex))
        {
            _logger.LogWarning(ex,
                "Family-store {SidecarKind} sidecar convergence failed; it will retry on the next convergence.",
                kind);
            phase.Fail(storeSequence);
            return false;
        }
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
