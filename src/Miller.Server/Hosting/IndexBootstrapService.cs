using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Indexing;
using Miller.Server.Hosting;
using Miller.Server.Logging;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;

namespace Miller.Server;

public enum BootstrapPhase
{
    Idle,
    Running,
    Bound,
    Failed,
}

public sealed record BootstrapSnapshot(
    BootstrapPhase Phase,
    string? CanonicalRoot,
    DateTimeOffset? StartedAtUtc,
    string? FailureMessage,
    string? LastFailureMessage,
    int RunGeneration);

internal enum BindOutcome
{
    Started,
    AlreadyBound,
    JoinedRunning,
    RebindDeferred,
}

/// <summary>
/// The startup bootstrap (M2 §7). Registered as an <see cref="IHostedService"/> BEFORE the MCP host so its
/// <see cref="StartAsync"/> runs to completion — building the in-memory index and opening the telemetry
/// ledger — before <c>WithStdioServerTransport</c>'s own hosted service starts accepting <c>tools/call</c>.
    /// It also holds the current workspace state (index, resolver, workspace context, ledger) which the DI container
    /// resolves through factory delegates. NOTE: the generic host CONSTRUCTS every hosted service before calling
/// <c>StartAsync</c> on any of them — registration order orders <c>StartAsync</c>, NOT construction. So the
/// getters below throw if read before this <see cref="StartAsync"/> completes, and no hosted-service constructor
/// may read them (the M3 services take only this bootstrap and read its getters lazily inside <c>ExecuteAsync</c>).
/// Tools are built per-call, well after <see cref="StartAsync"/>, so the holder is always populated for them.
///
/// Sequence: resolve the <see cref="WorkspaceContext"/> → create <c>&lt;root&gt;/.miller</c> → compute the stable
/// workspace id from the canonical root → locate the pinned julie-extract (fail loudly if absent) → scan when the
/// DB is missing or must be force-rebound to the stable id → read symbols → build the index → register the
/// workspace row → open the telemetry ledger + prune. The leader-only startup delta scan lives in
/// <see cref="Hosting.IndexerService"/>.
/// </summary>
public sealed class IndexBootstrapService : IHostedService, IDisposable
{
    private readonly ILogger<IndexBootstrapService> _logger;
    private readonly object _gate = new();

    private BoundWorkspace? _bound;
    private BootstrapPhase _phase = BootstrapPhase.Idle;
    private string? _snapshotRoot;
    private DateTimeOffset? _startedAtUtc;
    private string? _failureMessage;
    private string? _lastFailureMessage;
    private int _runGeneration;
    private TaskCompletionSource _runCompletion = CreateBindingGate();

    public IndexBootstrapService(ILogger<IndexBootstrapService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    private sealed record BoundWorkspace(
        IndexHolder Holder,
        SmartTargetResolver Resolver,
        WorkspaceContext Workspace,
        TelemetryLedger? Ledger);

    private sealed record BootstrapRunResult(
        BoundWorkspace Bound,
        string StableWorkspaceId,
        WorkspaceRegistryState RegistryStateAfterLoad,
        bool DidScan,
        long? ScanRevision,
        long BuiltRevision,
        int Pruned,
        long ElapsedMilliseconds,
        int DocumentCount,
        bool UsesExistingLedger);

    private BoundWorkspace CurrentBound =>
        Volatile.Read(ref _bound) ?? throw new InvalidOperationException("Holder requested before bootstrap completed.");

    /// <summary>
    /// The live index holder (M3): seeded with the initial index + its built revision; the freshness service
    /// swaps a fresh index in as the writer advances. Throws if accessed before <see cref="StartAsync"/> completes.
    /// </summary>
    public IndexHolder Holder => CurrentBound.Holder;

    /// <summary>The initially-built repository index. Prefer <see cref="Holder"/> for live freshness.</summary>
    public MillerRepositoryIndex Index => Holder.Current;

    public SmartTargetResolver Resolver => CurrentBound.Resolver;

    public WorkspaceContext Workspace => CurrentBound.Workspace;

    public TelemetryLedger Ledger =>
        CurrentBound.Ledger ?? throw new InvalidOperationException("TelemetryLedger requested before bootstrap completed.");

    /// <summary>True once a primary workspace has been bootstrapped (eager or deferred).</summary>
    public bool IsBound => Volatile.Read(ref _bound) is not null;

    /// <summary>True when startup deferred binding until MCP roots or the first tool call.</summary>
    public bool IsDeferred { get; private set; }

    /// <summary>Increments when the primary workspace is (re)bound; background services observe changes.</summary>
    public int BindingGeneration => Volatile.Read(ref _bindingGeneration);

    public BootstrapSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new BootstrapSnapshot(
                    _phase,
                    _phase == BootstrapPhase.Bound ? _bound?.Workspace.CanonicalRoot : _snapshotRoot,
                    _startedAtUtc,
                    _failureMessage,
                    _lastFailureMessage,
                    _runGeneration);
            }
        }
    }

    private int _bindingGeneration;
    private TaskCompletionSource _bindingReady = CreateBindingGate();

    private static TaskCompletionSource CreateBindingGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Awaits the first or subsequent primary workspace bind.</summary>
    public Task WaitUntilBoundAsync(CancellationToken cancellationToken)
    {
        if (IsBound)
            return Task.CompletedTask;
        return _bindingReady.Task.WaitAsync(cancellationToken);
    }

    public Task WaitForRunAsync(int runGeneration, CancellationToken cancellationToken)
    {
        Task wait;
        lock (_gate)
        {
            if (runGeneration <= 0 ||
                runGeneration != _runGeneration ||
                _phase != BootstrapPhase.Running)
            {
                return Task.CompletedTask;
            }

            wait = _runCompletion.Task;
        }

        return wait.WaitAsync(cancellationToken);
    }

    internal sealed record BootstrapScanDecision(
        bool ShouldScan,
        bool Force,
        WorkspaceRegistryState RegistryStateAfterLoad);

    /// <summary>
    /// The outcome of <see cref="LoadIndexWithAutoRebuild{T}"/>: the loaded index, whether a force-rebuild was
    /// needed to heal an incompatible DB, and (when rebuilt) the revision the rebuild scan produced — which the
    /// caller folds into the holder's seed revision and the registry's scanned-at bookkeeping.
    /// </summary>
    internal sealed record IndexLoadResult<T>(T Index, bool Rebuilt, long? RebuiltRevision);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var startup = WorkspaceBindingResolver.TryResolveStartup(Environment.CurrentDirectory);
        if (startup is not null)
        {
            BootstrapForRoot(startup.Path, startup.Source);
            return Task.CompletedTask;
        }

        IsDeferred = true;
        _logger.LogInformation(
            "Deferring workspace bootstrap until MCP client roots or the first tool call " +
            "(startup cwd {Cwd} is not a usable workspace root).",
            Environment.CurrentDirectory);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test-only hook: when set, invoked instead of <see cref="RunBootstrap"/> after canonical-root resolution.
    /// Return true when the interceptor fully handled binding.
    /// </summary>
    internal Func<string, WorkspaceBindingResolver.WorkspaceSource, bool>? TestBootstrapInterceptor { get; set; }

    internal Action<string>? TestRunBootstrapOverride { get; set; }

    /// <summary>
    /// Bind and bootstrap the primary workspace. Idempotent when already bound to the same canonical root;
    /// re-bootstraps when the canonical root changes (MCP roots/list_changed).
    /// </summary>
    internal BindOutcome BootstrapForRoot(string workspaceRoot, WorkspaceBindingResolver.WorkspaceSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        // EVERY binding source passes the sensitive-root guard, not just Cwd. A session launched with
        // cwd=$HOME defers bootstrap correctly, but the MCP client then offers file://$HOME as its root —
        // trusting the Roots (or Env) source unguarded kicked off a full home-directory scan (2026-07-06).
        string canonicalRoot = WorkspaceRootSafety.CanonicalizeAndRejectSensitiveRoot(
            workspaceRoot, fromCwd: source == WorkspaceBindingResolver.WorkspaceSource.Cwd);

        int runGeneration = 0;
        bool dispatch = false;
        lock (_gate)
        {
            if (_phase == BootstrapPhase.Running)
            {
                if (RootPathsEqual(_snapshotRoot, canonicalRoot))
                    return BindOutcome.JoinedRunning;

                _logger.LogWarning(
                    "Workspace rebind to {NewRoot} deferred while bootstrap for {RunningRoot} is still running.",
                    canonicalRoot, _snapshotRoot ?? "(unknown)");
                return BindOutcome.RebindDeferred;
            }

            var bound = _bound;
            if (bound?.Workspace.CanonicalRoot is not null &&
                RootPathsEqual(bound.Workspace.CanonicalRoot, canonicalRoot))
            {
                if (_phase == BootstrapPhase.Failed)
                {
                    _phase = BootstrapPhase.Bound;
                    _snapshotRoot = bound.Workspace.CanonicalRoot;
                    _startedAtUtc = null;
                    _failureMessage = null;
                    _lastFailureMessage = null;
                }
                return BindOutcome.AlreadyBound;
            }

            if (bound is not null)
            {
                _logger.LogInformation(
                    "Rebinding primary workspace from {OldRoot} to {NewRoot}.",
                    bound.Workspace.CanonicalRoot ?? "(unknown)", canonicalRoot);
            }

            if (TestBootstrapInterceptor?.Invoke(canonicalRoot, source) == true)
                return BindOutcome.Started;

            runGeneration = StartRunLocked(canonicalRoot);
            dispatch = true;
        }

        if (dispatch)
            _ = Task.Run(() => RunBootstrapInBackground(canonicalRoot, source, runGeneration));

        return BindOutcome.Started;
    }

    private int StartRunLocked(string canonicalRoot)
    {
        if (_phase != BootstrapPhase.Failed)
            _lastFailureMessage = null;

        _phase = BootstrapPhase.Running;
        _snapshotRoot = canonicalRoot;
        _startedAtUtc = DateTimeOffset.UtcNow;
        _failureMessage = null;
        _runGeneration++;
        _runCompletion = CreateBindingGate();
        return _runGeneration;
    }

    private void SignalBoundLocked()
    {
        int gen = Interlocked.Increment(ref _bindingGeneration);
        var gate = Interlocked.Exchange(ref _bindingReady, CreateBindingGate());
        gate.TrySetResult();
        _runCompletion.TrySetResult();
        _logger.LogDebug("Primary workspace bound (generation {Generation}).", gen);
    }

    private void Run()
    {
        var startup = WorkspaceBindingResolver.TryResolveStartup(Environment.CurrentDirectory);
        if (startup is null)
            throw WorkspaceBindingResolver.CreateBindingFailureException();
        BootstrapForRoot(startup.Path, startup.Source);
    }

    private void RunBootstrapInBackground(
        string canonicalRoot, WorkspaceBindingResolver.WorkspaceSource source, int runGeneration)
    {
        BootstrapRunResult? result = null;
        bool published = false;
        try
        {
            if (TestRunBootstrapOverride is { } runOverride)
            {
                runOverride(canonicalRoot);
                return;
            }

            result = RunBootstrap(canonicalRoot, source);
            published = PublishBoundWorkspace(result, runGeneration);
        }
        catch (Exception ex)
        {
            if (result is not null && !published && !result.UsesExistingLedger)
                result.Bound.Ledger?.Dispose();
            MarkBootstrapFailed(canonicalRoot, runGeneration, ex);
        }
    }

    private BootstrapRunResult RunBootstrap(string canonicalRoot, WorkspaceBindingResolver.WorkspaceSource source)
    {
        // The telemetry ledger is opened late but must be disposed if ANY later step throws (otherwise the
        // ledger stays open + the telemetry DB locked, but is never assigned to _ledger so Dispose() misses
        // it). Track it in a local and dispose on failure before the exception propagates.
        TelemetryLedger? ledger = null;
        bool usesExistingLedger = false;
        try
        {
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var ctx = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory);

            string canonicalDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            string stableWorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
            string millerDir = Path.GetDirectoryName(canonicalDbPath)!;
            Directory.CreateDirectory(millerDir);

            // Locate the pinned julie-extract under the tools root (NOT the repo cwd). Absent → fail loudly
            // (FileNotFoundException carrying the restore-script message) — Miller cannot index without it.
            var runner = JulieExtractRunner.Locate(ctx.ToolsRoot);

            // Locate only checks EXISTENCE, so a julie-extract left in .tools/ from before a pin bump passes it
            // but then fails every scan with a schema mismatch. Probe the bundled version up front and warn
            // loudly when it is older than the pin, so the operator sees the real cause ("re-run restore")
            // instead of inferring it from a downstream schema-mismatch loop (the binary is PRESENT, so the
            // "missing binary" hint misleads). Diagnostic only — the schema/contract gates still decide
            // compatibility (a newer/same-contract binary is fine), so this never blocks startup.
            if (JulieExtractVersionProbe.StaleBinaryWarning(runner.QueryVersion()) is { } staleBinaryWarning)
                _logger.LogWarning("julie-extract: {Warning}", staleBinaryWarning);

            // Initial bootstrap scan decision. A missing DB gets the first delta scan. An existing DB with a
            // missing/legacy/mismatched workspace_id is force-rebound before Miller loads it, so the in-memory
            // index, freshness cursor, registry row, and julie metadata all converge on the stable root hash.
            long? scanRevision = null;
            bool dbExists = File.Exists(canonicalDbPath);
            // v1 identity is the recorded canonical root_path, not a stored workspace_id (reconciliation #14).
            string? existingRootPath = dbExists ? ExtractReader.ReadRootPath(canonicalDbPath) : null;
            var scanDecision = DecideBootstrapScan(dbExists, existingRootPath, canonicalRoot);
            if (scanDecision.ShouldScan)
            {
                if (scanDecision.Force)
                {
                    _logger.LogInformation(
                        "Extract DB at {Db} has root_path={ExistingRootPath}; force-scanning {Root} with stable workspace_id={StableWorkspaceId}.",
                        canonicalDbPath, existingRootPath ?? "(missing)", canonicalRoot, stableWorkspaceId);
                }
                else
                {
                    _logger.LogInformation(
                        "No extract DB at {Db}; scanning {Root} with stable workspace_id={StableWorkspaceId}.",
                        canonicalDbPath, canonicalRoot, stableWorkspaceId);
                }
                var report = runner.Scan(canonicalRoot, canonicalDbPath, scanDecision.Force);
                scanRevision = report.Revision;
                _logger.LogInformation(
                    "Scan complete: {Symbols} symbols extracted (revision {Rev}).",
                    report.SymbolsExtracted, report.Revision);
                // A partial scan (some files failed to parse) is a CONSISTENT artifact loaded with rows
                // missing — surface it as a WARNING so the clean "Scan complete" above never hides silent
                // symbol loss (julie-extract migration review finding).
                if (PartialExtractLog.DescribePartial(report) is { } partial)
                    _logger.LogWarning("Bootstrap scan: {Partial}", partial);
            }
            else
            {
                _logger.LogInformation("Reusing existing extract DB at {Db}.", canonicalDbPath);
            }

            // Read → build the in-memory index + dependency graph as one unit via the single production path
            // (M5 D9; read-path opens the same DB file; canonical is fine). The bootstrap and the freshness
            // rebuild both route through RepositoryIndexLoader so each gets the graph identically.
            //
            // AUTO-HEAL: a reused DB whose root_path matched (so DecideBootstrapScan did NOT rescan) can still
            // be an INCOMPATIBLE artifact — e.g. a julie-extract schema/contract bump raised the expected
            // version since the DB was written. Rather than crash the whole host (which surfaces to the client
            // as "MCP failed to connect"), force-rebuild the index ONCE with the bundled julie-extract and
            // reload. A second incompatibility means the bundled tool does not match this build — fail loudly.
            var loadResult = LoadIndexWithAutoRebuild(
                load: () => RepositoryIndexLoader.Load(canonicalDbPath),
                forceRescan: () =>
                {
                    var rebuild = runner.Scan(canonicalRoot, canonicalDbPath, force: true);
                    _logger.LogInformation(
                        "Auto-rebuild scan complete: {Symbols} symbols extracted (revision {Rev}).",
                        rebuild.SymbolsExtracted, rebuild.Revision);
                    if (PartialExtractLog.DescribePartial(rebuild) is { } partial)
                        _logger.LogWarning("Auto-rebuild scan: {Partial}", partial);
                    return rebuild.Revision;
                },
                // The rebuild replaced the DB file; drop pooled read connections so the retry below opens a
                // fresh handle on the rebuilt artifact instead of re-reading the old inode's stale snapshot.
                onBeforeRetry: SqliteConnection.ClearAllPools,
                onIncompatible: ex => _logger.LogWarning(
                    "Existing extract DB at {Db} is incompatible ({Message}); force-rebuilding once with the " +
                    "bundled julie-extract.",
                    canonicalDbPath, ex.Message),
                onCorrupt: ex => _logger.LogWarning(
                    "Existing extract DB at {Db} is corrupt ({Message}); force-rebuilding once with the bundled " +
                    "julie-extract (a writer likely died mid-scan).",
                    canonicalDbPath, ex.Message));
            var index = loadResult.Index;

            // An auto-rebuild counts as a scan for the holder's seed revision + the registry's scanned-at
            // bookkeeping below, even though DecideBootstrapScan chose to reuse: julie just (re)wrote the DB.
            bool didScan = scanDecision.ShouldScan || loadResult.Rebuilt;
            if (loadResult.Rebuilt)
                scanRevision = loadResult.RebuiltRevision;

            // The workspace id is derived from the canonical root (stableWorkspaceId), NOT read back from the
            // DB: v1 stores no workspace_id, and the pre-load DecideBootstrapScan already force-rescanned any
            // DB whose recorded root_path did not match this root (reconciliation #14 — the post-load workspace_id
            // assertion is gone with the metadata key).
            var workspace = ctx with
            {
                WorkspaceId = stableWorkspaceId,
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = canonicalDbPath,
            };

            // Seed the holder's BuiltRevision: the scan report's revision when we just scanned, else the
            // latest persisted revision (a reused DB) so the freshness poll does not rebuild on first tick.
            long builtRevision = scanRevision
                ?? ReadLatestRevisionOrZero(canonicalDbPath, stableWorkspaceId);

            // Open the SEPARATE, writable telemetry ledger (never the read-only extract) + prune old rows.
            // The ledger is MACHINE-GLOBAL (shared <home>/.miller/telemetry.db across every workspace), so its
            // directory is NOT the per-repo .miller created above — ensure it exists before opening. Each row
            // is stamped with this workspace's id + root so the shared ledger stays attributable.
            // OpenAndPrune disposes the just-opened ledger if Prune throws, so a prune failure cannot leak
            // the open connection (the outer catch covers every OTHER post-open step the same way).
            Directory.CreateDirectory(Path.GetDirectoryName(workspace.TelemetryDbPath)!);
            int pruned;
            var existingLedger = Volatile.Read(ref _bound)?.Ledger;
            if (existingLedger is null)
            {
                ledger = OpenAndPrune(
                    workspace.TelemetryDbPath, stableWorkspaceId, workspace.WorkspaceRoot, retentionDays: 30, out pruned);
            }
            else
            {
                ledger = existingLedger;
                usesExistingLedger = true;
                pruned = 0;
            }

            // Seed the artifact identity alongside the revision: a later full rebuild PROMOTES a fresh file
            // whose restarted revision counter can land at or below builtRevision, and the freshness poll
            // detects that replacement by the artifact_id changing (FreshnessPoller). Without the seed the
            // held id is unknown and an exact-revision-tie rebuild would go unnoticed.
            var holder = new IndexHolder(index, builtRevision, ReadArtifactIdOrNull(canonicalDbPath));
            var resolver = new SmartTargetResolver(holder); // holder-backed: live freshness per call (M3 step 10)

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            return new BootstrapRunResult(
                new BoundWorkspace(holder, resolver, workspace, ledger),
                stableWorkspaceId,
                scanDecision.RegistryStateAfterLoad,
                didScan,
                scanRevision,
                builtRevision,
                pruned,
                (long)elapsed.TotalMilliseconds,
                index.DocumentCount,
                usesExistingLedger);
        }
        catch
        {
            // Partial-initialization cleanup: if the ledger was opened but a later step threw, dispose it so
            // the telemetry DB is not left locked / its WAL in an indeterminate state. _ledger was not yet
            // assigned, so Dispose() would otherwise leak it.
            if (ledger is not null && !usesExistingLedger)
                ledger.Dispose();
            throw;
        }
    }

    private bool PublishBoundWorkspace(BootstrapRunResult result, int runGeneration)
    {
        lock (_gate)
        {
            if (_phase != BootstrapPhase.Running || _runGeneration != runGeneration)
                return false;

            if (result.DidScan)
                MarkRegistryScanned(result.Bound.Workspace, result.StableWorkspaceId, result.ScanRevision ?? result.BuiltRevision);
            else
                RegisterBootstrapWorkspace(
                    result.Bound.Workspace,
                    result.StableWorkspaceId,
                    result.RegistryStateAfterLoad,
                    result.BuiltRevision);

            if (result.UsesExistingLedger)
                result.Bound.Ledger?.RebindWorkspace(result.StableWorkspaceId, result.Bound.Workspace.WorkspaceRoot);

            _bound = result.Bound;
            _phase = BootstrapPhase.Bound;
            _snapshotRoot = result.Bound.Workspace.CanonicalRoot;
            _startedAtUtc = null;
            _failureMessage = null;
            _lastFailureMessage = null;
            SignalBoundLocked();

            _logger.LogInformation(
                "Bootstrap ready: {Count} symbols indexed at revision {Rev}, workspace_id={Ws}, " +
                "{Pruned} telemetry rows pruned, in {Ms}ms.",
                result.DocumentCount, result.BuiltRevision, result.StableWorkspaceId, result.Pruned,
                result.ElapsedMilliseconds);
            return true;
        }
    }

    private void MarkBootstrapFailed(string canonicalRoot, int runGeneration, Exception error)
    {
        bool shouldMarkRegistry;
        lock (_gate)
        {
            if (_phase != BootstrapPhase.Running || _runGeneration != runGeneration)
                return;

            string message = error.Message;
            _phase = BootstrapPhase.Failed;
            _snapshotRoot = canonicalRoot;
            _failureMessage = message;
            _lastFailureMessage = message;
            _runCompletion.TrySetResult();
            shouldMarkRegistry = true;
        }

        _logger.LogError(error, "Bootstrap failed for {Root}.", canonicalRoot);
        if (!shouldMarkRegistry)
            return;

        try
        {
            string stableWorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
            string canonicalDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            Directory.CreateDirectory(Path.GetDirectoryName(canonicalDbPath)!);
            var workspace = WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory) with
            {
                WorkspaceId = stableWorkspaceId,
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = canonicalDbPath,
            };
            MarkRegistryError(workspace, stableWorkspaceId, error.Message);
        }
        catch (Exception markError)
        {
            _logger.LogWarning(markError, "Failed to mark bootstrap error for {Root}.", canonicalRoot);
        }
    }

    /// <summary>
    /// Open the telemetry ledger and prune in one step, GUARANTEEING the just-opened ledger is disposed if the
    /// prune throws (finding-4: an opened-but-unpublished ledger must never leak — it would hold the telemetry
    /// DB open with its WAL in an indeterminate state). On success returns the live ledger (the caller owns it);
    /// on failure disposes it and rethrows. The outer bootstrap try/catch covers every other post-open step.
    /// </summary>
    internal static TelemetryLedger OpenAndPrune(
        string telemetryDbPath, string? workspaceId, string? workspaceRoot, int retentionDays, out int pruned)
    {
        int prunedLocal = 0;
        var ledger = PrimeOrDispose(
            TelemetryLedger.Open(telemetryDbPath, workspaceId, workspaceRoot),
            l => prunedLocal = l.Prune(retentionDays));
        pruned = prunedLocal;
        return ledger;
    }

    /// <summary>
    /// Run a priming action against a freshly-acquired disposable resource, disposing it if priming throws so a
    /// half-initialized resource never leaks (finding-4). Returns the live resource on success (the caller owns
    /// disposal); rethrows on failure after disposing.
    /// </summary>
    internal static T PrimeOrDispose<T>(T resource, Action<T> prime) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(prime);
        try
        {
            prime(resource);
            return resource;
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decide whether bootstrap must (re)scan. v1 has no <c>workspace_id</c> metadata key, so artifact identity
    /// is the canonical <c>root_path</c> the artifact was extracted from (reconciliation #14): a missing DB delta-
    /// scans; a DB whose recorded root does NOT match the root Miller is indexing (a relocated/aliased workspace,
    /// or a pre-v1 DB with no <c>root_path</c>) is force-rescanned so the index, freshness cursor, and metadata all
    /// converge on the current root; a matching root reuses the existing DB.
    /// </summary>
    internal static BootstrapScanDecision DecideBootstrapScan(
        bool dbExists, string? existingRootPath, string canonicalRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);

        if (!dbExists)
            return new BootstrapScanDecision(
                ShouldScan: true, Force: false, WorkspaceRegistryState.Ready);

        if (!RootPathsEqual(existingRootPath, canonicalRoot))
            return new BootstrapScanDecision(
                ShouldScan: true, Force: true, WorkspaceRegistryState.Ready);

        return new BootstrapScanDecision(
            ShouldScan: false, Force: false, WorkspaceRegistryState.LoadedExisting);
    }

    /// <summary>
    /// Whether the DB's recorded <c>root_path</c> identifies the same workspace root Miller is indexing. Both
    /// julie (when it writes the artifact) and Miller (<see cref="PathCanonicalizer.CanonicalizeRoot"/>) record an
    /// absolute, symlink-resolved canonical root, but they do NOT spell it identically on Windows: Rust's
    /// <c>std::fs::canonicalize</c> emits the extended-length verbatim prefix (<c>\\?\C:\repo</c>) and reflects the
    /// on-disk casing, while Miller's canonical root strips that prefix and preserves the as-launched casing. So
    /// BOTH operands are normalized — verbatim prefix stripped
    /// (<see cref="PathCanonicalizer.StripWindowsVerbatimPrefix"/>), then compared case-insensitively on Windows
    /// and default macOS, and case-sensitively on Linux/POSIX — BEFORE the equality check. The normalization is
    /// pure string work: the recorded root is NOT re-canonicalized against the filesystem (it may not exist on this
    /// machine, e.g. a copied DB). A missing/empty recorded root (a pre-v1 artifact) never
    /// matches, forcing a clean rescan. Without this, a Windows workspace force-rescanned on every startup because
    /// <c>\\?\C:\repo</c> never matched <c>C:\repo</c> ordinally — a 30s+ rescan that tripped the MCP connect timeout.
    /// </summary>
    internal static bool RootPathsEqual(string? recordedRootPath, string canonicalRoot)
    {
        if (string.IsNullOrEmpty(recordedRootPath))
            return false;

        string recorded = PathCanonicalizer.StripWindowsVerbatimPrefix(recordedRootPath);
        string current = PathCanonicalizer.StripWindowsVerbatimPrefix(canonicalRoot);
        var comparison = RootPathComparison(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());
        return string.Equals(recorded, current, comparison);
    }

    internal static StringComparison RootPathComparison(bool isWindows, bool isMacOS) =>
        isWindows || isMacOS ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Load the in-memory index, AUTO-HEALING a reused-but-incompatible DB. <paramref name="load"/> runs first; if
    /// it throws <see cref="IncompatibleExtractException"/> — a stale schema/contract artifact that a root-matching
    /// reuse (<see cref="DecideBootstrapScan"/>) did not rescan, e.g. after a julie-extract schema bump raised the
    /// expected version — <paramref name="onIncompatible"/> is notified, <paramref name="forceRescan"/> rebuilds
    /// the DB from scratch with the bundled julie-extract, and the load is retried EXACTLY once. A second
    /// incompatibility propagates loudly: a freshly-rebuilt DB that is still incompatible means the bundled tool
    /// does not match this Miller build (an operator/config error a further rescan cannot fix), so we never loop.
    /// On the happy path neither <paramref name="forceRescan"/> nor <paramref name="onBeforeRetry"/> is invoked, so
    /// a healthy startup pays no extra scan. <paramref name="onBeforeRetry"/> runs between the rescan and the retry
    /// load — <see cref="Run"/> wires it to <c>SqliteConnection.ClearAllPools</c> because a force rebuild REPLACES
    /// the DB file (new inode), so the pooled read-only connection opened by the failed first load still points at
    /// the OLD inode and would re-read the stale incompatible snapshot; dropping pooled handles makes the retry open
    /// a fresh connection bound to the rebuilt file. Pure control flow over injected delegates — the unit test
    /// drives it with fakes; <see cref="Run"/> wires the real loader, runner, and pool barrier.
    /// </summary>
    internal static IndexLoadResult<T> LoadIndexWithAutoRebuild<T>(
        Func<T> load,
        Func<long?> forceRescan,
        Action onBeforeRetry,
        Action<IncompatibleExtractException> onIncompatible,
        Action<SqliteException> onCorrupt)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(forceRescan);
        ArgumentNullException.ThrowIfNull(onBeforeRetry);
        ArgumentNullException.ThrowIfNull(onIncompatible);
        ArgumentNullException.ThrowIfNull(onCorrupt);

        try
        {
            return new IndexLoadResult<T>(load(), Rebuilt: false, RebuiltRevision: null);
        }
        catch (IncompatibleExtractException ex)
        {
            // A stale-schema/contract artifact: notify, then force-rebuild once and reload.
            onIncompatible(ex);
            return RebuildAndRetry(load, forceRescan, onBeforeRetry);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            // A torn/truncated/half-written DB — e.g. the optional writer/indexer was killed (Ctrl-C, OOM, power
            // loss) mid-scan, leaving symbols.db malformed. Rather than crash startup (surfacing as "MCP failed to
            // connect"), force-rebuild once with the bundled julie-extract and reload — the same self-heal the
            // incompatible path uses. A SECOND corruption after rebuild escapes (we never loop).
            onCorrupt(ex);
            return RebuildAndRetry(load, forceRescan, onBeforeRetry);
        }
    }

    // SQLITE_CORRUPT (11) and SQLITE_NOTADB (26): the codes a torn/truncated extract DB raises on open/read.
    private static bool IsCorruption(SqliteException ex) => ex.SqliteErrorCode is 11 or 26;

    // Force-rebuild the DB out-of-process, drop pooled read connections still bound to the pre-rescan inode (so the
    // retry opens a fresh handle on the rebuilt artifact, not the old inode's stale snapshot), then reload ONCE.
    // A second failure on the retry load propagates — fail loudly rather than loop on a DB the tool cannot fix.
    private static IndexLoadResult<T> RebuildAndRetry<T>(Func<T> load, Func<long?> forceRescan, Action onBeforeRetry)
    {
        long? rebuiltRevision = forceRescan();
        onBeforeRetry();
        return new IndexLoadResult<T>(load(), Rebuilt: true, RebuiltRevision: rebuiltRevision);
    }

    internal static WorkspaceRegistryRow RegisterBootstrapWorkspace(
        WorkspaceContext workspace,
        string stableWorkspaceId,
        WorkspaceRegistryState state,
        long? revision)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableWorkspaceId);
        if (revision is < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Revision must be non-negative.");

        var (canonicalRoot, canonicalDbPath) = RequireCanonicalWorkspacePaths(workspace);
        using (var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath))
        {
            var row = registry.UpsertSeen(
                stableWorkspaceId,
                WorkspaceId.Display(canonicalRoot, stableWorkspaceId),
                canonicalRoot,
                canonicalDbPath,
                state);
            if (revision is null)
                return row;

            if (state == WorkspaceRegistryState.LoadedExisting)
                return registry.MarkLoadedExisting(stableWorkspaceId, revision.Value);

            return row;
        }
    }

    internal static WorkspaceRegistryRow MarkRegistryScanned(
        WorkspaceContext workspace, string stableWorkspaceId, long? revision)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableWorkspaceId);
        if (revision is < 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Revision must be non-negative.");

        var (canonicalRoot, canonicalDbPath) = RequireCanonicalWorkspacePaths(workspace);
        using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        var row = registry.UpsertSeen(
            stableWorkspaceId,
            WorkspaceId.Display(canonicalRoot, stableWorkspaceId),
            canonicalRoot,
            canonicalDbPath,
            WorkspaceRegistryState.Ready);
        return revision is { } rev ? registry.MarkScanned(stableWorkspaceId, rev) : row;
    }

    internal static WorkspaceRegistryRow MarkRegistryError(
        WorkspaceContext workspace, string stableWorkspaceId, string error)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableWorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        var (canonicalRoot, canonicalDbPath) = RequireCanonicalWorkspacePaths(workspace);
        using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        registry.UpsertSeen(
            stableWorkspaceId,
            WorkspaceId.Display(canonicalRoot, stableWorkspaceId),
            canonicalRoot,
            canonicalDbPath,
            WorkspaceRegistryState.Ready);
        return registry.MarkError(stableWorkspaceId, error);
    }

    private static (string CanonicalRoot, string CanonicalDbPath) RequireCanonicalWorkspacePaths(
        WorkspaceContext workspace)
    {
        string canonicalRoot = workspace.CanonicalRoot
            ?? throw new InvalidOperationException("Workspace canonical root is required before registry update.");
        string canonicalDbPath = workspace.CanonicalExtractDbPath
            ?? throw new InvalidOperationException("Workspace canonical extract DB path is required before registry update.");
        return (canonicalRoot, canonicalDbPath);
    }

    // The latest persisted revision for a reused DB, read from v1's extraction_revisions (MAX(revision_id); one
    // DB = one root, so no workspace filter — design §4.4). workspaceId here is ONLY the null-sentinel guard (a
    // never-scanned workspace has no revision → 0); it is NOT a SQL filter. A MISSING DB file is the only safe
    // degrade-to-0 case (the workspace genuinely has no revision yet → start fresh). A present-but-unreadable DB
    // (corruption, permission denied, the WAL-sidecar writable-dir violation, a lock) is an operator/config
    // error: surface it loudly (decision-10) rather than silently seeding revision 0. So only FileNotFoundException
    // degrades; InvalidOperationException (the D4 writable-dir guard) and SqliteException (corruption/lock) propagate.
    internal static long ReadLatestRevisionOrZero(string dbPath, string? workspaceId)
    {
        if (workspaceId is null)
            return 0;
        try
        {
            using var reader = new FreshnessReader(dbPath);
            return reader.LatestRevision();
        }
        catch (FileNotFoundException)
        {
            // The DB file does not exist → the workspace has no persisted revision yet; safe to start fresh.
            return 0;
        }
        // InvalidOperationException (D4 writable-dir guard) and SqliteException (corruption/lock/permission)
        // propagate loudly per decision-10 — a misconfigured DB must fail bootstrap, not degrade to revision 0.
    }

    // The artifact identity for the holder seed. Best-effort by design (null = unknown, the freshness poll
    // then falls back to revision-only comparisons): the index was ALREADY loaded successfully by this point,
    // so an unreadable metadata row must not fail bootstrap over a diagnostic-grade signal.
    private static string? ReadArtifactIdOrNull(string dbPath)
    {
        try
        {
            using var reader = new FreshnessReader(dbPath);
            return reader.ArtifactId();
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }

    /// <summary>
    /// Test seam: publish a workspace + holder directly, as if <see cref="StartAsync"/> had run, WITHOUT the
    /// SQLite reads / subprocess scan / ledger open. Lets a unit test exercise a collaborator that reads
    /// <see cref="Workspace"/> / <see cref="Holder"/> off the bootstrap (e.g. <see cref="FreshnessService.PollNow"/>)
    /// over a synthesized DB, with no live host. Same-root calls are idempotent; different roots replace the
    /// current test binding to model MCP roots/list_changed rebinds.
    /// Not used in production.
    /// </summary>
    internal void SeedForTest(WorkspaceContext workspace, IndexHolder holder)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(holder);
        lock (_gate)
        {
            var bound = _bound;
            if (bound?.Workspace.CanonicalRoot is not null &&
                workspace.CanonicalRoot is not null &&
                IndexBootstrapService.RootPathsEqual(bound.Workspace.CanonicalRoot, workspace.CanonicalRoot))
            {
                return;
            }
            _bound = new BoundWorkspace(holder, new SmartTargetResolver(holder), workspace, Ledger: null);
            _phase = BootstrapPhase.Bound;
            _snapshotRoot = workspace.CanonicalRoot;
            _startedAtUtc = null;
            _failureMessage = null;
            _lastFailureMessage = null;
            SignalBoundLocked();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => Volatile.Read(ref _bound)?.Ledger?.Dispose();
}
