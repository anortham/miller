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
