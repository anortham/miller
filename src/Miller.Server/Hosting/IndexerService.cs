using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server.Logging;
using Miller.Server.Workspaces;

// IndexBootstrapService + WorkspaceContext live in the Miller.Server namespace (M2).
using Miller.Server;

namespace Miller.Server.Hosting;

/// <summary>
/// The leader-gated file watcher (m3-design decision-1, §Components/3, implementation-order step 9). On start
/// each <c>miller</c> instance tries to acquire the cross-process <see cref="SingleWriterLock"/> for the
/// workspace. The winner is the LEADER: it attaches a <see cref="FileSystemWatcher"/> on the CANONICAL root
/// (recursive, language-agnostically filtered via <see cref="WatchPathFilter"/>) plus a watch on
/// <c>.git/HEAD</c>, coalesces events into <see cref="IndexerCore"/>'s <see cref="WatchEventQueue"/>, and on a
/// ~1s debounce tick drains → routes → calls <c>extract update/delete/scan</c> (canonical paths, one in-flight
/// subprocess). The FSW <c>Error</c> (InternalBuffer overflow) forces a rescan. A non-leader instance idles and
/// periodically re-tries the lock so it can take over if the leader dies (failover).
///
/// <para>Pure logic lives in <see cref="IndexerCore"/> / Core (coalesce, route, dispatch) and is unit-tested;
/// this class is the thin infra shell (FSW, timer, lock, .git/HEAD) exercised by the live Scale suite.</para>
/// </summary>
public sealed class IndexerService : BackgroundService
{
    // The debounce tick: collect a burst, then drain once (decision §Components/3). Originally ~1s (julie's
    // tick); tightened to agent speed — an agent edits and re-reads in well under a second, and this tick is the
    // first leg of the read-your-writes chain FreshnessLatencyBudgetTests pins. Still coalesces save-storms.
    internal static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(250);

    // How often a non-leader re-tries the writer lock so it can take over after the leader exits (failover).
    private static readonly TimeSpan DefaultLeaderRetryInterval = TimeSpan.FromSeconds(5);

    private readonly IndexBootstrapService _bootstrap;
    private readonly ILogger<IndexerService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<WorkspaceContext, string, string, IExtractOps> _createOps;
    private readonly Func<string, FullScanDrainResult> _drainFullScanRequests;
    private readonly Func<string, FileConvergeDrainResult> _drainFileConvergeRequests;

    // --- version-aware leadership (D2–D4): every input is an injected func so the orchestration is pure-testable.
    // Decisions live in LeadershipEligibility / YieldCooldown; this class only wires them into the claim loop,
    // the debounce tick, and the reader retry tick.
    private readonly Func<WorkspaceContext, IReadOnlySet<string>?> _fetchSupportedExtensions;
    private readonly IndexerLeadershipCoordinator _leadership;

    // julie's claimed extension set for the watcher gate (null = gate nothing, the fail-soft default).
    // Fetched ONCE per leadership claim, BEFORE the watchers attach, so every event handler observes the
    // final value (the fetch itself is process-cached in SupportedExtensionCatalog).
    private IReadOnlySet<string>? _supportedExtensions;
    // M4 log throttle: the first request that cannot be claimed warns (something is pinning a request file);
    // repeats on later ticks drop to Debug so a wedged file cannot spam a warning every 250ms.
    private bool _requestClaimSkipWarned;
    private readonly TimeSpan _leaderRetryInterval;
    private readonly bool _attachFileWatchers;

    // Current-workspace sidecar convergence. THIS instance — the writer-lock leader — is the one safe writer for
    // the CURRENT workspace's content.db/search.db, so convergence runs after scans and per-file updates under
    // _opsGate.
    private readonly IndexerSidecarConverger _sidecarConverger;
    private readonly WorkspaceRegistryScanPublisher _registryPublisher;

    private volatile IDisposable? _lease;
    private IndexerWatcherSet? _watchers;
    private IndexerCore? _core;
    private string? _currentMillerDir;

    // The leader's extract ops, set once leadership is won (null on a non-leader). M6 write-through reaches
    // through TryReindexAsLeader to converge the index inline after an apply; guarded by _opsGate so an edit on
    // the MCP thread never races the debounce-loop drain (julie tolerates one in-flight subprocess, but we keep
    // Miller's own calls serialized regardless).
    private IExtractOps? _ops;
    private readonly object _opsGate = new();

    // .git/HEAD changes (branch switch / checkout) are folded into ONE forced scan per drain rather than
    // drowning in the per-file storm a checkout produces (decision-7). Set by the HEAD watcher, read+reset on
    // the next drain under the lock below.
    private volatile bool _headChanged;
    private readonly object _headGate = new();

    public IndexerService(
        IndexBootstrapService bootstrap, ILogger<IndexerService> logger, ILoggerFactory loggerFactory,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar? contentSidecar = null)
        : this(
            bootstrap,
            logger,
            loggerFactory,
            static millerDir => SingleWriterLock.TryAcquire(millerDir),
            static (workspace, canonicalRoot, canonicalDbPath) =>
            {
                var runner = JulieExtractRunner.Locate(workspace.ToolsRoot);
                return JulieExtractOps.Create(canonicalRoot, canonicalDbPath, runner);
            },
            DefaultLeaderRetryInterval,
            sidecar,
            contentSidecar,
            attachFileWatchers: true,
            drainFullScanRequests: LeaderScanRequestQueue.DrainFullScanRequests,
            drainFileConvergeRequests: LeaderScanRequestQueue.DrainFileConvergeRequests,
            // The watcher's extension gate: julie's own claimed set, fetched once per process, null on any
            // failure (gate nothing). Production-only — the internal test ctor defaults to no gate so no
            // fast test can ever spawn the languages probe.
            fetchSupportedExtensions: static workspace =>
                SupportedExtensionCatalog.ForToolsRoot(workspace.ToolsRoot))
    {
    }

    internal IndexerService(
        IndexBootstrapService bootstrap,
        ILogger<IndexerService> logger,
        ILoggerFactory loggerFactory,
        Func<string, IDisposable?> tryAcquireLeadership,
        Func<WorkspaceContext, string, string, IExtractOps> createOps,
        TimeSpan leaderRetryInterval,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar? contentSidecar = null,
        bool attachFileWatchers = true,
        Func<string, FullScanDrainResult>? drainFullScanRequests = null,
        Func<string, FileConvergeDrainResult>? drainFileConvergeRequests = null,
        Func<string, YieldDrainResult>? drainYieldRequests = null,
        Func<string?>? ownExtractorVersion = null,
        bool? allowExtractorDowngrade = null,
        Func<string?, string?>? readArtifactExtractorVersion = null,
        Action<string, string, int, string>? requestYield = null,
        Func<string, LeaderIdentity?>? readLeaderIdentity = null,
        Func<LeaderIdentity, bool>? leaderAliveProbe = null,
        Func<DateTimeOffset>? clock = null,
        Func<int, DateTimeOffset?, bool>? processAliveProbe = null,
        Func<WorkspaceContext, IReadOnlySet<string>?>? fetchSupportedExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(tryAcquireLeadership);
        ArgumentNullException.ThrowIfNull(createOps);
        ArgumentNullException.ThrowIfNull(sidecar);
        if (leaderRetryInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(leaderRetryInterval), leaderRetryInterval, "Leader retry interval must be positive.");
        _bootstrap = bootstrap;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _createOps = createOps;
        _drainFullScanRequests = drainFullScanRequests ?? LeaderScanRequestQueue.DrainFullScanRequests;
        _drainFileConvergeRequests = drainFileConvergeRequests ?? LeaderScanRequestQueue.DrainFileConvergeRequests;
        _leaderRetryInterval = leaderRetryInterval;
        _sidecarConverger = new IndexerSidecarConverger(
            sidecar,
            contentSidecar ?? new ContentCorpusSidecar(),
            logger);
        _registryPublisher = new WorkspaceRegistryScanPublisher(logger);
        _attachFileWatchers = attachFileWatchers;
        // Lazy so the production probe (which reads the bootstrap's workspace for ToolsRoot) runs inside
        // ExecuteAsync, never in this constructor — the host constructs every hosted service before ANY
        // bootstrap StartAsync runs (the load-bearing host-lifecycle rule).
        var leadershipClock = clock ?? (static () => DateTimeOffset.UtcNow);
        _leadership = new IndexerLeadershipCoordinator(
            logger,
            tryAcquireLeadership,
            ownExtractorVersion ?? ProbeBundledExtractorVersion,
            allowExtractorDowngrade
                ?? Environment.GetEnvironmentVariable("MILLER_ALLOW_EXTRACTOR_DOWNGRADE") == "1",
            readArtifactExtractorVersion ?? ExtractBinaryVersionReader.TryRead,
            drainYieldRequests ?? LeaderScanRequestQueue.DrainYieldRequests,
            requestYield ?? LeaderScanRequestQueue.RequestYield,
            readLeaderIdentity ?? LeaderIdentityFile.TryRead,
            leaderAliveProbe ?? (static identity => LeaderIdentityFile.IsProcessAlive(identity)),
            leadershipClock,
            processAliveProbe ?? LeaderIdentityFile.IsProcessAlive);
        // Default = NO gate (null set). Unit tests reach this ctor; the gate's process probe must never run
        // in the fast suite, so only the public production ctor binds the real catalog fetch.
        _fetchSupportedExtensions = fetchSupportedExtensions ?? (static _ => null);
    }

    /// <summary>True once this instance holds the writer lock and is running the watcher. For diagnostics/tests.</summary>
    public bool IsLeader => _lease is not null;

    /// <summary>
    /// The most recent D2 eligibility verdict the claim loop evaluated (null until the first evaluation).
    /// Surfaced for status/health rendering: a permanent reader can say WHY it will never index
    /// ("extractor 2.1.3 is older than the index artifact 2.3.0") instead of looking mysteriously idle.
    /// Reference assignment is atomic; readers may observe it from other threads.
    /// </summary>
    internal LeadershipVerdict? EligibilityVerdict => _leadership.EligibilityVerdict;

    /// <summary>
    /// This instance's probed bundled-extractor version, surfaced for status/health rendering. Reads the lazy
    /// WITHOUT forcing it (null until the claim loop's first eligibility evaluation) so a tool call can never
    /// trigger the subprocess probe itself.
    /// </summary>
    internal string? OwnExtractorVersion => _leadership.OwnExtractorVersion;

    /// <summary>
    /// Whether the coalescing queue and forced-rescan flag currently hold no pending work — the second half of
    /// <c>index_fresh</c> (decision-8). A non-leader instance has no watcher/queue, so it is vacuously empty
    /// (true); a leader reports its live pending-work state. Read by <see cref="IndexFreshProbe"/>.
    /// </summary>
    public bool QueueEmpty => _core is null || !_core.HasPendingWork;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _bootstrap.WaitUntilBoundAsync(stoppingToken).ConfigureAwait(false);
                int generation = _bootstrap.BindingGeneration;

                var workspace = _bootstrap.Workspace;
                string canonicalRoot = workspace.CanonicalRoot
                    ?? throw new InvalidOperationException(
                        "IndexerService started before the bootstrap resolved the canonical root.");
                string canonicalDbPath = workspace.CanonicalExtractDbPath
                    ?? throw new InvalidOperationException(
                        "IndexerService started before the bootstrap resolved the canonical extract DB path.");
                string millerDir = Path.GetDirectoryName(workspace.ExtractDbPath)!;
                _currentMillerDir = millerDir;

                // The inner loop exists for the D4 yield protocol: a leader that abdicates to a newer-extractor
                // challenger falls back into the claim loop as a reader (under the anti-flap cooldown) instead of
                // exiting. It also restarts when the primary workspace is rebound via MCP roots/list_changed.
                while (!stoppingToken.IsCancellationRequested && generation == _bootstrap.BindingGeneration)
                {
                    if (!await RunLeadershipSessionAsync(
                        workspace, canonicalRoot, canonicalDbPath, millerDir, generation, stoppingToken).ConfigureAwait(false))
                    {
                        if (generation == _bootstrap.BindingGeneration)
                            return; // normal shutdown
                        break; // primary workspace rebound mid-session
                    }
                }

                if (stoppingToken.IsCancellationRequested)
                    return;

                StepDownLeadership(millerDir);
                _logger.LogInformation("Primary workspace rebound; restarting indexer for the new root.");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            StepDownLeadership(_currentMillerDir);
        }
    }

    private void StepDownLeadership(string? millerDir)
    {
        DisposeWatchers();
        lock (_opsGate)
            _ops = null; // stop offering inline write-through once we step down
        if (_lease is not null)
        {
            _logger.LogDebug("Indexer stepping down: releasing the writer lock.");
            MillerRole.SetReader();
            if (millerDir is not null)
            {
                // Remove our identity BEFORE releasing the lock so no successor can have written its own file
                // yet — a graceful step-down must not leave a stale "leader" for health to probe.
                LeaderIdentityFile.TryDelete(millerDir);
            }
        }
        _lease?.Dispose();
        _lease = null;
    }

    /// <summary>
    /// One full leadership session: claim loop (reader until the lock is won) → leader setup → debounce loop.
    /// Returns true when the leader ABDICATED to a newer-extractor challenger (the caller re-enters as a
    /// reader); false on cancellation (normal shutdown).
    /// </summary>
    private async Task<bool> RunLeadershipSessionAsync(
        WorkspaceContext workspace,
        string canonicalRoot,
        string canonicalDbPath,
        string millerDir,
        int bindingGeneration,
        CancellationToken stoppingToken)
    {
        // --- leader election: poll until we win the lock (or are asked to stop) ---
        // M8 §D4: log the transition into the reader role ONCE at Information (the meaningful state change),
        // then each subsequent failed re-try at Debug ("still a reader") so the 5s failover poll does not spam
        // Information forever. Becoming the leader below is the other transition, logged at Information.
        bool announcedReader = false;
        LeadershipVerdict? claimVerdict = null;
        while (!stoppingToken.IsCancellationRequested &&
               bindingGeneration == _bootstrap.BindingGeneration &&
               _lease is null)
        {
            IndexerLeadershipClaimResult claim = _leadership.TryClaim(millerDir, canonicalDbPath);
            if (claim.Claimed)
            {
                _lease = claim.Lease;
                claimVerdict = claim.Verdict; // the verdict that gated THIS claim drives the D3 upgrade rescan below
                break;
            }

            // Reader retry tick: if a LIVE leader bundles a strictly older extractor, ask it to yield (D4).
            _leadership.MaybeRequestYield(millerDir, workspace.WorkspaceId, claim.Verdict);
            if (!announcedReader)
            {
                _logger.LogInformation(
                    "Not the indexer leader (ineligible, cooling down, or another miller holds the lock); " +
                    "idling as a reader.");
                announcedReader = true;
            }
            else
            {
                _logger.LogDebug(
                    "Still a reader; will re-try in {RetrySeconds}s.",
                    _leaderRetryInterval.TotalSeconds);
            }
            await Task.Delay(_leaderRetryInterval, stoppingToken).ConfigureAwait(false);
        }

        if (stoppingToken.IsCancellationRequested)
            return false;
        if (bindingGeneration != _bootstrap.BindingGeneration)
            return false;

        // --- leader: build the dispatch core + attach the watchers ---
        // Pass the CANONICAL db (verified-fact 4): the single-file update/delete ops require an
        // already-canonical --db (the runner no longer GetFullPath-mangles it).
        IExtractOps ops = _createOps(workspace, canonicalRoot, canonicalDbPath);
        lock (_opsGate)
            _ops = ops; // publish for M6 write-through (TryReindexAsLeader)
        _core = new IndexerCore(
            new WatchEventQueue(), ops, File.Exists,
            _loggerFactory.CreateLogger<IndexerCore>());

        // Resolve julie's claimed extension set BEFORE the watchers attach so every event handler sees the
        // final value. Null (probe failed / binary missing) gates nothing — the historical behavior.
        _supportedExtensions = _fetchSupportedExtensions(workspace);
        if (_supportedExtensions is { Count: > 0 } gate)
            _logger.LogInformation(
                "Watcher extension gate active: {Count} extensions claimed by julie-extract.", gate.Count);

        if (_attachFileWatchers)
            AttachWatchers(canonicalRoot);
        // M8 §D2: this instance won the lease — flip the live log role to leader so every subsequent log line
        // (human + jsonl) is tagged role=leader, distinguishing it from the reader instances sharing the logs
        // directory. Readers leave the startup default (reader) untouched.
        MillerRole.SetLeader();
        // Record WHO leads (pid/version/path/extractor version) for `workspace health` and for the D4 yield
        // protocol: readers compare their extractor against ExtractorVersion to decide whether to challenge.
        // Best-effort: identity is advisory only.
        try
        {
            LeaderIdentityFile.Write(millerDir, new LeaderIdentity(
                Environment.ProcessId, MillerVersion.Current, Environment.ProcessPath, DateTimeOffset.UtcNow,
                _leadership.ProbeOwnExtractorVersion()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed write must not leave a crashed predecessor's leader.json as the visible truth: health
            // would report a dead/mismatched leader while THIS healthy process leads. Clear it (best-effort).
            LeaderIdentityFile.TryDelete(millerDir);
            _logger.LogWarning(ex, "Could not record the leader identity; workspace health will report it as unknown.");
        }
        if (_attachFileWatchers)
            _logger.LogInformation("Indexer leader: watching {Root} (recursive files + directories) + .git/HEAD.", canonicalRoot);

        // Yield once so BackgroundService.StartAsync can return after the watcher is attached: existing DBs
        // are available immediately as loaded_existing, while this leader reconciles missed downtime edits in
        // the background before the first debounce tick.
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested)
            return false;
        if (bindingGeneration != _bootstrap.BindingGeneration)
            return false;

        RunStartupDeltaScan(workspace);
        if (bindingGeneration != _bootstrap.BindingGeneration)
            return false;

        // D3 auto-upgrade rescan: this claim's verdict proved the artifact was produced by an OLDER extractor
        // than ours, so reconcile the whole repo with the newer binary immediately — upgrades self-heal with
        // zero user action. One forced scan per claim, never per tick.
        if (claimVerdict is { ArtifactOlderThanOwn: true })
        {
            _logger.LogInformation(
                "Extractor upgrade detected: bundled julie-extract {OwnVersion} is newer than the index artifact; " +
                "running a forced full rescan.",
                _leadership.ProbeOwnExtractorVersion());
            lock (_opsGate)
                ScanAsLeaderUnderGate(force: true, source: "extractor-upgrade");
        }

        // --- debounce loop: drain on each tick (collects bursts into a single coalesced batch) ---
        while (!stoppingToken.IsCancellationRequested &&
               bindingGeneration == _bootstrap.BindingGeneration)
        {
            await Task.Delay(DebounceInterval, stoppingToken).ConfigureAwait(false);

            bool headChanged;
            lock (_headGate)
            {
                headChanged = _headChanged;
                _headChanged = false;
            }

            IndexerLeadershipYieldDecision? yieldDecision = null;
            try
            {
                // D4 leader side: drain yield requests alongside the other request kinds. The decision is
                // evaluated first but ACTED on only after this tick's work finishes (below), so an in-flight
                // converge batch is never abandoned mid-tick.
                yieldDecision = _leadership.EvaluateYieldRequests(millerDir, LogRequestDrainStats);
                TryProcessLeaderFullScanRequests(millerDir);
                TryProcessFileConvergeRequests(millerDir);

                // Hold _opsGate across the drain so the debounce-loop drain and the on-demand Try* scans
                // (TryScanAsLeader / TryReindexAsLeader) share ONE serialization: two julie `extract`
                // subprocesses must never run against the same .miller DB at once (the M3 single-writer
                // corruption guard, D3). DrainAndProcess additionally serializes the queue on IndexerCore's
                // own gate; the lock order is always _opsGate -> _core gate (the Try* methods never take the
                // core gate, and the watcher enqueue only takes the core gate), so there is no inversion.
                lock (_opsGate)
                {
                    bool processed = _core!.DrainAndProcess(headChanged, out bool usedWholeRepoScan);
                    if (processed)
                        TryConvergeSidecarToLatest(workspace.CanonicalExtractDbPath, fullRebuild: usedWholeRepoScan);
                }
            }
            catch (Exception ex)
            {
                // DrainAndProcess isolates per-op failures itself; a throw here is a bug in routing, not an
                // extract failure. Log and keep the loop alive — the watcher must not die on one bad tick.
                _logger.LogError(ex, "Indexer drain tick failed; continuing.");
            }

            if (yieldDecision is { } decision)
            {
                AbdicateLeadership(
                    millerDir,
                    decision.RequesterPid,
                    decision.RequesterVersion,
                    decision.RequesterObservedAtUtc);
                return true; // re-enter the claim loop as a reader, under the cooldown
            }
        }

        return false;
    }

    // Production own-version probe: locate the bundled binary once and ask it. Null on ANY failure (missing
    // binary, failed exec) — the eligibility matrix then renders this instance a permanent reader with a clear
    // reason, and the existing restore-script guidance applies. Runs lazily inside ExecuteAsync, never in the
    // constructor (host-lifecycle rule: no bootstrap getters before bootstrap StartAsync).
    private string? ProbeBundledExtractorVersion()
    {
        try
        {
            return JulieExtractRunner.Locate(_bootstrap.Workspace.ToolsRoot).QueryVersion();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "Could not probe the bundled julie-extract version; this instance cannot claim indexer leadership.");
            return null;
        }
    }

    // D4 abdication: tear down ALL leader-only state, remove our identity, release the lease so the challenger's
    // 5s retry can win it, and arm the anti-flap cooldown toward the challenger. Mirrors the graceful step-down
    // in ExecuteAsync's finally (identity removed BEFORE the lease is released).
    private void AbdicateLeadership(
        string millerDir,
        int requesterPid,
        string requesterVersion,
        DateTimeOffset requesterObservedAtUtc)
    {
        _logger.LogInformation(
            "Yielding indexer leadership: requester pid {RequesterPid} bundles extractor {RequesterVersion}, newer than own {OwnVersion}; " +
            "abdicating and entering a {CooldownSeconds}s cooldown.",
            requesterPid, requesterVersion, _leadership.ProbeOwnExtractorVersion(), YieldCooldown.Duration.TotalSeconds);
        DisposeWatchers();
        lock (_opsGate)
        {
            _ops = null; // stop offering inline write-through; TryScanAsLeader reports NotLeader again
            _core = null;
        }
        LeaderIdentityFile.TryDelete(millerDir);
        MillerRole.SetReader();
        _lease?.Dispose();
        _lease = null;
        _leadership.BeginCooldown(requesterPid, requesterObservedAtUtc);
    }

    private void RunStartupDeltaScan(WorkspaceContext workspace)
    {
        string stableWorkspaceId = workspace.WorkspaceId
            ?? throw new InvalidOperationException(
                "IndexerService cannot run startup scan before bootstrap resolves the stable workspace id.");

        try
        {
            ExtractReport report;
            lock (_opsGate)
            {
                IExtractOps ops = _ops
                    ?? throw new InvalidOperationException("Indexer leader startup scan requested before ops were published.");
                report = ops.Scan(force: false);
                // Converge search.db under _opsGate — the same lock that serializes extract subprocesses — so the
                // symbols.db read never races a concurrent extract that could replace the file. Revision-gated
                // inside the sidecar; a no-op when the feature is off.
                TryConvergeSidecar(workspace.CanonicalExtractDbPath, report, fullRebuild: true);
            }

            _registryPublisher.MarkScanned(workspace, stableWorkspaceId, report.Revision);
            _logger.LogInformation(
                "Startup delta scan complete: revision {Revision}, {Updated} files updated, {Deleted} files deleted.",
                report.Revision, report.FilesUpdated, report.FilesDeleted);
            if (PartialExtractLog.DescribePartial(report) is { } partial)
                _logger.LogWarning("Startup delta scan: {Partial}", partial);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup delta scan failed; keeping the loaded index until a later scan converges.");
            try
            {
                IndexBootstrapService.MarkRegistryError(workspace, stableWorkspaceId, ex.Message);
            }
            catch (Exception registryEx)
            {
                _logger.LogWarning(registryEx, "Failed to record startup scan failure in the workspace registry.");
            }
        }
    }

    /// <summary>
    /// M6 write-through (decision-6): if THIS instance is the indexer leader, reindex <paramref name="path"/>
    /// inline (<c>extract update --file</c>) so its FreshnessService bumps the revision and swaps the index for
    /// the next edit's gate. Returns true if the leader performed the reindex; false if this instance is not the
    /// leader (the caller then relies on the leader's watcher reconciling the file write — the backstop). The
    /// reindex is best-effort: an extract failure is logged and reported as not-converged-inline, never thrown,
    /// because the edit is already committed to disk and the freshness gate is the ultimate safety net.
    /// </summary>
    public bool TryReindexAsLeader(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_opsGate)
        {
            if (_ops is not { } ops)
                return false; // not the leader — the watcher event from the file write converges instead
            try
            {
                ExtractReport report = ops.Update(path);
                if (PartialExtractLog.DescribePartial(report) is { } partial)
                    _logger.LogWarning("Inline write-through reindex of {Path}: {Partial}", path, partial);
                TryConvergeSidecar(_bootstrap.Workspace.CanonicalExtractDbPath, report, fullRebuild: false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Inline write-through reindex of {Path} failed; the freshness gate will catch it.", path);
                return false;
            }
        }
    }

    /// <summary>
    /// M7 workspace refresh/full (decision-3): if THIS instance is the indexer leader, run a whole-repo
    /// <c>extract scan</c> through its <see cref="IExtractOps"/> — <paramref name="force"/> <c>false</c> is the
    /// delta reconcile behind <c>workspace refresh</c>, <c>true</c> the from-scratch rebuild behind
    /// <c>workspace full</c> — and return <see cref="ScanOutcome.Scanned(ExtractReport)"/> carrying julie's
    /// report (the revision the freshness poll then converges on). If this instance does NOT hold the writer lock
    /// it returns <see cref="ScanOutcome.NotLeader"/> WITHOUT scanning: two miller instances must never both
    /// <c>extract scan</c> (the M3 single-writer corruption guard), so a non-leader honestly reports it cannot
    /// force a scan here and relies on the leader's watcher + the freshness poll. The scan runs under
    /// <see cref="_opsGate"/> — the same serialization as the debounce-loop drain and the M6 write-through — so
    /// an on-demand scan never races a concurrent <c>extract</c>. Best-effort: an extract failure is logged and
    /// returned as <see cref="ScanOutcome.Failed"/>, never thrown into the caller (the tool), because the prior
    /// index stays valid and the next scan/poll reconciles.
    /// </summary>
    public ScanOutcome TryScanAsLeader(bool force)
    {
        lock (_opsGate)
        {
            return ScanAsLeaderUnderGate(force, "On-demand");
        }
    }

    private bool TryProcessLeaderFullScanRequests(string millerDir)
    {
        FullScanDrainResult result;
        try
        {
            result = _drainFullScanRequests(millerDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Leader full-scan request drain failed; will retry on a later tick.");
            return false;
        }

        LogRequestDrainStats("full-scan", result.ExpiredDiscarded, result.ClaimSkipped);
        if (!result.Requested)
            return false;

        lock (_opsGate)
        {
            ScanOutcome outcome = ScanAsLeaderUnderGate(force: true, source: "Leader-requested");
            return outcome.Result == ScanOutcome.Kind.Scanned;
        }
    }

    private ScanOutcome ScanAsLeaderUnderGate(bool force, string source)
    {
        if (_ops is not { } ops)
            return ScanOutcome.NotLeader; // not the leader — must not write (M3 single-writer guard)

        ExtractReport report;
        try
        {
            report = ops.Scan(force);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Source} {Kind} scan failed; keeping the prior index (the next scan/poll reconciles).",
                source, force ? "full (force)" : "refresh (delta)");
            return ScanOutcome.Failed;
        }

        if (PartialExtractLog.DescribePartial(report) is { } partial)
            _logger.LogWarning(
                "{Source} {Kind} scan: {Partial}", source, force ? "full (force)" : "refresh (delta)", partial);

        // Converge derived sidecars after a successful scan, still under _opsGate. Deliberately OUTSIDE the
        // scan's try/catch so sidecar issues can never be misreported as scan failures. Some pure unit seams
        // publish fake ops without seeding bootstrap workspace state; those still test scan dispatch only.
        if (TryGetWorkspaceForSidecarConvergence() is { CanonicalExtractDbPath: { } symbolsDbPath })
            TryConvergeSidecar(symbolsDbPath, report, fullRebuild: true);

        return ScanOutcome.Scanned(report);
    }

    private WorkspaceContext? TryGetWorkspaceForSidecarConvergence()
    {
        try
        {
            return _bootstrap.Workspace;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void TryConvergeSidecarToLatest(string? symbolsDbPath, bool fullRebuild)
    {
        if (symbolsDbPath is null)
            return;

        try
        {
            using var freshness = new FreshnessReader(symbolsDbPath);
            TryConvergeSidecar(symbolsDbPath, freshness.LatestRevision(), fullRebuild);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or IncompatibleExtractException)
        {
            _logger.LogWarning(ex,
                "Search sidecar freshness check failed; the sidecar will remain unavailable or stale until the next successful convergence.");
        }
    }

    private void TryConvergeSidecar(string? symbolsDbPath, ExtractReport report, bool fullRebuild)
    {
        if (report.Revision is { } revision)
            TryConvergeSidecar(symbolsDbPath, revision, fullRebuild);
    }

    // Sidecar convergence for the CURRENT workspace. MUST be called holding _opsGate: it reads symbols.db, which
    // a concurrent extract could replace.
    private void TryConvergeSidecar(string? symbolsDbPath, long revision, bool fullRebuild)
    {
        if (symbolsDbPath is null || revision <= 0)
            return; // no symbols.db path or no revision cursor to stamp; the next revision-bearing op builds it

        string workspaceRoot = _bootstrap.Workspace.CanonicalRoot ?? _bootstrap.Workspace.WorkspaceRoot;
        string? workspaceId = _bootstrap.Workspace.WorkspaceId;
        _sidecarConverger.Converge(symbolsDbPath, workspaceRoot, workspaceId, revision, fullRebuild);
        _registryPublisher.TryMarkScanned(_bootstrap.Workspace, workspaceId, revision);
    }

    /// <summary>
    /// Test seam: publish a fake <see cref="IExtractOps"/> as THIS instance's leader ops AND build the dispatch
    /// <see cref="IndexerCore"/> over the SAME ops, exactly as the production <see cref="ExecuteAsync"/> does once
    /// leadership is won (one ops instance driven by both the Try* methods and the debounce drain). Lets a unit
    /// test exercise the <see cref="TryScanAsLeader"/> / <see cref="TryReindexAsLeader"/> leader branch, and the
    /// drain-vs-Try* serialization (<see cref="DrainForTest"/>), without acquiring the cross-process writer lock
    /// or spawning julie. Not used in production.
    /// </summary>
    internal void PublishOpsForTest(IExtractOps ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        lock (_opsGate)
        {
            _ops = ops;
            _core = new IndexerCore(
                new WatchEventQueue(), ops, _ => true, _loggerFactory.CreateLogger<IndexerCore>());
        }
    }

    /// <summary>
    /// Test seam: enqueue a watcher event into the published core's queue so <see cref="DrainForTest"/> has work
    /// to dispatch. Requires <see cref="PublishOpsForTest"/> to have built the core. Not used in production.
    /// </summary>
    internal void EnqueueForTest(WatchEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        IndexerCore core = _core
            ?? throw new InvalidOperationException("PublishOpsForTest must run before EnqueueForTest.");
        core.Enqueue(ev);
    }

    /// <summary>
    /// Test seam: run the same watcher change routing that <see cref="FileSystemWatcher"/> invokes, without
    /// requiring a live watcher. Requires <see cref="PublishOpsForTest"/> to have built the core.
    /// </summary>
    internal void HandleChangedForTest(WatcherChangeTypes changeType, string fullPath) =>
        HandleChanged(changeType, fullPath);

    /// <summary>
    /// Test seam: run the same directory-change routing that the directory <see cref="FileSystemWatcher"/>
    /// invokes, without requiring a live watcher. Requires <see cref="PublishOpsForTest"/> to have built the core.
    /// </summary>
    internal void HandleDirectoryChangedForTest(string fullPath) =>
        HandleDirectoryChanged(fullPath);

    /// <summary>
    /// Test seam: run the same directory-rename routing that the directory <see cref="FileSystemWatcher"/>
    /// invokes, without requiring a live watcher. Requires <see cref="PublishOpsForTest"/> to have built the core.
    /// </summary>
    internal void HandleDirectoryRenamedForTest(string oldFullPath, string fullPath) =>
        HandleDirectoryRenamed(oldFullPath, fullPath);

    /// <summary>
    /// Test seam: install a supported-extension set for the watcher gate exactly as the leader path does
    /// after winning the lease, without spawning the live <c>languages --json</c> probe. Not used in production.
    /// </summary>
    internal void SetSupportedExtensionsForTest(IReadOnlySet<string>? extensions) =>
        _supportedExtensions = extensions;

    /// <summary>
    /// Test seam: run ONE debounce drain exactly as the production loop does — under <see cref="_opsGate"/>, the
    /// same lock the on-demand <see cref="TryScanAsLeader"/> / <see cref="TryReindexAsLeader"/> take — so a test
    /// can drive a drain concurrently with a Try* call and assert they never run two extracts at once (the M3
    /// single-writer guard, D3). Requires <see cref="PublishOpsForTest"/> to have built the core. Not used in
    /// production.
    /// </summary>
    internal void DrainForTest(bool headChanged)
    {
        IndexerCore core = _core
            ?? throw new InvalidOperationException("PublishOpsForTest must run before DrainForTest.");
        lock (_opsGate)
        {
            bool processed = core.DrainAndProcess(headChanged, out bool usedWholeRepoScan);
            if (processed)
                TryConvergeSidecarToLatest(_bootstrap.Workspace.CanonicalExtractDbPath, fullRebuild: usedWholeRepoScan);
        }
    }

    /// <summary>
    /// Test seam: drain leader full-scan request files and service them through the same force-scan path the
    /// production debounce loop uses. Requires <see cref="PublishOpsForTest"/> when the caller expects a scan.
    /// Not used in production.
    /// </summary>
    internal bool ProcessLeaderFullScanRequestsForTest(string millerDir) =>
        TryProcessLeaderFullScanRequests(millerDir);

    /// <summary>
    /// Test seam: drain single-file converge request files and enqueue them into the core's coalescing queue
    /// exactly as the production debounce loop does; pair with <see cref="DrainForTest"/> to run the resulting
    /// extracts. Requires <see cref="PublishOpsForTest"/> when the caller expects work to be enqueued. Not used
    /// in production.
    /// </summary>
    internal bool ProcessFileConvergeRequestsForTest(string millerDir) =>
        TryProcessFileConvergeRequests(millerDir);

    /// <summary>
    /// Test seam: run ONE gated claim attempt exactly as the production claim loop does (D2 eligibility gate →
    /// D4 cooldown gate → acquire func). Returns true when the lease was acquired. Pure when the version/artifact
    /// funcs are injected — no lock files, no bootstrap. Not used in production.
    /// </summary>
    internal bool AttemptClaimForTest(string millerDir, string? extractDbPath = null)
    {
        IndexerLeadershipClaimResult claim = _leadership.TryClaim(millerDir, extractDbPath);
        if (claim.Claimed)
            _lease = claim.Lease;
        return claim.Claimed;
    }

    /// <summary>
    /// Test seam: run ONE reader retry tick's yield-request side (D4 requester) — evaluate eligibility, then
    /// challenge a live older leader at most once per TTL/leader. Not used in production.
    /// </summary>
    internal void MaybeRequestYieldForTest(string millerDir, string workspaceId, string? extractDbPath = null) =>
        _leadership.MaybeRequestYield(
            millerDir,
            workspaceId,
            _leadership.EvaluateEligibility(extractDbPath));

    /// <summary>
    /// Test seam: mark THIS instance as holding the writer lease (as the production claim loop does on a win)
    /// so a test can drive the leader-side yield path and assert the lease is disposed on abdication. Pair with
    /// <see cref="PublishOpsForTest"/> for the ops/core state. Not used in production.
    /// </summary>
    internal void AssumeLeadershipForTest(IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _lease = lease;
    }

    /// <summary>
    /// Test seam: drain yield requests and, when the strongest challenger is strictly newer, perform the full
    /// abdication (leader-state teardown, identity delete, lease release, cooldown) exactly as the production
    /// debounce tick does. Returns true when leadership was yielded. Not used in production.
    /// </summary>
    internal bool ProcessYieldRequestsForTest(string millerDir)
    {
        if (_leadership.EvaluateYieldRequests(millerDir, LogRequestDrainStats) is not { } decision)
            return false;
        AbdicateLeadership(
            millerDir,
            decision.RequesterPid,
            decision.RequesterVersion,
            decision.RequesterObservedAtUtc);
        return true;
    }

    // Drain single-file converge requests (written by reader write-through / gate-time recovery) and enqueue
    // each as a synthetic Changed event into the core's coalescing WatchEventQueue — the SAME queue the
    // FileSystemWatcher feeds, drained by _core.DrainAndProcess later on the SAME debounce tick (this method
    // runs before the drain in ExecuteAsync's loop, so converge latency stays within one tick). Routing through
    // the queue instead of calling TryReindexAsLeader directly lets the queue's per-path coalescing collapse a
    // reader's converge request and the watcher event for the same file write into ONE extract (M3). ONLY the
    // leader may drain: a reader consuming requests would delete them unserviced. Never escalates to a
    // whole-repo scan — these are targeted converges by design (queue overflow forcing a scan-reconcile is the
    // shared lossy-stream backstop, not an escalation of these requests).
    private bool TryProcessFileConvergeRequests(string millerDir)
    {
        IndexerCore? core;
        lock (_opsGate)
        {
            if (_ops is null)
                return false; // not the leader — leave the requests for the instance that can service them
            core = _core;
        }

        if (core is null)
            return false; // leader ops without a core never happens in production; nothing can service the queue

        FileConvergeDrainResult result;
        try
        {
            result = _drainFileConvergeRequests(millerDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "File-converge request drain failed; will retry on a later tick.");
            return false;
        }

        LogRequestDrainStats("file-converge", result.ExpiredDiscarded, result.ClaimSkipped);
        foreach (string path in result.Paths)
            core.Enqueue(new WatchEvent(path, WatchEventKind.Modified));
        return result.Paths.Count > 0;
    }

    // M2/M4 drain bookkeeping: discarded-expired requests are an Information fact (a leader on an old build let
    // them rot); a request that could not be claimed warns ONCE then drops to Debug (see _requestClaimSkipWarned).
    private void LogRequestDrainStats(string kind, int expiredDiscarded, int claimSkipped)
    {
        if (expiredDiscarded > 0)
            _logger.LogInformation(
                "Discarded {Count} expired {Kind} request(s) older than {TtlMinutes} minutes without servicing.",
                expiredDiscarded, kind, LeaderScanRequestQueue.RequestTtl.TotalMinutes);

        if (claimSkipped > 0)
        {
            if (!_requestClaimSkipWarned)
            {
                _requestClaimSkipWarned = true;
                _logger.LogWarning(
                    "Skipped {Count} {Kind} request(s) that could not be claimed (file held or undeletable); " +
                    "they will be retried on later ticks and swept after the TTL.",
                    claimSkipped, kind);
            }
            else
            {
                _logger.LogDebug(
                    "Skipped {Count} {Kind} request(s) that could not be claimed; still waiting on the TTL sweep.",
                    claimSkipped, kind);
            }
        }
    }

    private void AttachWatchers(string canonicalRoot)
    {
        _watchers = IndexerWatcherSet.Attach(
            canonicalRoot,
            new IndexerWatcherCallbacks(
                OnChanged,
                OnRenamed,
                OnError,
                OnDirectoryChanged,
                OnDirectoryRenamed,
                OnHeadChanged,
                OnIgnorePolicyChanged));
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        HandleChanged(e.ChangeType, e.FullPath);
    }

    private void HandleChanged(WatcherChangeTypes changeType, string fullPath)
    {
        if (_core is null)
            return;

        string root = _bootstrap.Workspace.CanonicalRoot!;
        if (WatchPathFilter.ShouldForceRescan(root, fullPath))
        {
            _core.SignalRescan();
            return;
        }
        if (Directory.Exists(fullPath))
        {
            HandleDirectoryChanged(fullPath);
            return;
        }
        if (!WatchPathFilter.ShouldProcess(root, fullPath, _supportedExtensions))
            return;
        _core.Enqueue(WatcherEventMapper.Map(changeType, fullPath));
    }

    private void OnDirectoryChanged(object sender, FileSystemEventArgs e)
    {
        HandleDirectoryChanged(e.FullPath);
    }

    private void HandleDirectoryChanged(string fullPath)
    {
        if (_core is null)
            return;

        string root = _bootstrap.Workspace.CanonicalRoot!;
        if (WatchPathFilter.ShouldProcess(root, fullPath))
            _core.SignalRescan();
    }

    private void OnDirectoryRenamed(object sender, RenamedEventArgs e)
    {
        HandleDirectoryRenamed(e.OldFullPath, e.FullPath);
    }

    private void HandleDirectoryRenamed(string oldFullPath, string fullPath)
    {
        if (_core is null)
            return;

        string root = _bootstrap.Workspace.CanonicalRoot!;
        if (WatchPathFilter.ShouldProcess(root, oldFullPath)
            || WatchPathFilter.ShouldProcess(root, fullPath))
            _core.SignalRescan();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (_core is null)
            return;
        string root = _bootstrap.Workspace.CanonicalRoot!;
        if (WatchPathFilter.ShouldForceRescan(root, e.OldFullPath)
            || WatchPathFilter.ShouldForceRescan(root, e.FullPath))
        {
            _core.SignalRescan();
            return;
        }
        if (Directory.Exists(e.FullPath))
        {
            OnDirectoryRenamed(sender, e);
            return;
        }

        bool oldOk = WatchPathFilter.ShouldProcess(root, e.OldFullPath, _supportedExtensions);
        bool newOk = WatchPathFilter.ShouldProcess(root, e.FullPath, _supportedExtensions);

        // A rename can cross the filter boundary. Renamed INTO a watched area = a create; renamed OUT = a delete;
        // both watched = a true rename; neither = ignore.
        if (oldOk && newOk)
            _core.Enqueue(WatcherEventMapper.MapRenamed(e.OldFullPath, e.FullPath));
        else if (newOk)
            _core.Enqueue(WatcherEventMapper.Map(WatcherChangeTypes.Created, e.FullPath));
        else if (oldOk)
            _core.Enqueue(WatcherEventMapper.Map(WatcherChangeTypes.Deleted, e.OldFullPath));
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // InternalBuffer overflow: events were dropped at the OS level. Force a whole-repo scan reconcile.
        _logger.LogWarning(e.GetException(), "FileSystemWatcher buffer overflow; forcing a rescan.");
        _core?.SignalRescan();
    }

    private void OnHeadChanged(object sender, FileSystemEventArgs e)
    {
        lock (_headGate)
            _headChanged = true;
    }

    private void OnIgnorePolicyChanged(object sender, FileSystemEventArgs e)
    {
        _core?.SignalRescan();
    }

    private void DisposeWatchers()
    {
        _watchers?.Dispose();
        _watchers = null;
    }
}
