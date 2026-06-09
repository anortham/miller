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
    // julie's debounce tick: collect a burst, then drain once (decision §Components/3, "~1s, julie's tick").
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromSeconds(1);

    // How often a non-leader re-tries the writer lock so it can take over after the leader exits (failover).
    private static readonly TimeSpan DefaultLeaderRetryInterval = TimeSpan.FromSeconds(5);

    private readonly IndexBootstrapService _bootstrap;
    private readonly ILogger<IndexerService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<string, IDisposable?> _tryAcquireLeadership;
    private readonly Func<WorkspaceContext, string, string, IExtractOps> _createOps;
    private readonly Func<string, bool> _drainFullScanRequests;
    private readonly TimeSpan _leaderRetryInterval;
    private readonly bool _attachFileWatchers;

    // The on-disk search.db sidecar. Default ON. When enabled, THIS instance — the writer-lock leader — is the one
    // safe writer for the CURRENT workspace's search.db, so it converges it after scans and per-file updates under
    // _opsGate.
    private readonly SymbolSearchSidecar _sidecar;
    private readonly ContentCorpusSidecar _contentSidecar;

    private IDisposable? _lease;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _gitHeadWatcher;
    private readonly List<FileSystemWatcher> _ancestorIgnorePolicyWatchers = new();
    private IndexerCore? _core;

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
            drainFullScanRequests: LeaderScanRequestQueue.DrainFullScanRequests)
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
        Func<string, bool>? drainFullScanRequests = null)
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
        _tryAcquireLeadership = tryAcquireLeadership;
        _createOps = createOps;
        _drainFullScanRequests = drainFullScanRequests ?? LeaderScanRequestQueue.DrainFullScanRequests;
        _leaderRetryInterval = leaderRetryInterval;
        _sidecar = sidecar;
        _contentSidecar = contentSidecar ?? new ContentCorpusSidecar();
        _attachFileWatchers = attachFileWatchers;
    }

    /// <summary>True once this instance holds the writer lock and is running the watcher. For diagnostics/tests.</summary>
    public bool IsLeader => _lease is not null;

    /// <summary>
    /// Whether the coalescing queue currently holds no pending events — the second half of <c>index_fresh</c>
    /// (decision-8). A non-leader instance has no watcher/queue, so it is vacuously empty (true); a leader
    /// reports its live queue count. Read by <see cref="IndexFreshProbe"/>.
    /// </summary>
    public bool QueueEmpty => _core is null || _core.Queue.Count == 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workspace = _bootstrap.Workspace;
        string canonicalRoot = workspace.CanonicalRoot
            ?? throw new InvalidOperationException(
                "IndexerService started before the bootstrap resolved the canonical root.");
        string canonicalDbPath = workspace.CanonicalExtractDbPath
            ?? throw new InvalidOperationException(
                "IndexerService started before the bootstrap resolved the canonical extract DB path.");
        string millerDir = Path.GetDirectoryName(workspace.ExtractDbPath)!;

        try
        {
            // --- leader election: poll until we win the lock (or are asked to stop) ---
            // M8 §D4: log the transition into the reader role ONCE at Information (the meaningful state change),
            // then each subsequent failed re-try at Debug ("still a reader") so the 5s failover poll does not spam
            // Information forever. Becoming the leader below is the other transition, logged at Information.
            bool announcedReader = false;
            while (!stoppingToken.IsCancellationRequested && _lease is null)
            {
                _lease = _tryAcquireLeadership(millerDir);
                if (_lease is null)
                {
                    if (!announcedReader)
                    {
                        _logger.LogInformation(
                            "Not the indexer leader (another miller holds the lock); idling as a reader.");
                        announcedReader = true;
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Still a reader (the writer lock is still held); will re-try in {RetrySeconds}s.",
                            _leaderRetryInterval.TotalSeconds);
                    }
                    await Task.Delay(_leaderRetryInterval, stoppingToken).ConfigureAwait(false);
                }
            }

            if (stoppingToken.IsCancellationRequested)
                return;

            // --- leader: build the dispatch core + attach the watchers ---
            // Pass the CANONICAL db (verified-fact 4): the single-file update/delete ops require an
            // already-canonical --db (the runner no longer GetFullPath-mangles it).
            IExtractOps ops = _createOps(workspace, canonicalRoot, canonicalDbPath);
            lock (_opsGate)
                _ops = ops; // publish for M6 write-through (TryReindexAsLeader)
            _core = new IndexerCore(
                new WatchEventQueue(), ops, File.Exists,
                _loggerFactory.CreateLogger<IndexerCore>());

            if (_attachFileWatchers)
                AttachWatchers(canonicalRoot);
            // M8 §D2: this instance won the lease — flip the live log role to leader so every subsequent log line
            // (human + jsonl) is tagged role=leader, distinguishing it from the reader instances sharing the logs
            // directory. Readers leave the startup default (reader) untouched.
            MillerRole.SetLeader();
            if (_attachFileWatchers)
                _logger.LogInformation("Indexer leader: watching {Root} (recursive) + .git/HEAD.", canonicalRoot);

            // Yield once so BackgroundService.StartAsync can return after the watcher is attached: existing DBs
            // are available immediately as loaded_existing, while this leader reconciles missed downtime edits in
            // the background before the first debounce tick.
            await Task.Yield();
            if (stoppingToken.IsCancellationRequested)
                return;

            RunStartupDeltaScan(workspace);

            // --- debounce loop: drain on each tick (collects bursts into a single coalesced batch) ---
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(DebounceInterval, stoppingToken).ConfigureAwait(false);

                bool headChanged;
                lock (_headGate)
                {
                    headChanged = _headChanged;
                    _headChanged = false;
                }

                try
                {
                    TryProcessLeaderFullScanRequests(millerDir);

                    // Hold _opsGate across the drain so the debounce-loop drain and the on-demand Try* scans
                    // (TryScanAsLeader / TryReindexAsLeader) share ONE serialization: two julie `extract`
                    // subprocesses must never run against the same .miller DB at once (the M3 single-writer
                    // corruption guard, D3). DrainAndProcess additionally serializes the queue on IndexerCore's
                    // own gate; the lock order is always _opsGate -> _core gate (the Try* methods never take the
                    // core gate, and the watcher enqueue only takes the core gate), so there is no inversion.
                    lock (_opsGate)
                    {
                        bool processed = _core.DrainAndProcess(headChanged, out bool usedWholeRepoScan);
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
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            DisposeWatchers();
            lock (_opsGate)
                _ops = null; // stop offering inline write-through once we step down
            // M8 §D4: the leadership-loss transition at Debug (the inverse of the "Indexer leader: watching ..."
            // Information line above). On a normal shutdown the host is stopping anyway; if a future failover ever
            // releases the lease while running, this records the role change. M8 §D2: flip the live log role back
            // to reader so any line emitted after step-down is tagged honestly (no stale role=leader).
            if (_lease is not null)
            {
                _logger.LogDebug("Indexer stepping down: releasing the writer lock.");
                MillerRole.SetReader();
            }
            _lease?.Dispose();
            _lease = null;
        }
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

            IndexBootstrapService.MarkRegistryScanned(workspace, stableWorkspaceId, report.Revision);
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
        bool requested;
        try
        {
            requested = _drainFullScanRequests(millerDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Leader full-scan request drain failed; will retry on a later tick.");
            return false;
        }

        if (!requested)
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
        if (!_sidecar.Enabled)
            return;
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

    // search.db convergence for the CURRENT workspace. Enabled-and-stale ⇒ incremental update from julie's
    // revision_file_changes; missing/corrupt/schema-stale ⇒ full repair rebuild; off/already-fresh/no-revision
    // ⇒ no-op. MUST be called holding _opsGate: it reads symbols.db, which a concurrent extract could replace.
    private void TryConvergeSidecar(string? symbolsDbPath, long revision, bool fullRebuild)
    {
        if (symbolsDbPath is null || revision <= 0)
            return; // no symbols.db path or no revision cursor to stamp; the next revision-bearing op builds it

        string workspaceRoot = _bootstrap.Workspace.CanonicalRoot ?? _bootstrap.Workspace.WorkspaceRoot;
        string? workspaceId = _bootstrap.Workspace.WorkspaceId;
        try
        {
            if (_contentSidecar.EnsureBuilt(symbolsDbPath, workspaceRoot, workspaceId, revision))
                _logger.LogInformation("Converged content corpus sidecar at revision {Revision}.", revision);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or IncompatibleExtractException)
        {
            _logger.LogWarning(ex,
                "Content corpus sidecar convergence failed; source text search will remain unavailable or stale until the next successful convergence.");
        }

        if (!_sidecar.Enabled)
            return;

        try
        {
            bool changed = fullRebuild
                ? _sidecar.EnsureBuilt(symbolsDbPath, revision, workspaceRoot)
                : _sidecar.EnsureCurrent(symbolsDbPath, revision, workspaceRoot);
            if (changed)
                _logger.LogInformation("Converged search sidecar at revision {Revision}.", revision);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or IncompatibleExtractException)
        {
            _logger.LogWarning(ex,
                "Search sidecar convergence failed; the sidecar will remain unavailable or stale until the next successful convergence.");
        }
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
            core.DrainAndProcess(headChanged);
    }

    /// <summary>
    /// Test seam: drain leader full-scan request files and service them through the same force-scan path the
    /// production debounce loop uses. Requires <see cref="PublishOpsForTest"/> when the caller expects a scan.
    /// Not used in production.
    /// </summary>
    internal bool ProcessLeaderFullScanRequestsForTest(string millerDir) =>
        TryProcessLeaderFullScanRequests(millerDir);

    private void AttachWatchers(string canonicalRoot)
    {
        _watcher = new FileSystemWatcher(canonicalRoot)
        {
            IncludeSubdirectories = true,
            // Watch the change kinds that mean "content/structure moved". LastWrite catches edits; FileName
            // catches create/delete/rename; DirectoryName catches dir moves that carry files.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024, // largest the OS allows; overflow still self-heals via Error->scan
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;

        // A dedicated watch on .git/HEAD: a branch switch/checkout flips HEAD once; we force ONE scan reconcile
        // instead of processing the thousands of per-file events a checkout produces (decision-7). The .git
        // dir is excluded from the main watcher by WatchPathFilter, so this is the only HEAD signal.
        string gitDir = Path.Combine(canonicalRoot, ".git");
        if (Directory.Exists(gitDir))
        {
            _gitHeadWatcher = new FileSystemWatcher(gitDir, "HEAD")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _gitHeadWatcher.Changed += OnHeadChanged;
            _gitHeadWatcher.Created += OnHeadChanged;
            _gitHeadWatcher.Renamed += OnHeadChanged;
            _gitHeadWatcher.EnableRaisingEvents = true;
        }

        foreach (string ignoreFile in WorkspaceIgnorePolicy.AncestorGitignoreFilesOutsideRoot(canonicalRoot))
        {
            string? directory = Path.GetDirectoryName(ignoreFile);
            if (directory is null || !Directory.Exists(directory))
                continue;

            var watcher = new FileSystemWatcher(directory, ".gitignore")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            watcher.Changed += OnIgnorePolicyChanged;
            watcher.Created += OnIgnorePolicyChanged;
            watcher.Deleted += OnIgnorePolicyChanged;
            watcher.Renamed += OnIgnorePolicyChanged;
            watcher.EnableRaisingEvents = true;
            _ancestorIgnorePolicyWatchers.Add(watcher);
        }
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
        if (!WatchPathFilter.ShouldProcess(root, fullPath))
            return;
        // Drop directory events: julie operates on files; a dir create/delete surfaces as per-file events too.
        if (Directory.Exists(fullPath))
            return;
        _core.Enqueue(WatcherEventMapper.Map(changeType, fullPath));
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

        bool oldOk = WatchPathFilter.ShouldProcess(root, e.OldFullPath);
        bool newOk = WatchPathFilter.ShouldProcess(root, e.FullPath);

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
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChanged;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }
        if (_gitHeadWatcher is not null)
        {
            _gitHeadWatcher.EnableRaisingEvents = false;
            _gitHeadWatcher.Changed -= OnHeadChanged;
            _gitHeadWatcher.Created -= OnHeadChanged;
            _gitHeadWatcher.Renamed -= OnHeadChanged;
            _gitHeadWatcher.Dispose();
            _gitHeadWatcher = null;
        }
        foreach (var watcher in _ancestorIgnorePolicyWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnIgnorePolicyChanged;
            watcher.Created -= OnIgnorePolicyChanged;
            watcher.Deleted -= OnIgnorePolicyChanged;
            watcher.Renamed -= OnIgnorePolicyChanged;
            watcher.Dispose();
        }
        _ancestorIgnorePolicyWatchers.Clear();
    }
}
