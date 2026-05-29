using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Core.Freshness;
using Miller.Indexing;

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
    private static readonly TimeSpan LeaderRetryInterval = TimeSpan.FromSeconds(5);

    private readonly IndexBootstrapService _bootstrap;
    private readonly ILogger<IndexerService> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private SingleWriterLock? _lease;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _gitHeadWatcher;
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
        IndexBootstrapService bootstrap, ILogger<IndexerService> logger, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _bootstrap = bootstrap;
        _logger = logger;
        _loggerFactory = loggerFactory;
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
            while (!stoppingToken.IsCancellationRequested && _lease is null)
            {
                _lease = SingleWriterLock.TryAcquire(millerDir);
                if (_lease is null)
                {
                    _logger.LogInformation(
                        "Not the indexer leader (another miller holds the lock); idling as a reader.");
                    await Task.Delay(LeaderRetryInterval, stoppingToken).ConfigureAwait(false);
                }
            }

            if (stoppingToken.IsCancellationRequested)
                return;

            // --- leader: build the dispatch core + attach the watchers ---
            var runner = JulieExtractRunner.Locate(workspace.ToolsRoot);
            // Pass the CANONICAL db (verified-fact 4): the single-file update/delete ops require an
            // already-canonical --db (the runner no longer GetFullPath-mangles it).
            IExtractOps ops = JulieExtractOps.Create(canonicalRoot, canonicalDbPath, runner);
            lock (_opsGate)
                _ops = ops; // publish for M6 write-through (TryReindexAsLeader)
            _core = new IndexerCore(
                new WatchEventQueue(), ops, File.Exists,
                _loggerFactory.CreateLogger<IndexerCore>());

            AttachWatchers(canonicalRoot);
            _logger.LogInformation("Indexer leader: watching {Root} (recursive) + .git/HEAD.", canonicalRoot);

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
                    _core.DrainAndProcess(headChanged);
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
            _lease?.Dispose();
            _lease = null;
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
                ops.Update(path);
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
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (_core is null || !WatchPathFilter.ShouldProcess(_bootstrap.Workspace.CanonicalRoot!, e.FullPath))
            return;
        // Drop directory events: julie operates on files; a dir create/delete surfaces as per-file events too.
        if (Directory.Exists(e.FullPath))
            return;
        _core.Enqueue(WatcherEventMapper.Map(e.ChangeType, e.FullPath));
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (_core is null)
            return;
        string root = _bootstrap.Workspace.CanonicalRoot!;
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
    }
}
