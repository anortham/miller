using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Miller.Indexing;

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

    public IndexerSidecarConverger(
        SymbolSearchSidecar searchSidecar,
        ContentCorpusSidecar contentSidecar,
        ILogger logger)
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
            logger)
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
        ILogger logger)
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
    }

    public void Converge(
        string? symbolsDbPath,
        string workspaceRoot,
        string? workspaceId,
        long revision,
        bool fullRebuild)
    {
        if (symbolsDbPath is null || revision <= 0)
            return;

        // Resolve the derived-artifact paths once for corrupt-escalation. A pathological symbols.db path simply
        // disables the escalation path; the converge calls below surface the path problem themselves.
        string? contentDbPath = TryResolveSidecarPath(symbolsDbPath, _contentDbPathFor);
        string? searchDbPath = TryResolveSidecarPath(symbolsDbPath, _searchDbPathFor);

        ConvergeContentCorpus(symbolsDbPath, workspaceRoot, workspaceId, revision, contentDbPath);
        if (_searchEnabled)
            ConvergeSearch(symbolsDbPath, workspaceRoot, revision, fullRebuild, searchDbPath);

        RecordConvergeHistory(symbolsDbPath, workspaceId, revision, searchDbPath);
    }

    // Metric-history cheap arm: append one source='converge' snapshot AFTER the sidecar converge steps, independent
    // of their success — the aggregates read symbols.db directly, not the sidecars. Best-effort by contract:
    // RecordConverge never throws and never blocks (skip-on-busy), so a history hiccup can never delay indexing.
    // The marker metric rides the region search index just converged into search.db (opened best-effort below);
    // when it is unavailable (search disabled, stale, or region tables absent) the marker metric is simply absent.
    private void RecordConvergeHistory(string symbolsDbPath, string? workspaceId, long revision, string? searchDbPath)
    {
        MetricSnapshotAggregates.RecordConverge(
            symbolsDbPath,
            workspaceId,
            revision,
            MillerVersion.Current,
            TryOpenRegionIndex(searchDbPath, revision),
            onError: ex => _logger.LogWarning(
                ex, "Metric-history converge snapshot skipped; the trend will have a gap at revision {Revision}.",
                revision));
    }

    // Open the region search index just built into search.db, so the converge snapshot can carry marker_total.
    // FtsRegionSearchIndex.Open THROWS on any unavailability (missing/stale search.db, region tables absent when
    // region search is disabled) — never returns null — so every failure degrades cleanly to "no region index" and
    // never affects converge. The index is not IDisposable (per-Search connections are Pooling=false and disposed).
    private IRegionSearchIndex? TryOpenRegionIndex(string? searchDbPath, long revision)
    {
        if (!_searchEnabled || searchDbPath is null)
            return null;

        try
        {
            return FtsRegionSearchIndex.Open(searchDbPath, revision);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or SqliteException or ArgumentException)
        {
            _logger.LogDebug(
                ex, "Region search index unavailable at converge; marker metric absent at revision {Revision}.",
                revision);
            return null;
        }
    }

    private void ConvergeContentCorpus(
        string symbolsDbPath,
        string workspaceRoot,
        string? workspaceId,
        long revision,
        string? contentDbPath)
    {
        try
        {
            if (_ensureContentBuilt(symbolsDbPath, workspaceRoot, workspaceId, revision))
                _logger.LogInformation("Converged content corpus sidecar at revision {Revision}.", revision);
        }
        catch (Exception ex) when (IsConvergenceException(ex))
        {
            if (contentDbPath is null || !_tryRecover(
                    ex,
                    contentDbPath,
                    () => _ensureContentBuilt(symbolsDbPath, workspaceRoot, workspaceId, revision)))
            {
                _logger.LogWarning(ex,
                    "Content corpus sidecar convergence failed; source text search will remain unavailable or stale until the next successful convergence.");
            }
        }
    }

    private void ConvergeSearch(
        string symbolsDbPath,
        string workspaceRoot,
        long revision,
        bool fullRebuild,
        string? searchDbPath)
    {
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
        }
        catch (Exception ex) when (IsConvergenceException(ex))
        {
            if (searchDbPath is null || !_tryRecover(
                    ex,
                    searchDbPath,
                    () => _ensureSearchBuilt(symbolsDbPath, revision, workspaceRoot, out _)))
            {
                _logger.LogWarning(ex,
                    "Search sidecar convergence failed; the sidecar will remain unavailable or stale until the next successful convergence.");
            }
        }
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
            or ArgumentException or NotSupportedException or IncompatibleExtractException;
}
