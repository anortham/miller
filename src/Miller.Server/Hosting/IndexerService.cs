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

    // A path that RETRIES gets a short admission budget; only a path with no retry gets a long one. EVERY
    // governed site in this service retries — the debounce drain re-peeks each tick, and the startup, upgrade,
    // and leader-requested scans all re-arm the latch. Two of them (TryScanAsLeader, and the drain that delays
    // D4 abdication) are reachable from a live MCP call, where a half-hour stall would jam every agent sharing
    // the connection and outlive LeaderScanRequestQueue's request TTL. MILLER_SCAN_GOVERNOR_WAIT deliberately
    // does NOT apply here: it is the operator budget for the one-shot CLI/dashboard forced refresh.
    internal static readonly TimeSpan DefaultScanAdmissionWait = TimeSpan.FromSeconds(5);

    // The owner-record label for a scan whose workspace root cannot be resolved (only reachable mid-rebind).
    // It is diagnostics text, never a lookup key — no local state is published under it.
    internal const string UnknownWorkspaceRootLabel = "(unknown)";

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

    // Machine-wide (per-user) admission over whole-repo scans and the sidecar convergence that follows them.
    // Acquired BEFORE _opsGate at every governed site so the per-file write-through path this design exempts
    // (TryReindexAsLeader) can never be blocked behind an admission wait.
    private readonly ScanGovernor _governor;
    private readonly TimeSpan _governorWait;
    private volatile CancellationTokenSource? _governorCancellation;

    private volatile IDisposable? _lease;
    private IndexerWatcherSet? _watchers;
    private IndexerCore? _core;
    private string? _currentMillerDir;

    // The scan-governor key for the workspace this session serves, captured while the bootstrap is bound. It is
    // the same value ScanGovernorKey.For yields, so a mid-rebind acquire (when the bootstrap getter throws) still
    // publishes under a root status/health actually look up instead of an invented one.
    private volatile string? _currentScanGovernorKey;

    // The leader's extract ops, set once leadership is won (null on a non-leader). M6 write-through reaches
    // through TryReindexAsLeader to converge the index inline after an apply; guarded by _opsGate so an edit on
    // the MCP thread never races the debounce-loop drain (julie tolerates one in-flight subprocess, but we keep
    // Miller's own calls serialized regardless).
    private IExtractOps? _ops;
    private readonly object _opsGate = new();

    // The persisted, per-workspace whole-repo scan-failure policy: the SINGLE retry timer every scan site in this
    // service and in IndexerCore consults. Bound once leadership is won (it needs the workspace's .miller dir);
    // the in-memory fallback covers the pure test seams that publish ops without a bound workspace. Reference
    // assignment is atomic; the debounce loop and MCP threads both read it.
    private readonly IScanFailurePolicy _fallbackFailurePolicy = new InMemoryScanFailurePolicy();
    private volatile IScanFailurePolicy? _failurePolicy;

    private IScanFailurePolicy FailurePolicy => _failurePolicy ?? _fallbackFailurePolicy;

    // .git/HEAD changes (branch switch / checkout) are folded into ONE forced scan per drain rather than
    // drowning in the per-file storm a checkout produces (decision-7). Set by the HEAD watcher, read+reset on
    // the next drain under the lock below.
    private volatile bool _headChanged;
    private readonly object _headGate = new();

    public IndexerService(
        IndexBootstrapService bootstrap, ILogger<IndexerService> logger, ILoggerFactory loggerFactory,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar? contentSidecar = null,
        ScanGovernor? scanGovernor = null)
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
            drainLeaderHandoffRequests: LeaderScanRequestQueue.DrainLeaderHandoffRequests,
            // The watcher's extension gate: julie's own claimed set, fetched once per process, null on any
            // failure (gate nothing). Production-only — the internal test ctor defaults to no gate so no
            // fast test can ever spawn the languages probe.
            fetchSupportedExtensions: static workspace =>
                SupportedExtensionCatalog.ForToolsRoot(workspace.ToolsRoot),
            scanGovernor: scanGovernor)
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
        Func<string, LeaderHandoffDrainResult>? drainLeaderHandoffRequests = null,
        Func<string?>? ownExtractorVersion = null,
        bool? allowExtractorDowngrade = null,
        Func<string?, string?>? readArtifactExtractorVersion = null,
        Action<string, string, int, string>? requestYield = null,
        Func<string, LeaderIdentity?>? readLeaderIdentity = null,
        Func<LeaderIdentity, bool>? leaderAliveProbe = null,
        Func<DateTimeOffset>? clock = null,
        Func<int, DateTimeOffset?, bool>? processAliveProbe = null,
        Func<WorkspaceContext, IReadOnlySet<string>?>? fetchSupportedExtensions = null,
        ScanGovernor? scanGovernor = null,
        TimeSpan? scanGovernorWait = null)
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
            drainLeaderHandoffRequests ?? LeaderScanRequestQueue.DrainLeaderHandoffRequests,
            requestYield ?? LeaderScanRequestQueue.RequestYield,
            readLeaderIdentity ?? LeaderIdentityFile.TryRead,
            leaderAliveProbe ?? (static identity => LeaderIdentityFile.IsProcessAlive(identity)),
            leadershipClock,
            processAliveProbe ?? LeaderIdentityFile.IsProcessAlive);
        // Default = NO gate (null set). Unit tests reach this ctor; the gate's process probe must never run
        // in the fast suite, so only the public production ctor binds the real catalog fetch.
        _fetchSupportedExtensions = fetchSupportedExtensions ?? (static _ => null);
        // Default = OFF, so no fast test ever opens a lease under the real user-global ~/.miller.
        _governor = scanGovernor ?? ScanGovernor.Disabled();
        _governorWait = scanGovernorWait ?? DefaultScanAdmissionWait;
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
        // The synchronous scan paths take no token of their own, so an admission wait would otherwise stall
        // Generic-Host shutdown. TryScanAsLeader is reached from MCP tool code with no token and shares this one.
        var governorCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _governorCancellation = governorCancellation;
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
                _currentScanGovernorKey = canonicalRoot;

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
            governorCancellation.Cancel();
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
        _failurePolicy = PersistedScanFailurePolicy.For(canonicalDbPath, canonicalRoot);
        lock (_opsGate)
            _ops = ops; // publish for M6 write-through (TryReindexAsLeader)
        _core = new IndexerCore(
            new WatchEventQueue(), ops, File.Exists,
            _loggerFactory.CreateLogger<IndexerCore>(),
            FailurePolicy);

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

        if (claimVerdict is { ArtifactOlderThanOwn: true })
            RunExtractorUpgradeRescan();

        // --- debounce loop: drain on each tick (collects bursts into a single coalesced batch) ---
        while (!stoppingToken.IsCancellationRequested &&
               bindingGeneration == _bootstrap.BindingGeneration)
        {
            await Task.Delay(DebounceInterval, stoppingToken).ConfigureAwait(false);

            IndexerLeadershipYieldDecision? yieldDecision = null;
            IndexerLeadershipHandoffDecision? handoffDecision = null;
            try
            {
                // D4 leader side, decided BEFORE any work: a leader about to abdicate must not queue behind the
                // machine-wide scan lease for a scan it is giving up, and leaving the full-scan/converge request
                // files undrained hands them to its successor instead of deleting them unserviced.
                yieldDecision = _leadership.EvaluateYieldRequests(millerDir, LogRequestDrainStats);
                handoffDecision = _leadership.EvaluateLeaderHandoffRequests(millerDir, LogRequestDrainStats);

                if (yieldDecision is null && handoffDecision is null)
                    RunDrainTick(millerDir, workspace);
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

            if (handoffDecision is { } handoff)
            {
                AbdicateLeadershipForExplicitHandoff(
                    millerDir,
                    handoff.RequesterPid,
                    handoff.RequesterObservedAtUtc);
                return true; // re-enter the claim loop as a reader, under the cooldown
            }
        }

        return false;
    }

    // D3 auto-upgrade rescan: this claim's verdict proved the artifact was produced by an OLDER extractor than
    // ours, so reconcile the whole repo with the newer binary immediately — upgrades self-heal with zero user
    // action. One forced scan per claim, never per tick: this runs OUTSIDE the debounce loop and
    // ScanAsLeaderUnderGate REPORTS a throw as Failed rather than rethrowing, so a refused admission AND a failed
    // extract both have to re-arm the latch (with force) or the upgrade never self-heals.
    private void RunExtractorUpgradeRescan()
    {
        _logger.LogInformation(
            "Extractor upgrade detected: bundled julie-extract {OwnVersion} is newer than the index artifact; " +
            "running a forced full rescan.",
            _leadership.ProbeOwnExtractorVersion());

        bool rescanned = false;
        using (ScanGovernorAdmission? admission = TryAcquireScanAdmission("leader-upgrade"))
        {
            if (admission is not null)
            {
                lock (_opsGate)
                {
                    rescanned = ScanAsLeaderUnderGate(ScanIntent.ExtractorUpgrade, source: "extractor-upgrade")
                        .Result == ScanOutcome.Kind.Scanned;
                }
            }
        }

        if (!rescanned)
            _core?.RequestWholeRepoScan(ScanIntent.ExtractorUpgrade);
    }

    // One debounce tick's work: leader request files, then the coalesced drain. Runs only on a tick that is NOT
    // abdicating, so `.git/HEAD` stays latched for the successor rather than being consumed and dropped.
    private void RunDrainTick(string millerDir, WorkspaceContext workspace)
    {
        bool headChanged;
        lock (_headGate)
        {
            headChanged = _headChanged;
            _headChanged = false;
        }

        TryProcessLeaderFullScanRequests(millerDir);
        TryProcessFileConvergeRequests(millerDir);

        // Scan admission is taken OUTSIDE _opsGate (and outside IndexerCore's own gate), because waiting for it
        // while holding _opsGate would block the exempt per-file write-through path. The peek can race the
        // watcher threads, but only in the safe direction: if the flags grow between peek and drain,
        // DrainAndProcess skips the scan and keeps the latch for the next tick — never an ungoverned scan.
        bool mayScanWholeRepo = _core!.WouldRunWholeRepoScan(headChanged);
        using ScanGovernorAdmission? admission = mayScanWholeRepo
            ? TryAcquireScanAdmission("leader-drain-rescan")
            : null;
        BetweenScanPeekAndDrainForTest?.Invoke();

        // Hold _opsGate across the drain so the debounce-loop drain and the on-demand Try* scans
        // (TryScanAsLeader / TryReindexAsLeader) share ONE serialization: two julie `extract` subprocesses must
        // never run against the same .miller DB at once (the M3 single-writer corruption guard, D3).
        // DrainAndProcess additionally serializes the queue on IndexerCore's own gate; the lock order is always
        // _opsGate -> _core gate (the Try* methods take the core gate only to arm or clear the rescan latch,
        // always in that direction, and the watcher enqueue takes the core gate alone), so there is no inversion.
        lock (_opsGate)
        {
            bool processed = _core!.DrainAndProcess(
                headChanged,
                wholeRepoScanAdmitted: admission is not null,
                out bool usedWholeRepoScan);
            if (processed)
                TryConvergeSidecarToLatest(workspace.CanonicalExtractDbPath, fullRebuild: usedWholeRepoScan);
        }
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
        TearDownLeadershipForRequester(millerDir, requesterPid, requesterObservedAtUtc);
    }

    private void AbdicateLeadershipForExplicitHandoff(
        string millerDir,
        int requesterPid,
        DateTimeOffset requesterObservedAtUtc)
    {
        _logger.LogInformation(
            "Explicit indexer leadership handoff requested by pid {RequesterPid}; abdicating and entering a {CooldownSeconds}s cooldown.",
            requesterPid, YieldCooldown.Duration.TotalSeconds);
        TearDownLeadershipForRequester(millerDir, requesterPid, requesterObservedAtUtc);
    }

    private void TearDownLeadershipForRequester(
        string millerDir,
        int requesterPid,
        DateTimeOffset requesterObservedAtUtc)
    {
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

        // The startup delta is an AUTOMATIC path, so it honors the persisted backoff: a workspace whose scans keep
        // being OOM-killed must not get one more free attempt out of every fresh process that claims leadership.
        ScanAttemptDecision decision = FailurePolicy.Evaluate(ScanIntent.IncrementalReconcile);
        if (!decision.Attempt)
        {
            _core?.RequestWholeRepoScan(ScanIntent.IncrementalReconcile);
            _logger.LogWarning(
                "Startup delta scan deferred until {RetryAtUtc:O} after {Failures} consecutive whole-repo scan " +
                "failures; serving the loaded index until then.",
                decision.RetryAtUtc, decision.ConsecutiveFailures);
            return;
        }

        using ScanGovernorAdmission? admission = TryAcquireScanAdmission("leader-startup");
        if (admission is null)
        {
            // Not a workspace error: the index stays valid and the debounce loop's next rescan tick retries.
            // A startup delta is incremental, so the re-armed latch stays a delta scan.
            _core?.RequestWholeRepoScan(ScanIntent.IncrementalReconcile);
            return;
        }

        try
        {
            ExtractReport report;
            long armingGeneration = _core?.WholeRepoScanArmingGeneration ?? 0;
            lock (_opsGate)
            {
                IExtractOps ops = _ops
                    ?? throw new InvalidOperationException("Indexer leader startup scan requested before ops were published.");
                report = ops.Scan(ScanIntent.IncrementalReconcile, decision.Jobs);
                // Converge search.db under _opsGate — the same lock that serializes extract subprocesses — so the
                // symbols.db read never races a concurrent extract that could replace the file. Revision-gated
                // inside the sidecar; a no-op when the feature is off.
                TryConvergeSidecar(workspace.CanonicalExtractDbPath, report, fullRebuild: true);
            }

            FailurePolicy.RecordSuccess(ScanIntent.IncrementalReconcile);
            // A delta scan does not satisfy a pending FORCED request, and neither does it satisfy a request armed
            // after it started, so the core decides whether this clears the latch; without the call a delta that
            // ran here would leave it armed for a duplicate scan.
            _core?.NoteWholeRepoScanCompleted(ScanIntent.IncrementalReconcile, armingGeneration);
            _registryPublisher.MarkScanned(workspace, stableWorkspaceId, report.Revision);
            _logger.LogInformation(
                "Startup delta scan complete: revision {Revision}, {Updated} files updated, {Deleted} files deleted.",
                report.Revision, report.FilesUpdated, report.FilesDeleted);
            if (ExtractReportLog.DescribeWarning(report) is { } warning)
                _logger.LogWarning("Startup delta scan: {Warning}", warning);
        }
        catch (Exception ex)
        {
            // This block runs once per claim, OUTSIDE the debounce loop, so nothing else retries it: without the
            // re-arm a failed startup delta drops the reconcile for every edit made while Miller was down.
            RecordScanFailure(ScanIntent.IncrementalReconcile, decision, ex);
            _core?.RequestWholeRepoScan(ScanIntent.IncrementalReconcile);
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
                if (ExtractReportLog.DescribeWarning(report) is { } warning)
                    _logger.LogWarning("Inline write-through reindex of {Path}: {Warning}", path, warning);
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
    /// <c>extract scan</c> through its <see cref="IExtractOps"/> — <see cref="ScanIntent.IncrementalReconcile"/>
    /// is the delta reconcile behind <c>workspace refresh</c>, <see cref="ScanIntent.UserFullRebuild"/> the
    /// from-scratch rebuild behind <c>workspace full</c> — and return
    /// <see cref="ScanOutcome.Scanned(ExtractReport)"/> carrying julie's
    /// report (the revision the freshness poll then converges on). If this instance does NOT hold the writer lock
    /// it returns <see cref="ScanOutcome.NotLeader"/> WITHOUT scanning: two miller instances must never both
    /// <c>extract scan</c> (the M3 single-writer corruption guard), so a non-leader honestly reports it cannot
    /// force a scan here and relies on the leader's watcher + the freshness poll. The scan runs under
    /// <see cref="_opsGate"/> — the same serialization as the debounce-loop drain and the M6 write-through — so
    /// an on-demand scan never races a concurrent <c>extract</c>. Best-effort: an extract failure is logged and
    /// returned as <see cref="ScanOutcome.Failed"/>, never thrown into the caller (the tool), because the prior
    /// index stays valid and the next scan/poll reconciles. A refused machine-wide scan admission is NOT a
    /// failure: it re-arms the rescan latch with <paramref name="intent"/> and returns
    /// <see cref="ScanOutcome.Queued"/>, so the caller can say the scan is queued rather than sending an agent
    /// hunting an extract error that was never logged.
    ///
    /// <para><paramref name="bypassBackoff"/> is the direct-user carve-out: a person typing
    /// <c>workspace refresh/full</c> is not the automatic path the persisted scan-failure backoff exists to
    /// throttle, so the retry TIMER is skipped for them AND their rebuild is never downgraded to a delta —
    /// somebody who asked for a from-scratch rebuild must get one or be told they did not. The attempt is still
    /// recorded, and it still carries the post-SIGKILL jobs clamp.</para>
    ///
    /// <para>An AUTOMATIC caller can get <see cref="ScanOutcome.Kind.Downgraded"/>: a delta ran against the
    /// still-servable prior artifact and the requested rebuild is still owed (already re-armed on the rescan
    /// latch). It is neither a success nor a failure and must not be rendered as either.</para>
    /// </summary>
    public ScanOutcome TryScanAsLeader(ScanIntent intent, bool bypassBackoff = false)
    {
        if (!HoldsLeaderOps())
            return ScanOutcome.NotLeader; // never wait for machine-wide admission we could not use

        ScanAttemptDecision decision = FailurePolicy.Evaluate(intent, bypassBackoff);
        if (!decision.Attempt)
        {
            _core?.RequestWholeRepoScan(intent);
            return ScanOutcome.Queued(DescribeScanBackoff(decision));
        }

        using ScanGovernorAdmission? admission = TryAcquireScanAdmission("leader-ondemand");
        if (admission is null)
        {
            _core?.RequestWholeRepoScan(intent);
            return ScanOutcome.Queued(_governor.DescribeHolder());
        }

        lock (_opsGate)
        {
            return ScanAsLeaderUnderGate(intent, "On-demand", decision);
        }
    }

    private static string DescribeScanBackoff(ScanAttemptDecision decision) =>
        $"The previous whole-repo scan of this workspace failed {decision.ConsecutiveFailures} time(s) in a row; " +
        $"the next automatic attempt is not before {decision.RetryAtUtc:O}.";

    private bool HoldsLeaderOps()
    {
        lock (_opsGate)
            return _ops is not null;
    }

    // Machine-wide admission for ONE whole-repo scan plus the sidecar convergence that follows it. Always called
    // OUTSIDE _opsGate. Returns null on refusal or on shutdown; the caller must degrade, never scan ungoverned.
    private ScanGovernorAdmission? TryAcquireScanAdmission(string reason)
    {
        // Publish under the SAME key status/health read (CanonicalRoot, falling back to the unresolved root):
        // keying the writer by the symlink-resolved path and the reader by the raw one made this process's own
        // lease render as another process's on every symlinked workspace. When no key can be derived at all,
        // publish NO local state rather than a third key shape no reader looks up.
        WorkspaceContext? workspace = TryGetWorkspaceForSidecarConvergence();
        string? workspaceRoot = ScanGovernorKey.For(workspace) ?? _currentScanGovernorKey;
        var request = new ScanGovernorRequest(
            workspaceRoot ?? UnknownWorkspaceRootLabel, reason, ExtractJobsPolicy.FromEnvironment());

        ScanGovernorAdmission? admission;
        try
        {
            admission = ScanGovernorAdmission.TryAcquire(
                _governor,
                workspaceRoot is null ? null : ScanGovernorState.Shared,
                request,
                _governorWait,
                _governorCancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Scan admission wait ({Reason}) abandoned because the host is shutting down.", reason);
            return null;
        }

        if (admission is null)
            _logger.LogWarning(
                "Refused machine-wide scan admission for {Reason} after {WaitSeconds}s; keeping the prior index " +
                "(the next tick retries). {Holder}",
                reason, _governorWait.TotalSeconds, _governor.DescribeHolder());

        return admission;
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

        // The drain already DELETED the request files, so a refused, deferred, downgraded, or failed scan must
        // re-arm the latch or the requester's rebuild is silently dropped — there is no request file left to
        // retry from. Servicing another process's queued request is an AUTOMATIC path — the requester is long
        // gone — so it honors the backoff rather than bypassing it.
        ScanAttemptDecision decision = FailurePolicy.Evaluate(ScanIntent.UserFullRebuild);
        if (!decision.Attempt)
        {
            _core?.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
            _logger.LogWarning(
                "Leader-requested full scan deferred: {Backoff}", DescribeScanBackoff(decision));
            return false;
        }

        using ScanGovernorAdmission? admission = TryAcquireScanAdmission("leader-requested-full");
        if (admission is null)
        {
            _core?.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
            return false;
        }

        lock (_opsGate)
        {
            ScanOutcome outcome = ScanAsLeaderUnderGate(
                ScanIntent.UserFullRebuild, source: "Leader-requested", decision);
            // A downgraded run re-armed the rebuild inside the gate, where the decision not to discharge it was
            // made; re-arming it again here would only bump the arming generation a second time.
            if (outcome.Result is not (ScanOutcome.Kind.Scanned or ScanOutcome.Kind.Downgraded))
                _core?.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
            return outcome.Result == ScanOutcome.Kind.Scanned;
        }
    }

    // Callers MUST already hold machine-wide scan admission (TryAcquireScanAdmission), acquired outside
    // _opsGate, and must hold it across the sidecar convergence below — scan+sidecar storms must not overlap
    // across workspaces. `decision` is the scan-failure policy's verdict for this attempt, evaluated by the
    // caller BEFORE it took admission; null means "evaluate here" (the upgrade-rescan path, which has no earlier
    // decision point).
    private ScanOutcome ScanAsLeaderUnderGate(
        ScanIntent intent, string source, ScanAttemptDecision? decision = null)
    {
        if (_ops is not { } ops)
            return ScanOutcome.NotLeader; // not the leader — must not write (M3 single-writer guard)

        ScanAttemptDecision attempt = decision ?? FailurePolicy.Evaluate(intent);
        if (!attempt.Attempt)
        {
            _logger.LogWarning("{Source} scan deferred: {Backoff}", source, DescribeScanBackoff(attempt));
            return ScanOutcome.Queued(DescribeScanBackoff(attempt));
        }

        // Read BEFORE the scan: TryScanAsLeader's own refusal path arms the latch WITHOUT _opsGate, so a request
        // can be armed while this scan is running, and the completion below must not clear one this scan started
        // too early to have serviced.
        long armingGeneration = _core?.WholeRepoScanArmingGeneration ?? 0;

        ExtractReport report;
        try
        {
            report = ops.Scan(attempt.EffectiveIntent, attempt.Jobs);
        }
        catch (Exception ex)
        {
            RecordScanFailure(attempt.EffectiveIntent, attempt, ex);
            _logger.LogWarning(ex,
                "{Source} {Kind} scan failed; keeping the prior index (the next scan/poll reconciles).",
                source, DescribeScanKind(attempt));
            return ScanOutcome.Failed;
        }

        if (ExtractReportLog.DescribeWarning(report) is { } warning)
            _logger.LogWarning(
                "{Source} {Kind} scan: {Warning}", source, DescribeScanKind(attempt), warning);

        if (attempt.Downgraded)
            FailurePolicy.RecordDowngradedServe();
        else
            FailurePolicy.RecordSuccess(attempt.EffectiveIntent);

        // This whole-repo scan ran OUTSIDE IndexerCore's drain, so tell the core it happened or the latch that
        // would have run it stays armed and the next tick rebuilds the same repo a second time. Intent-aware, so
        // a downgraded run discharges only the delta it actually performed.
        _core?.NoteWholeRepoScanCompleted(attempt.EffectiveIntent, armingGeneration);

        // Converge derived sidecars after a successful scan, still under _opsGate. Deliberately OUTSIDE the
        // scan's try/catch so sidecar issues can never be misreported as scan failures. Some pure unit seams
        // publish fake ops without seeding bootstrap workspace state; those still test scan dispatch only.
        if (TryGetWorkspaceForSidecarConvergence() is { CanonicalExtractDbPath: { } symbolsDbPath })
            TryConvergeSidecar(symbolsDbPath, report, fullRebuild: true);

        if (!attempt.Downgraded)
            return ScanOutcome.Scanned(report);

        // A DOWNGRADED rebuild served the prior artifact instead of rebuilding it. Re-arming here, rather than
        // trusting each caller to, is what makes "the force scan is still owed" true of this method: the pending
        // intent was just NOT discharged above, and every exit from here reports Downgraded so no caller can
        // mistake the delta for the rebuild.
        string downgradeReason = ScanFailurePolicy.DescribeDowngrade(intent, attempt);
        _logger.LogWarning(
            "{Source} scan was downgraded to a delta reconcile after {Failures} consecutive failures; serving " +
            "the existing artifact with degraded freshness until the rebuild succeeds.",
            source, attempt.ConsecutiveFailures);
        _core?.RequestWholeRepoScan(intent);
        return ScanOutcome.Downgraded(report, downgradeReason);
    }

    private static string DescribeScanKind(ScanAttemptDecision decision) =>
        ScanFailurePolicy.DescribeIntent(decision.EffectiveIntent);

    // The jobs value recorded is the one the attempt actually ran with, so a post-SIGKILL clamp is visible in the
    // record rather than inferred from the ambient environment a later reader would resolve differently.
    private void RecordScanFailure(ScanIntent intent, ScanAttemptDecision decision, Exception error) =>
        FailurePolicy.RecordFailure(
            intent,
            JulieExtractException.ExitCodeOf(error),
            decision.Jobs ?? ExtractJobsPolicy.FromEnvironment());

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
                new WatchEventQueue(), ops, _ => true, _loggerFactory.CreateLogger<IndexerCore>(),
                FailurePolicy);
        }
    }

    /// <summary>
    /// Test seam: bind the scan-failure policy this service and its core consult, as the production leadership
    /// claim does from the workspace's <c>.miller</c> directory. Call BEFORE
    /// <see cref="PublishOpsForTest"/> so the core is built over the same instance. Not used in production.
    /// </summary>
    internal void PublishFailurePolicyForTest(IScanFailurePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _failurePolicy = policy;
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
    internal void DrainForTest(bool headChanged, bool wholeRepoScanAdmitted = true)
    {
        IndexerCore core = _core
            ?? throw new InvalidOperationException("PublishOpsForTest must run before DrainForTest.");
        lock (_opsGate)
        {
            bool processed = core.DrainAndProcess(headChanged, wholeRepoScanAdmitted, out bool usedWholeRepoScan);
            if (processed)
                TryConvergeSidecarToLatest(_bootstrap.Workspace.CanonicalExtractDbPath, fullRebuild: usedWholeRepoScan);
        }
    }

    /// <summary>
    /// Test seam: run ONE debounce tick's production body — leader request files, the governed peek/acquire, and
    /// the drain — so a test can prove the real admission expression rather than a hand-supplied boolean. Not
    /// used in production.
    /// </summary>
    internal void RunDrainTickForTest(string millerDir) =>
        RunDrainTick(millerDir, _bootstrap.Workspace);

    /// <summary>
    /// Test seam: invoked between the drain tick's scan-admission peek and the drain itself. The peek reads
    /// IndexerCore's flags under its own gate and cannot exclude a watcher thread arming them a moment later, so
    /// this is how a test reproduces that race deterministically and proves the drain refuses rather than running
    /// an ungoverned scan. Not used in production.
    /// </summary>
    internal Action? BetweenScanPeekAndDrainForTest { get; set; }

    /// <summary>
    /// Test seam: run the leadership-acquisition delta scan exactly as the production claim path does. Not used
    /// in production.
    /// </summary>
    internal void RunStartupDeltaScanForTest(WorkspaceContext workspace) => RunStartupDeltaScan(workspace);

    /// <summary>
    /// Test seam: run the D3 extractor-upgrade forced rescan exactly as the production claim path does. Not used
    /// in production.
    /// </summary>
    internal void RunExtractorUpgradeRescanForTest() => RunExtractorUpgradeRescan();

    /// <summary>
    /// Test seam: arm the whole-repo rescan latch on the published core, as the production re-arm sites do.
    /// </summary>
    internal void RequestWholeRepoScanForTest(ScanIntent intent) => _core?.RequestWholeRepoScan(intent);

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

    internal bool ProcessLeaderHandoffRequestsForTest(string millerDir)
    {
        if (_leadership.EvaluateLeaderHandoffRequests(millerDir, LogRequestDrainStats) is not { } decision)
            return false;
        AbdicateLeadershipForExplicitHandoff(
            millerDir,
            decision.RequesterPid,
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
