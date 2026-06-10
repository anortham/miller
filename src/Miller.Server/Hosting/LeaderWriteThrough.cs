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
    private readonly Func<LeaderIdentity, bool> _isLeaderAlive;

    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public LeaderWriteThrough(IndexerService indexer, IndexBootstrapService bootstrap, ILogger<LeaderWriteThrough> logger)
        : this(indexer, bootstrap, logger, isLeaderAlive: null)
    {
    }

    /// <summary>Test seam: <paramref name="isLeaderAlive"/> replaces the real process-liveness probe (null = real),
    /// same parameter-injection precedent as <see cref="LeaderIdentityFile.IsProcessAlive(LeaderIdentity)"/>'s.</summary>
    internal LeaderWriteThrough(
        IndexerService indexer,
        IndexBootstrapService bootstrap,
        ILogger<LeaderWriteThrough> logger,
        Func<LeaderIdentity, bool>? isLeaderAlive)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);
        _indexer = indexer;
        _bootstrap = bootstrap;
        _logger = logger;
        _isLeaderAlive = isLeaderAlive ?? LeaderIdentityFile.IsProcessAlive;
    }

    /// <inheritdoc/>
    public StaleRecoveryAttempt TryRecoverStaleFile(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (_indexer.TryReindexAsLeader(fullPath))
            return StaleRecoveryAttempt.Converged;
        // Gate-time recovery is the path whose caller BLOCKS on the outcome (EditService polls the gate for up
        // to its whole recovery budget after a Requested). Burn that budget only when a leader that can actually
        // drain the request is alive right now; otherwise refuse immediately so the gate falls back to its
        // existing stale message instead of a guaranteed-fruitless multi-second poll (M2-writer).
        if (!LiveLeaderCanServiceConvergeRequests())
            return StaleRecoveryAttempt.None;
        return TryRequestFileConverge([fullPath])
            ? StaleRecoveryAttempt.Requested
            : StaleRecoveryAttempt.None;
    }

    // Whether a live, converge-capable leader is recorded for this workspace. Capability note: leader.json
    // itself (ea023cc) shipped AFTER the file-converge drain (fa78c49), and a version-number floor cannot tell a
    // pre-drain 0.3.6 release build from a post-drain 0.3.6 dev build (the informational version differs only by
    // git SHA) — so "this leader writes an identity file at all" IS the protocol-capability signal, and no
    // version comparison is attempted. Liveness/absence is the high-value check; probe-unknown collapses to
    // "assume capable" so a diagnostics hiccup never regresses the happy path.
    private bool LiveLeaderCanServiceConvergeRequests()
    {
        try
        {
            string? millerDir = Path.GetDirectoryName(_bootstrap.Workspace.ExtractDbPath);
            if (string.IsNullOrEmpty(millerDir))
                return false;

            LeaderIdentity? identity = LeaderIdentityFile.TryRead(millerDir);
            if (identity is null)
            {
                _logger.LogDebug(
                    "Gate-time recovery: no leader identity is recorded (no leader, or a pre-identity build " +
                    "leads); refusing without a converge request.");
                return false;
            }

            if (!_isLeaderAlive(identity))
            {
                _logger.LogDebug(
                    "Gate-time recovery: recorded leader pid {Pid} (version {Version}) is not alive; refusing " +
                    "without a converge request.", identity.Pid, identity.Version);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Gate-time recovery: leader liveness probe failed; assuming a capable leader.");
            return true; // probe-unknown ⇒ assume capable — never regress the happy path on a diagnostics error
        }
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
