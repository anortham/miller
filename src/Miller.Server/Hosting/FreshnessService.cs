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
    private readonly ILogger<FreshnessService> _logger;

    private FreshnessReader? _reader;
    private IndexRebuilder? _rebuilder;
    private string? _workspaceId;

    // Serializes a poll-then-swap so the loop tick and an on-demand PollNow never drive the single-connection
    // FreshnessReader concurrently (the reader is explicitly not safe for concurrent use). The loop holds it for
    // its tick; PollNow (the MCP tool thread behind `workspace refresh/full`) holds it for its on-demand poll.
    private readonly object _pollGate = new();

    // The latest revision observed by the most recent successful poll. Cached so the per-tool-call index_fresh
    // probe does not run its own SQLite query on the hot path; refreshed every poll tick. -1 = not yet polled.
    private long _lastObservedRevision = -1;

    public FreshnessService(IndexBootstrapService bootstrap, ILogger<FreshnessService> logger)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);
        _bootstrap = bootstrap;
        _logger = logger;
    }

    // The live holder, read LAZILY off the bootstrap. The host constructs this hosted service before
    // IndexBootstrapService.StartAsync runs, so the holder must NOT be injected in the constructor (that
    // resolves the holder-backed singleton eagerly and throws "Holder requested before bootstrap completed").
    // Every read below happens from ExecuteAsync's loop / PollNow / the probe — all after StartAsync seeded it.
    private IndexHolder Holder => _bootstrap.Holder;

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
            // Assign the single-connection reader/rebuilder UNDER _pollGate: the same lock PollNow takes to read
            // them. The lock — not just volatility — is load-bearing on the disposal side (below), so we keep the
            // whole lifecycle (assign + dispose) on one gate for a clean happens-before edge.
            lock (_pollGate)
            {
                _reader = new FreshnessReader(workspace.ExtractDbPath);
                _rebuilder = new IndexRebuilder(workspace.ExtractDbPath);
            }

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
            DisposeReader();
        }
    }

    // Dispose the long-lived reader UNDER _pollGate so an in-flight PollNow (which holds the gate while driving
    // the reader) finishes its query before the connection is closed — closing the single SqliteConnection out
    // from under a live ExecuteScalar would free the native handle mid-query. The gate makes the dispose wait.
    private void DisposeReader()
    {
        lock (_pollGate)
        {
            _reader?.Dispose();
            _reader = null;
            _rebuilder = null;
        }
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
    /// one 2s tick for the loop. Safe to call REGARDLESS of whether the hosted <see cref="ExecuteAsync"/> loop
    /// has initialised <see cref="_reader"/>/<see cref="_rebuilder"/>: if the loop is running, it reuses them
    /// (serialized against the tick via <see cref="_pollGate"/>); if not (a non-leader that idled, or before the
    /// loop's first tick), it builds a TRANSIENT reader/rebuilder from the workspace, polls, and disposes the
    /// transient reader. When the workspace has no <c>workspace_id</c> (a never-scanned / static extract) there
    /// is no revision cursor to poll, so it honestly returns not-swapped at the held revision. Fully best-effort:
    /// any read/rebuild failure (a mid-write WAL hiccup, a momentarily-locked DB) is logged and reported as
    /// not-swapped at the held revision — it NEVER throws into the caller (the tool).
    /// </summary>
    public PollResult PollNow()
    {
        long held = Holder.BuiltRevision;

        // The workspace id is the poll's WHERE-clause key. Resolve from the loop's cached id if running, else
        // from the bootstrap workspace (the loop may never have started — a non-leader, or a pre-first-tick call).
        string? workspaceId = _workspaceId ?? _bootstrap.Workspace.WorkspaceId;
        if (workspaceId is null)
        {
            // No revision cursor exists (the extract was never scanned / is static). Nothing to converge on —
            // report not-swapped at the held revision rather than fabricate a convergence.
            return new PollResult(Swapped: false, Revision: held);
        }

        lock (_pollGate)
        {
            try
            {
                // Reuse the loop's long-lived reader/rebuilder when the hosted loop has initialised them;
                // otherwise build transient ones from the workspace for this single on-demand poll.
                if (_reader is { } reader && _rebuilder is { } rebuilder)
                    return PollThenSwap(reader, rebuilder, workspaceId);

                string dbPath = _bootstrap.Workspace.ExtractDbPath;
                using var transientReader = new FreshnessReader(dbPath);
                var transientRebuilder = new IndexRebuilder(dbPath);
                return PollThenSwap(transientReader, transientRebuilder, workspaceId);
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

    /// <summary>
    /// Test seam: initialise the long-lived <see cref="_reader"/>/<see cref="_rebuilder"/> from the bootstrap
    /// workspace UNDER <see cref="_pollGate"/>, exactly as <see cref="ExecuteAsync"/> does, so a test can exercise
    /// the reader-lifecycle serialization without running the timer loop. Not used in production.
    /// </summary>
    internal void InitReaderForTest()
    {
        string dbPath = _bootstrap.Workspace.ExtractDbPath;
        lock (_pollGate)
        {
            _reader = new FreshnessReader(dbPath);
            _rebuilder = new IndexRebuilder(dbPath);
        }
    }

    /// <summary>
    /// Test seam: dispose the long-lived reader through the SAME <see cref="DisposeReader"/> path
    /// <see cref="ExecuteAsync"/>'s finally uses, so a test can prove the disposal serializes against an in-flight
    /// poll holding <see cref="_pollGate"/> (the dispose-vs-read race fix). Not used in production.
    /// </summary>
    internal void DisposeReaderForTest() => DisposeReader();

    /// <summary>
    /// Test seam: run <paramref name="action"/> while holding <see cref="_pollGate"/>, standing in for an
    /// in-flight <see cref="PollNow"/> that holds the gate while it drives the reader. Lets a test assert a
    /// concurrent <see cref="DisposeReaderForTest"/> cannot close the reader until the gate is released. Not used
    /// in production.
    /// </summary>
    internal void RunUnderPollGateForTest(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_pollGate)
            action();
    }

    // The loop's per-tick poll. Serialized against PollNow via _pollGate so the single-connection reader is
    // never driven concurrently. Read/rebuild failures are kept-and-retried, never crashing the loop (decision-10).
    private void PollAndSwap()
    {
        lock (_pollGate)
        {
            try
            {
                PollThenSwap(_reader!, _rebuilder!, _workspaceId!);
            }
            catch (Exception ex)
            {
                // A transient read/rebuild failure (a mid-write WAL hiccup, a momentarily-locked DB) must keep
                // the prior index and retry next tick — never crash the poll loop (decision-10).
                _logger.LogWarning(ex, "Freshness poll failed; keeping the prior index and retrying next tick.");
            }
        }
    }

    // The shared poll-then-swap core (the loop and PollNow both route through it): read the latest persisted
    // revision, cache it for the index_fresh probe, and rebuild+swap iff the writer has moved ahead. Returns the
    // typed outcome. Callers hold _pollGate; exceptions propagate to the caller's own keep-prior-index handling.
    private PollResult PollThenSwap(FreshnessReader reader, IndexRebuilder rebuilder, string workspaceId)
    {
        var holder = Holder;
        long built = holder.BuiltRevision;
        long latest = reader.LatestRevision(workspaceId);
        Interlocked.Exchange(ref _lastObservedRevision, latest);

        // M8 §D4: the observed-vs-built comparison every poll makes — at Debug so it costs nothing at the default
        // level, but it is exactly the line that explains why a poll did (or did not) swap.
        _logger.LogDebug(
            "Freshness poll: observed revision {Observed} vs built revision {Built}.", latest, built);

        bool swapped = FreshnessPoller.PollOnce(holder, latest, rebuilder.Rebuild);
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
