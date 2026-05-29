using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;

// IndexBootstrapService lives in the Miller.Server namespace (M2).
using Miller.Server;

namespace Miller.Server.Hosting;

/// <summary>
/// The revision poller (m3-design decision-2/-5, §Components/3, implementation-order step 9). Runs in EVERY
/// instance (leader and readers alike). On an interval it reads the latest persisted revision from
/// <see cref="FreshnessReader.LatestRevision"/> and, when it exceeds the held index's built revision, rebuilds
/// a fresh <see cref="MillerRepositoryIndex"/> via <see cref="IndexRebuilder"/> and atomically
/// <see cref="IndexHolder.Swap"/>s it in (the pure decision lives in <see cref="FreshnessPoller"/>). This is how
/// a reader instance converges on the leader's writes — no IPC, no daemon in the read path.
///
/// <para>The holder is exposed (<see cref="IndexFresh"/>) so the telemetry filter can compute the coarse
/// <c>index_fresh</c> signal; the leader could also poke an immediate poll after its own extract, but the
/// interval poll already converges within one tick, so M3 keeps it interval-only (no extra coupling).</para>
/// </summary>
public sealed class FreshnessService : BackgroundService
{
    // The poll cadence. Short enough that an external edit converges quickly, long enough to be near-free.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IndexBootstrapService _bootstrap;
    private readonly IndexHolder _holder;
    private readonly ILogger<FreshnessService> _logger;

    private FreshnessReader? _reader;
    private IndexRebuilder? _rebuilder;
    private string? _workspaceId;

    // The latest revision observed by the most recent successful poll. Cached so the per-tool-call index_fresh
    // probe does not run its own SQLite query on the hot path; refreshed every poll tick. -1 = not yet polled.
    private long _lastObservedRevision = -1;

    public FreshnessService(
        IndexBootstrapService bootstrap, IndexHolder holder, ILogger<FreshnessService> logger)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(logger);
        _bootstrap = bootstrap;
        _holder = holder;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workspace = _bootstrap.Workspace;
        _workspaceId = workspace.WorkspaceId;

        if (_workspaceId is null)
        {
            // No workspace id => no canonical_revisions rows to poll (a never-scanned or empty extract). The
            // bootstrap already built the initial index; there is nothing to converge on. Idle.
            _logger.LogInformation(
                "FreshnessService: no workspace_id; the index is static (no revision cursor to poll).");
            return;
        }

        try
        {
            _reader = new FreshnessReader(workspace.ExtractDbPath);
            _rebuilder = new IndexRebuilder(workspace.ExtractDbPath);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                PollAndSwap();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            _reader?.Dispose();
            _reader = null;
        }
    }

    /// <summary>
    /// The latest revision the most recent poll observed, or the holder's built revision before the first poll
    /// (so <c>index_fresh</c> reads "fresh" at startup rather than "unknown-but-stale"). Cheap cached read for
    /// the per-call <see cref="IndexFreshProbe"/> — no SQLite on the tool hot path.
    /// </summary>
    public long LatestObservedRevision =>
        Interlocked.Read(ref _lastObservedRevision) is var rev && rev >= 0 ? rev : _holder.BuiltRevision;

    private void PollAndSwap()
    {
        try
        {
            long latest = _reader!.LatestRevision(_workspaceId!);
            Interlocked.Exchange(ref _lastObservedRevision, latest);
            bool swapped = FreshnessPoller.PollOnce(_holder, latest, _rebuilder!.Rebuild);
            if (swapped)
                _logger.LogInformation("Freshness: rebuilt + swapped index to revision {Revision}.", latest);
        }
        catch (Exception ex)
        {
            // A transient read/rebuild failure (a mid-write WAL hiccup, a momentarily-locked DB) must keep the
            // prior index and retry next tick — never crash the poll loop (decision-10).
            _logger.LogWarning(ex, "Freshness poll failed; keeping the prior index and retrying next tick.");
        }
    }
}
