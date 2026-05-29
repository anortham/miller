using Microsoft.Extensions.Logging;

namespace Miller.Server.Hosting;

/// <summary>
/// The production <see cref="IEditWriteThrough"/> (m6-design decision-6, impl-order step 9). After an apply it
/// asks the <see cref="IndexerService"/> to reindex each changed file inline IF this instance is the indexer
/// leader; otherwise the file write already emitted a FileSystemWatcher event the leader's M3 watcher
/// reconciles, so this is a no-op. Either path converges the index, and the next edit's freshness gate is the
/// ultimate backstop, so convergence is best-effort and never throws (a failure must not fail the
/// already-committed edit).
/// </summary>
public sealed class LeaderWriteThrough : IEditWriteThrough
{
    private readonly IndexerService _indexer;
    private readonly ILogger<LeaderWriteThrough> _logger;

    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public LeaderWriteThrough(IndexerService indexer, ILogger<LeaderWriteThrough> logger)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(logger);
        _indexer = indexer;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Converge(IReadOnlyList<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        foreach (string path in changedFiles)
        {
            bool reindexed = _indexer.TryReindexAsLeader(path);
            if (!reindexed)
            {
                // Not the leader (or the inline reindex declined): the watcher + freshness poll converge it.
                _logger.LogDebug(
                    "Write-through: {Path} not reindexed inline (not leader or inline reindex unavailable); " +
                    "relying on the watcher.", path);
            }
        }
    }
}
