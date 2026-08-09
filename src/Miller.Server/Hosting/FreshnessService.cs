using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Indexing.Reads;

// IndexBootstrapService lives in the Miller.Server namespace (M2).
using Miller.Server;

namespace Miller.Server.Hosting;

/// <summary>
/// The revision poller (m3-design decision-2/-5, §Components/3, implementation-order step 9). Runs in EVERY
/// instance (leader and readers alike). On an interval it reads the latest persisted revision + artifact
/// identity through a TRANSIENT <see cref="FreshnessReader"/> and, when the writer moved ahead or the artifact
/// file was replaced by a full rebuild, rebuilds a fresh <see cref="MillerRepositoryIndex"/> via
/// <see cref="IndexRebuilder"/> and atomically <see cref="IndexHolder.Swap"/>s it in (the pure decision lives
/// in <see cref="FreshnessPoller"/>). This is how a reader instance converges on the leader's writes — no IPC,
/// no daemon in the read path.
///
/// <para><b>The per-poll open is load-bearing.</b> A full (force) rebuild PROMOTES a fresh file over
/// <c>symbols.db</c> (<see cref="FullRebuildPromotion"/>, 2026-06-11 Eros field report #2). A long-lived
/// connection survives that rename holding an fd to the unlinked OLD inode, so every later poll would re-read
/// the dead artifact and freshness would silently freeze forever — the same trap that made every per-operation
/// read open <c>Pooling=false</c> (the 2026-06-11 Eros fleet finding). Opening per poll always reads the
/// CURRENT file; the open is microseconds against the 500ms cadence, and on Windows it also avoids pinning a
/// near-permanent handle that would block the promote's overwrite-move (SQLite opens without
/// FILE_SHARE_DELETE).</para>
///
/// <para>The holder is exposed (<see cref="LatestObservedRevision"/>) so the telemetry filter can compute the
/// coarse <c>index_fresh</c> signal; the leader could also poke an immediate poll after its own extract, but
/// the interval poll already converges within one tick, so M3 keeps it interval-only (no extra coupling).</para>
/// </summary>
public sealed class FreshnessService : BackgroundService
{
    // The poll cadence. Agent-speed: an agent edits and re-reads well under a second later, and a READER-served
    // session pays this poll twice (leader swap + reader swap), so it must stay inside the edit gate's recovery
    // budget — FreshnessLatencyBudgetTests pins the chain. Each poll is one cheap SQLite open + revision read.
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IndexBootstrapService _bootstrap;
    private readonly ILogger<FreshnessService> _logger;
    private readonly Func<bool> _storeEnabled;

    private string? _workspaceId;

    // Serializes a poll-then-swap so the loop tick and an on-demand PollNow never interleave their
    // read-decide-rebuild-swap sequences (two concurrent rebuilds of a 2GB index would double the work and race
    // the swap). The loop holds it for its tick; PollNow (the MCP tool thread behind `workspace refresh/full`)
    // holds it for its on-demand poll.
    private readonly object _pollGate = new();

    // The latest revision observed by the most recent successful poll. Cached so the per-tool-call index_fresh
    // probe does not run its own SQLite query on the hot path; refreshed every poll tick. -1 = not yet polled.
    private long _lastObservedRevision = -1;

    public FreshnessService(
        IndexBootstrapService bootstrap,
        ILogger<FreshnessService> logger,
        Func<bool>? storeEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);
        _bootstrap = bootstrap;
        _logger = logger;
        _storeEnabled = storeEnabled ?? WorkspaceReadSessionFactory.StoreEnabledFromEnvironment;
    }

    // The live holder, read LAZILY off the bootstrap. The host constructs this hosted service before
    // IndexBootstrapService.StartAsync runs, so the holder must NOT be injected in the constructor (that
    // resolves the holder-backed singleton eagerly and throws "Holder requested before bootstrap completed").
    // Every read below happens from ExecuteAsync's loop / PollNow / the probe — all after StartAsync seeded it.
    private IndexHolder Holder => _bootstrap.Holder;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _bootstrap.WaitUntilBoundAsync(stoppingToken).ConfigureAwait(false);
                int generation = _bootstrap.BindingGeneration;
                var workspace = _bootstrap.Workspace;
                _workspaceId = workspace.WorkspaceId;

                if (_workspaceId is null)
                {
                    // No workspace id => a never-scanned / static extract with no revision cursor to poll. The bootstrap
                    // already built the initial index; there is nothing to converge on. Idle until rebind or shutdown.
                    _logger.LogInformation(
                        "FreshnessService: no workspace_id; the index is static (no revision cursor to poll).");
                    await WaitForRebindOrCancelAsync(generation, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                Interlocked.Exchange(ref _lastObservedRevision, -1);

                while (!stoppingToken.IsCancellationRequested && generation == _bootstrap.BindingGeneration)
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                    PollAndSwap(workspace.ExtractDbPath);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task WaitForRebindOrCancelAsync(int generation, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && generation == _bootstrap.BindingGeneration)
            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The latest revision the most recent poll observed, or the holder's built revision before the first poll
    /// (so <c>index_fresh</c> reads "fresh" at startup rather than "unknown-but-stale"). Cheap cached read for
    /// the per-call <see cref="IndexFreshProbe"/> — no SQLite on the tool hot path.
    /// </summary>
    public long LatestObservedRevision =>
        Interlocked.Read(ref _lastObservedRevision) is var rev && rev >= 0 ? rev : Holder.BuiltRevision;

    /// <summary>
    /// Run the poll-then-swap ONCE on demand and return the typed outcome immediately (M7 decision-3) — the
    /// trigger behind <c>workspace refresh/full</c>, so the in-memory index ends current without waiting up to
    /// one poll tick for the loop. Builds the same transient reader/rebuilder the loop tick uses, serialized
    /// against the tick via the poll gate, so it works regardless of whether the hosted loop ever started.
    /// When the workspace has no <c>workspace_id</c> (a never-scanned / static extract) there is no revision
    /// cursor to poll, so it honestly returns not-swapped at the held revision. Fully best-effort: any
    /// read/rebuild failure (a mid-write WAL hiccup, a momentarily-locked DB) is logged and reported as
    /// not-swapped at the held revision — it NEVER throws into the caller (the tool).
    /// </summary>
    public PollResult PollNow()
    {
        // The workspace id is the poll's WHERE-clause key. Resolve from the loop's cached id if running, else
        // from the bootstrap workspace (the loop may never have started — a non-leader, or a pre-first-tick call).
        string? workspaceId = _workspaceId ?? _bootstrap.Workspace.WorkspaceId;
        if (workspaceId is null)
        {
            // No revision cursor exists (the extract was never scanned / is static). Nothing to converge on —
            // report not-swapped at the held revision rather than fabricate a convergence.
            return new PollResult(Swapped: false, Revision: Holder.BuiltRevision);
        }

        lock (_pollGate)
        {
            try
            {
                return PollThenSwap(_bootstrap.Workspace.ExtractDbPath);
            }
            catch (Exception ex)
            {
                // On-demand poll is best-effort: keep the prior index and report not-swapped at the held
                // revision; never throw into the tool (the loop/next poll reconciles).
                _logger.LogWarning(ex, "On-demand freshness poll failed; keeping the prior index.");
                return new PollResult(Swapped: false, Revision: Holder.BuiltRevision);
            }
        }
    }

    // The loop's per-tick poll. Serialized against PollNow via _pollGate so two poll-then-swap sequences never
    // interleave. Read/rebuild failures are kept-and-retried, never crashing the loop (decision-10).
    private void PollAndSwap(string dbPath)
    {
        lock (_pollGate)
        {
            try
            {
                PollThenSwap(dbPath);
            }
            catch (Exception ex)
            {
                // A transient read/rebuild failure (a mid-write WAL hiccup, a momentarily-locked DB, the brief
                // promote window of a full rebuild) must keep the prior index and retry next tick — never crash
                // the poll loop (decision-10).
                _logger.LogWarning(ex, "Freshness poll failed; keeping the prior index and retrying next tick.");
            }
        }
    }

    // The shared poll-then-swap core (the loop and PollNow both route through it): open a transient reader on
    // the CURRENT file (see the class doc — a long-lived connection would freeze on a promoted rebuild's old
    // inode), read the latest persisted revision + artifact identity, cache the revision for the index_fresh
    // probe, and rebuild+swap iff the writer moved ahead or the artifact was replaced. Returns the typed
    // outcome. Callers hold _pollGate; exceptions propagate to the caller's own keep-prior-index handling.
    // v1's extraction_revisions has no workspace_id (one DB = one root), so LatestRevision takes no key — the
    // _workspaceId gate decides WHETHER to poll (a static extract has none), not the SQL filter.
    private PollResult PollThenSwap(string dbPath)
    {
        var holder = Holder;
        long built = holder.BuiltRevision;
        WorkspaceContext workspace = _bootstrap.Workspace;
        string workspaceRoot = workspace.CanonicalRoot ?? workspace.WorkspaceRoot;
        bool storeEnabled = _storeEnabled();
        long latest;
        string? artifactId;
        using (WorkspaceReadHandle reader = WorkspaceReadSessionFactory.Open(
                   dbPath,
                   workspaceRoot,
                   workspace.WorkspaceId,
                   storeEnabled))
        {
            latest = reader.Snapshot.Freshness.StoreLogSequence ?? reader.Snapshot.Freshness.Revision;
            artifactId = reader.Snapshot.IndexIdentity;
        }
        Interlocked.Exchange(ref _lastObservedRevision, latest);

        // M8 §D4: the observed-vs-built comparison every poll makes — at Debug so it costs nothing at the default
        // level, but it is exactly the line that explains why a poll did (or did not) swap.
        _logger.LogDebug(
            "Freshness poll: observed revision {Observed} (artifact {Artifact}) vs built revision {Built}.",
            latest, artifactId, built);

        bool swapped = FreshnessPoller.PollOnce(holder, latest, artifactId, () =>
        {
            using WorkspaceReadHandle reader = WorkspaceReadSessionFactory.Open(
                dbPath,
                workspaceRoot,
                workspace.WorkspaceId,
                storeEnabled);
            return RepositoryIndexLoader.LoadSession(reader);
        });
        if (swapped)
            _logger.LogInformation("Freshness: rebuilt + swapped index to revision {Revision}.", latest);
        return new PollResult(swapped, latest);
    }
}

/// <summary>
/// The typed result of <see cref="FreshnessService.PollNow"/> (M7 decision-3): whether the on-demand poll
/// rebuilt + swapped the index (<see cref="Swapped"/>) and the revision the index now reflects
/// (<see cref="Revision"/> — the newly-swapped revision on a swap, else the latest observed / held revision).
/// </summary>
/// <param name="Swapped">True iff a newer revision was observed and the index was rebuilt + swapped.</param>
/// <param name="Revision">The revision the held index now reflects after the poll.</param>
public readonly record struct PollResult(bool Swapped, long Revision);
