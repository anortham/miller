using Microsoft.Extensions.Logging;
using Miller.Server.Workspaces;

namespace Miller.Server.Hosting;

/// <summary>
/// The production <see cref="IEditWriteThrough"/> (m6-design decision-6, impl-order step 9). After an apply it
/// asks the <see cref="IndexerService"/> to reindex each changed file inline IF this instance is the indexer
/// leader; otherwise it writes a single-file converge request the leader's debounce tick drains
/// (<see cref="LeaderScanRequestQueue"/>) — most real sessions are readers, so without the request transport a
/// reader's convergence would wait on the watcher debounce alone. Either path converges the index, and the next
/// edit's freshness gate is the ultimate backstop, so convergence is best-effort and never throws (a failure
/// must not fail the already-committed edit).
/// </summary>
public sealed class LeaderWriteThrough : IEditWriteThrough
{
    private readonly IndexerService _indexer;
    private readonly IndexBootstrapService _bootstrap;
    private readonly ILogger<LeaderWriteThrough> _logger;

    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public LeaderWriteThrough(IndexerService indexer, IndexBootstrapService bootstrap, ILogger<LeaderWriteThrough> logger)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);
        _indexer = indexer;
        _bootstrap = bootstrap;
        _logger = logger;
    }

    /// <inheritdoc/>
    public StaleRecoveryAttempt TryRecoverStaleFile(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (_indexer.TryReindexAsLeader(fullPath))
            return StaleRecoveryAttempt.Converged;
        return TryRequestFileConverge([fullPath])
            ? StaleRecoveryAttempt.Requested
            : StaleRecoveryAttempt.None;
    }

    /// <inheritdoc/>
    public void Converge(IReadOnlyList<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        List<string>? pending = null;
        foreach (string path in changedFiles)
        {
            if (!_indexer.TryReindexAsLeader(path))
                (pending ??= []).Add(path);
        }

        // Not the leader (or the inline reindex declined): hand the leader ONE converge request for the batch;
        // its next debounce tick services it. If even that fails, the watcher + freshness poll remain the backstop.
        if (pending is not null && !TryRequestFileConverge(pending))
        {
            _logger.LogDebug(
                "Write-through: {Count} file(s) not reindexed inline and no converge request written; " +
                "relying on the watcher.", pending.Count);
        }
    }

    private bool TryRequestFileConverge(IReadOnlyList<string> fullPaths)
    {
        try
        {
            WorkspaceContext workspace = _bootstrap.Workspace;
            string? millerDir = Path.GetDirectoryName(workspace.ExtractDbPath);
            if (string.IsNullOrEmpty(millerDir))
                return false;
            LeaderScanRequestQueue.RequestFileConverge(millerDir, workspace.WorkspaceId ?? "unknown", fullPaths);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Write-through: could not write a file-converge request; relying on the watcher.");
            return false;
        }
    }
}
