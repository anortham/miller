using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server.Hosting;
using Miller.Server.Logging;
using Miller.Server.Resolution;
using Miller.Server.Telemetry;
using Miller.Server.Tools;
using Miller.Server.Workspaces;

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
    int RunGeneration,
    bool IsBound);

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

    // The bootstrap scan runs on a detached Task.Run, so without this a scan blocked on machine-wide admission
    // would survive host shutdown holding an OS handle.
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ScanGovernor _governor;
    private readonly Func<bool> _storeEnabled;
    private readonly IIndexerPhaseSink _phaseSink;

    private BoundWorkspace? _bound;
    private BootstrapPhase _phase = BootstrapPhase.Idle;
    private string? _snapshotRoot;
    private DateTimeOffset? _startedAtUtc;
    private string? _failureMessage;
    private string? _lastFailureMessage;
    private int _runGeneration;
    private TaskCompletionSource _runCompletion = CreateBindingGate();

    public IndexBootstrapService(
        ILogger<IndexBootstrapService> logger,
        ScanGovernor? scanGovernor = null,
        Func<bool>? storeEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _phaseSink = new LoggingIndexerPhaseSink(logger);
        // Default = OFF, so no fast test ever opens a lease under the real user-global ~/.miller.
        _governor = scanGovernor ?? ScanGovernor.Disabled();
        _storeEnabled = storeEnabled ?? WorkspaceReadSessionFactory.StoreEnabledFromEnvironment;
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
        bool UsesExistingLedger,
        WorkspaceLineage? Lineage);

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
                    _runGeneration,
                    _bound is not null);
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
        ScanIntent Intent,
        WorkspaceRegistryState RegistryStateAfterLoad)
    {
        /// <summary>Whether the decided scan is a from-scratch rebuild rather than a hash-delta reconcile.</summary>
        internal bool Force => ScanIntentPolicy.RequiresForce(Intent);
    }

    /// <summary>
    /// The outcome of <see cref="LoadIndexWithAutoRebuild{T}"/>: the loaded index, whether a force-rebuild
    /// actually ran, and (when it ran) the revision it produced — which the caller folds into the holder's seed
    /// revision and the registry's scanned-at bookkeeping. <c>Rebuilt</c> is false when the rebuild was SKIPPED
    /// because another instance held the writer lock; reporting it true there recorded a scan nothing performed.
    /// </summary>
    internal sealed record IndexLoadResult<T>(T Index, bool Rebuilt, long? RebuiltRevision);

    /// <summary>How a bootstrap that needed to scan resolved the workspace writer lock.</summary>
    internal enum BootstrapLeaseOutcome
    {
        /// <summary>The lease is held; the accompanying decision was re-read after acquisition.</summary>
        Acquired,

        /// <summary>Another instance holds the lease, and the artifact it produced is finished and usable.</summary>
        WinnerArtifactUsable,

        /// <summary>The wait expired with the lease still held elsewhere and no finished artifact observed.</summary>
        TimedOut,
    }

    /// <summary>
    /// The result of <see cref="AcquireBootstrapScanLease{TLease}"/>. <c>Decision</c> is ALWAYS evaluated after the
    /// outcome is known, so a caller that acts on it is acting on post-lock facts rather than the stale pre-lock
    /// probe. <c>Lease</c> is non-null only for <see cref="BootstrapLeaseOutcome.Acquired"/>.
    /// </summary>
    internal sealed record BootstrapScanLease<TLease>(
        BootstrapLeaseOutcome Outcome,
        TLease? Lease,
        BootstrapScanDecision Decision) where TLease : class, IDisposable;

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
    /// Test-only hook: invoked immediately before every extract subprocess <see cref="RunBootstrap"/> launches,
    /// so a test can count the scans the real bootstrap performed. The scan itself still runs for real.
    /// </summary>
    internal Action? TestScanObserver { get; set; }

    internal Action? TestBeforeBootstrapScanLease { get; set; }

    /// <summary>
    /// Test-only home directory override for <see cref="WorkspaceContext.Create"/>. When set, every workspace
    /// context the bootstrap builds (including <see cref="MarkBootstrapFailed"/>) routes registry/telemetry
    /// paths under this directory instead of the real user profile.
    /// </summary>
    internal string? TestHomeDirectoryOverride { get; set; }

    /// <summary>
    /// Test-only override for the machine-wide scan-admission budget, so a contention test does not sit out the
    /// ten-minute production wait.
    /// </summary>
    internal TimeSpan? TestBootstrapScanAdmissionWait { get; set; }

    private WorkspaceContext CreateWorkspaceContext(string canonicalRoot) =>
        WorkspaceContext.Create(canonicalRoot, AppContext.BaseDirectory, TestHomeDirectoryOverride);

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

    /// <summary>
    /// Re-run the bootstrap for the root this service is ALREADY bound to, because a different checkout now
    /// occupies that path (<see cref="Miller.Indexing.WorkspaceRootIdentity"/>). <see cref="BootstrapForRoot"/>
    /// deliberately answers <see cref="BindOutcome.AlreadyBound"/> for an unchanged root, so path-reuse needs its
    /// own entry point; everything after this call is the ordinary bootstrap run, including
    /// <see cref="ReadBootstrapScanDecision"/> — the identity fact is folded INTO that decision by
    /// <see cref="EscalateForReplacedRoot"/> rather than deciding anything in parallel with it.
    ///
    /// <para>The caller must have released the workspace writer lease first: the run this starts needs it to
    /// rebuild the artifact.</para>
    /// </summary>
    /// <returns>
    /// The run generation to await with <see cref="WaitForRunAsync"/>. When a run is already in flight its
    /// generation is returned instead of starting a second one — that run rebinds the workspace on its own, and
    /// starting a competing one would race it for the same writer lease.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="canonicalRoot"/> is null or blank.</exception>
    internal int RebootstrapForReplacedRoot(string canonicalRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);

        int runGeneration;
        lock (_gate)
        {
            if (_phase == BootstrapPhase.Running)
                return _runGeneration;

            _logger.LogWarning(
                "Workspace root {Root} is now occupied by a different checkout; re-bootstrapping it.",
                canonicalRoot);
            runGeneration = StartRunLocked(canonicalRoot);
        }

        _ = Task.Run(() => RunBootstrapInBackground(
            canonicalRoot, WorkspaceBindingResolver.WorkspaceSource.Roots, runGeneration, rootReplaced: true));
        return runGeneration;
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
        string canonicalRoot, WorkspaceBindingResolver.WorkspaceSource source, int runGeneration,
        bool rootReplaced = false)
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

            result = RunBootstrap(canonicalRoot, source, rootReplaced);
            published = PublishBoundWorkspace(result, runGeneration);
            if (!published && !result.UsesExistingLedger)
                result.Bound.Ledger?.Dispose();
        }
        catch (OperationCanceledException ex) when (_shutdown.IsCancellationRequested)
        {
            if (result is not null && !published && !result.UsesExistingLedger)
                result.Bound.Ledger?.Dispose();
            MarkBootstrapAbandoned(canonicalRoot, runGeneration, ex);
        }
        catch (Exception ex)
        {
            if (result is not null && !published && !result.UsesExistingLedger)
                result.Bound.Ledger?.Dispose();
            MarkBootstrapFailed(canonicalRoot, source, runGeneration, ex);
        }
    }

    private BootstrapRunResult RunBootstrap(
        string canonicalRoot, WorkspaceBindingResolver.WorkspaceSource source, bool rootReplaced = false)
    {
        // The telemetry ledger is opened late but must be disposed if ANY later step throws (otherwise the
        // ledger stays open + the telemetry DB locked, but is never assigned to _ledger so Dispose() misses
        // it). Track it in a local and dispose on failure before the exception propagates.
        TelemetryLedger? ledger = null;
        bool usesExistingLedger = false;
        try
        {
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var ctx = CreateWorkspaceContext(canonicalRoot);

            string canonicalDbPath = Path.Combine(canonicalRoot, ".miller", "symbols.db");
            string stableWorkspaceId = WorkspaceId.FromCanonicalRoot(canonicalRoot);
            string millerDir = Path.GetDirectoryName(canonicalDbPath)!;
            Directory.CreateDirectory(millerDir);

            // One lineage sample per run, read BEFORE any registry write: the comparison below asks what the
            // stored row says about the PREVIOUS occupant, and the same run refreshes that row on the way out.
            var lineage = CaptureLineage(canonicalRoot);
            bool persistedRootReplaced = DisqualifiesRebind(
                ReadRegistryRow(ctx, stableWorkspaceId), IdentityOf(lineage));
            if (persistedRootReplaced)
            {
                _logger.LogWarning(
                    "Workspace root {Root} was occupied by a different checkout when it was last registered; " +
                    "rebuilding its index.", canonicalRoot);
            }

            if (_storeEnabled())
            {
                return RunStoreBootstrap(
                    ctx,
                    canonicalRoot,
                    canonicalDbPath,
                    stableWorkspaceId,
                    rootReplaced || persistedRootReplaced,
                    lineage,
                    startedAt);
            }

            StoreRollbackExportResult rollback = StoreRollbackExporter.ExportForBootstrap(
                canonicalRoot,
                canonicalDbPath,
                JulieStoreClient.Locate(ctx.ToolsRoot));
            bool rollbackRequiresSourceRebuild = rollback.RequiresSourceRebuild;
            if (rollback.Warning is { } rollbackWarning)
                _logger.LogWarning("{Warning}", rollbackWarning);
            if (rollback.RequiresPointerCleanup)
            {
                throw new StoreRollbackRetryException(
                    new IOException(rollback.Warning ?? "The promoted legacy artifact still has a store pointer."));
            }

            // Locate the pinned julie-extract under the tools root (NOT the repo cwd). Absent → fail loudly
            // (FileNotFoundException carrying the restore-script message) — Miller cannot index without it.
            var runner = JulieExtractRunner.Locate(
                ctx.ToolsRoot,
                reason => _logger.LogWarning(
                    "julie-extract is running WITHOUT Windows orphan containment: {Reason}. The scan proceeds, " +
                    "but if this Miller is killed the extractor can outlive it.", reason));

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
            // v1 identity is the recorded canonical root_path, not a stored workspace_id (reconciliation #14).
            string? existingRootPath = null;

            BootstrapScanDecision ReadDecision()
            {
                var probe = ReadBootstrapScanDecision(canonicalDbPath, canonicalRoot);
                existingRootPath = probe.ExistingRootPath;
                if (rootReplaced || persistedRootReplaced)
                    return EscalateForReplacedRoot(probe.Decision);
                return rollbackRequiresSourceRebuild
                    ? EscalateForStoreRollback(probe.Decision)
                    : probe.Decision;
            }

            var winnerArtifact = new WinnerArtifactProbe(canonicalDbPath, canonicalRoot, stableWorkspaceId);
            // The bootstrap RECORDS scan failures but is never DEFERRED by the backoff. It only scans when there
            // is no usable artifact for this root at all, so deferring could only turn a transient into a hard
            // bind failure with nothing served; concurrency here is already bounded by the workspace writer lease
            // (a loser loads the winner's artifact) and the machine-wide governor. What it must do is record —
            // that record is what throttles every AUTOMATIC path afterwards — and honor the post-SIGKILL jobs
            // clamp, which costs a struggling machine nothing.
            PersistedScanFailurePolicy failurePolicy =
                PersistedScanFailurePolicy.For(canonicalDbPath, canonicalRoot);

            var scanDecision = ReadDecision();
            bool scanned = false;
            IndexLoadResult<MillerRepositoryIndex> loadResult;

            // Every bootstrap scan — including the force rebind, which PROMOTES over the live artifact — runs
            // under this lease. It is disposed before the method returns: the same process's IndexerService claim
            // loop unblocks only after bind, so a leaked lease would make this instance a permanent non-leader.
            SingleWriterLock? bootstrapLease = null;
            try
            {
                if (scanDecision.ShouldScan)
                {
                    TestBeforeBootstrapScanLease?.Invoke();
                    var scanLease = AcquireBootstrapScanLease(
                        tryAcquire: () => SingleWriterLock.TryAcquire(millerDir),
                        decide: ReadDecision,
                        winnerArtifactUsable: () => MayStandDownForWinnerArtifact(
                            rootReplaced,
                            persistedRootReplaced,
                            rollbackRequiresSourceRebuild,
                            winnerArtifact.IsFinished),
                        wait: BootstrapScanLockWait(),
                        pollInterval: BootstrapScanLockPollInterval,
                        utcNow: () => DateTimeOffset.UtcNow,
                        sleep: Thread.Sleep);
                    bootstrapLease = scanLease.Lease;
                    scanDecision = scanLease.Decision;

                    if (rollbackRequiresSourceRebuild)
                    {
                        if (bootstrapLease is null)
                        {
                            throw new StoreRollbackRetryException(new IOException(
                                "Store rollback source reconciliation could not acquire the workspace writer lock."));
                        }

                        StoreRollbackExportResult currentRollback = StoreRollbackExporter.ExportForBootstrap(
                            canonicalRoot,
                            canonicalDbPath,
                            JulieStoreClient.Locate(ctx.ToolsRoot),
                            heldWriterLease: bootstrapLease);
                        if (currentRollback.Warning is { } currentRollbackWarning)
                            _logger.LogWarning("{Warning}", currentRollbackWarning);
                        if (currentRollback.RequiresPointerCleanup)
                        {
                            throw new StoreRollbackRetryException(
                                new IOException(currentRollback.Warning ??
                                    "The promoted legacy artifact still has a store pointer."));
                        }

                        rollbackRequiresSourceRebuild = currentRollback.RequiresSourceRebuild;
                        scanDecision = ReadDecision();
                    }

                    if (scanLease.Outcome == BootstrapLeaseOutcome.WinnerArtifactUsable)
                    {
                        _logger.LogInformation(
                            "Another Miller instance holds the writer lock for {Db}; loading the artifact it " +
                            "produced instead of scanning. {Holder}",
                            canonicalDbPath, DescribeBootstrapLockHolder(millerDir));
                    }
                    else if (scanLease.Outcome == BootstrapLeaseOutcome.TimedOut)
                    {
                        if (scanDecision.ShouldScan)
                        {
                            throw new InvalidOperationException(
                                $"Timed out waiting for the Miller writer lock on {millerDir}, and no usable index " +
                                $"exists at {canonicalDbPath}. {DescribeBootstrapLockHolder(millerDir)}");
                        }

                        _logger.LogWarning(
                            "Timed out waiting for the Miller writer lock for {Db}; serving the existing artifact " +
                            "without confirming its freshness. {Holder}",
                            canonicalDbPath, DescribeBootstrapLockHolder(millerDir));
                    }
                }

                if (scanDecision.ShouldScan && bootstrapLease is not null)
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
                    // SingleWriterLock -> ScanGovernor: admission is taken INSIDE the lease this bootstrap
                    // already holds, never the other way round.
                    using ScanGovernorAdmission? admission =
                        AcquireBootstrapScanAdmission(canonicalRoot, "bootstrap");
                    if (admission is null)
                    {
                        throw new ScanAdmissionTimeoutException(
                            $"Timed out waiting for machine-wide scan admission to index {canonicalRoot}, and no " +
                            $"usable index exists at {canonicalDbPath}. {_governor.DescribeHolder()}");
                    }

                    TestScanObserver?.Invoke();
                    ScanAttemptDecision attempt =
                        failurePolicy.Evaluate(scanDecision.Intent, bypassBackoff: true);
                    IndexLevelPolicy levelPolicy =
                        IndexLevels.ResolveForWorkspace(ctx.RegistryDbPath, stableWorkspaceId);

                    // A fresh linked worktree whose repository already has an indexed main checkout can be seeded
                    // from that artifact instead of re-extracting the whole tree. The attempt runs under the lease
                    // and admission this block already holds, and every non-promoted outcome falls through to the
                    // plain scan below with the target untouched.
                    RebindBootstrapOutcome rebind = TryRebindFromMainCheckout(
                        canonicalRoot, canonicalDbPath, ctx, runner, failurePolicy, attempt, levelPolicy,
                        rootReplaced || persistedRootReplaced || rollbackRequiresSourceRebuild);
                    if (rebind.Result == RebindBootstrapOutcome.Kind.Promoted)
                    {
                        scanned = true;
                        scanRevision = rebind.Revision;
                        _logger.LogInformation(
                            "Bootstrapped {Root} by rebinding the index of {SourceRoot} ({SourceDisplayId}) " +
                            "instead of a full extraction: {Reason} (revision {Rev}).",
                            canonicalRoot, rebind.SourceRoot, rebind.SourceDisplayId, rebind.Reason,
                            rebind.Revision);
                        if (rebind.Warning is { } rebindWarning)
                            _logger.LogWarning("Bootstrap scan: {Warning}", rebindWarning);
                    }
                    else
                    {
                        LogRebindFallback(_logger, canonicalRoot, rebind);

                        // Also at the fallback entry, because a SIGKILLed rebind is the one failure path that
                        // cannot clear its own staging, and a stranded trio is full-artifact sized.
                        RebindBootstrap.DiscardStaging(canonicalDbPath);

                        // A failed rebind journalled its own failure, so the pre-attempt decision is stale — most
                        // importantly it predates the post-SIGKILL --jobs clamp an OOM-killed delta just earned.
                        ScanAttemptDecision fallbackAttempt = RebindBootstrap.FallbackAttemptAfterRebind(
                            failurePolicy, scanDecision.Intent, attempt, rebind);

                        // A non-force bootstrap scan means no COMMITTED artifact exists — either no DB file, or a
                        // metadata-only shell from a crashed first scan (DecideBootstrapScan's !hasCommittedRevision
                        // arm). Both are first builds: julie records index_level only with extraction history, so a
                        // level-less shell accepts the policy's first-build level without conflict. The force case
                        // is a root rebind, which LevelForScan routes by intent.
                        ExtractIndexLevel bootstrapLevel = IndexLevels.LevelForScan(
                            fallbackAttempt.EffectiveIntent, newArtifact: !scanDecision.Force, levelPolicy);
                        ExtractReport report = RunRecordedScan(
                            failurePolicy, fallbackAttempt,
                            () => runner.Scan(
                                canonicalRoot, canonicalDbPath, scanDecision.Force, fallbackAttempt.Jobs,
                                bootstrapLevel));
                        scanned = true;
                        scanRevision = report.Revision;
                        _logger.LogInformation(
                            "Scan complete: {Symbols} symbols extracted (revision {Rev}).",
                            report.SymbolsExtracted, report.Revision);
                        if (ExtractReportLog.DescribeWarning(report) is { } warning)
                            _logger.LogWarning("Bootstrap scan: {Warning}", warning);
                    }
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
                loadResult = LoadIndexWithAutoRebuild(
                    load: () => RepositoryIndexLoader.Load(canonicalDbPath),
                    forceRescan: healIntent =>
                    {
                        // A force scan promotes over the live artifact (FullRebuildPromotion), and
                        // JulieExtractRunner.Scan's contract is that force-scan callers hold Miller's single-writer
                        // lock so two instances cannot interleave promotes on the same workspace. Skip the rebuild
                        // rather than promote unlocked: whoever holds the lock is already healing this artifact, and
                        // the retry load below either picks up their result or fails loudly.
                        //
                        // A lease this bootstrap ALREADY holds must be reused, never re-acquired: the lock is
                        // FileShare.None, so a second handle is denied to this process too and would spend the full
                        // wait discovering that, then skip a rebuild it was entitled to run.
                        using SingleWriterLock? writeLock = bootstrapLease is null
                            ? AcquireWriteLockForAutoRebuild(canonicalDbPath)
                            : null;
                        if (bootstrapLease is null && writeLock is null)
                        {
                            _logger.LogWarning(
                                "Auto-rebuild skipped: another Miller instance holds the write lock for {Db}. " +
                                "Retrying the load against whatever that instance produced.",
                                canonicalDbPath);
                            return null;
                        }

                        using ScanGovernorAdmission? admission =
                            AcquireBootstrapScanAdmission(canonicalRoot, "bootstrap-auto-rebuild");
                        if (admission is null)
                        {
                            _logger.LogWarning(
                                "Auto-rebuild skipped: machine-wide scan admission was refused for {Db}. " +
                                "Retrying the load against the existing artifact. {Holder}",
                                canonicalDbPath, _governor.DescribeHolder());
                            return null;
                        }

                        TestScanObserver?.Invoke();
                        ScanAttemptDecision attempt = failurePolicy.Evaluate(healIntent, bypassBackoff: true);
                        ExtractReport rebuild = RunRecordedScan(
                            failurePolicy, attempt,
                            () => runner.Scan(
                                canonicalRoot, canonicalDbPath, force: true, attempt.Jobs,
                                // Heals rebuild at the policy's repair level (symbols under progressive:
                                // restore serving fast, the upgrade re-latches from the artifact afterward).
                                IndexLevels.LevelForScan(
                                    attempt.EffectiveIntent, newArtifact: false,
                                    IndexLevels.ResolveForWorkspace(ctx.RegistryDbPath, stableWorkspaceId))));
                        _logger.LogInformation(
                            "Auto-rebuild scan complete: {Symbols} symbols extracted (revision {Rev}).",
                            rebuild.SymbolsExtracted, rebuild.Revision);
                        if (ExtractReportLog.DescribeWarning(rebuild) is { } warning)
                            _logger.LogWarning("Auto-rebuild scan: {Warning}", warning);
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

                if (rollbackRequiresSourceRebuild)
                {
                    if ((!scanned && !loadResult.Rebuilt) || bootstrapLease is null)
                    {
                        throw new StoreRollbackRetryException(
                            new IOException("Store rollback source reconciliation did not complete under the writer lock."));
                    }

                    try
                    {
                        string? cleanupWarning = StoreRollbackExporter.DeletePointerAfterSourceRebuild(
                            canonicalRoot,
                            canonicalDbPath,
                            bootstrapLease);
                        if (cleanupWarning is not null)
                            _logger.LogWarning("{Warning}", cleanupWarning);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new StoreRollbackRetryException(ex);
                    }
                }
            }
            finally
            {
                bootstrapLease?.Dispose();
            }

            var index = loadResult.Index;

            // An auto-rebuild counts as a scan for the holder's seed revision + the registry's scanned-at
            // bookkeeping below, even though DecideBootstrapScan chose to reuse: julie just (re)wrote the DB.
            bool didScan = scanned || loadResult.Rebuilt;
            if (loadResult.Rebuilt)
                scanRevision = loadResult.RebuiltRevision;

            using SingleWriterLock? rollbackCleanupLease = rollbackRequiresSourceRebuild
                ? SingleWriterLock.TryAcquire(millerDir)
                    ?? throw new StoreRollbackRetryException(new IOException(
                        "Cannot verify store rollback pointer cleanup because the workspace writer lock is held."))
                : null;

            if (rollbackRequiresSourceRebuild)
            {
                if (StoreWorkspacePointer.Exists(canonicalRoot))
                {
                    throw new StoreRollbackRetryException(new IOException(
                        "The store pointer became valid while legacy reconciliation was running; " +
                        "Miller will retry before binding the legacy artifact."));
                }
            }

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
                usesExistingLedger,
                lineage);
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

    private BootstrapRunResult RunStoreBootstrap(
        WorkspaceContext context,
        string canonicalRoot,
        string canonicalDbPath,
        string stableWorkspaceId,
        bool rootReplaced,
        WorkspaceLineage? lineage,
        long startedAt)
    {
        TelemetryLedger? ledger = null;
        bool usesExistingLedger = false;
        try
        {
            WorkspaceContext workspace = context with
            {
                WorkspaceId = stableWorkspaceId,
                CanonicalRoot = canonicalRoot,
                CanonicalExtractDbPath = canonicalDbPath,
            };
            RegisterBootstrapWorkspace(
                workspace,
                stableWorkspaceId,
                WorkspaceRegistryState.Refreshing,
                revision: null,
                lineage);

            string millerDir = Path.GetDirectoryName(canonicalDbPath)
                ?? throw new InvalidOperationException(
                    $"Cannot determine the .miller directory for index DB path '{canonicalDbPath}'.");
            StoreFamilyBinding? binding = null;
            bool lockAcquired = false;
            bool winnerArtifactUsable = false;
            // A cold start that lands mid-import blocks here with a 10-minute budget and, before this,
            // emitted NOTHING — the 35 s bootstrap on the Miller workspace itself was invisible in the log
            // (2026-08-12 triage). Counting through the sleep delegate keeps the pure helper untouched.
            var leaseWaitClock = System.Diagnostics.Stopwatch.StartNew();
            int leaseWaitPolls = 0;
            BootstrapScanLease<SingleWriterLock> storeLease = AcquireBootstrapScanLease(
                tryAcquire: () =>
                {
                    SingleWriterLock? lease = SingleWriterLock.TryAcquire(millerDir);
                    lockAcquired = lease is not null;
                    return lease;
                },
                decide: () =>
                {
                    if (!lockAcquired)
                    {
                        return new BootstrapScanDecision(
                            ShouldScan: !winnerArtifactUsable,
                            ScanIntent.IncrementalReconcile,
                            winnerArtifactUsable
                                ? WorkspaceRegistryState.LoadedExisting
                                : WorkspaceRegistryState.Ready);
                    }

                    binding = StoreWorkspaceCoordinator.ResolveBinding(
                        workspace,
                        canonicalRoot,
                        rootReplaced);
                    return new BootstrapScanDecision(
                        binding.State == StoreBindingState.Planned || rootReplaced,
                        rootReplaced ? ScanIntent.RootRebind : ScanIntent.IncrementalReconcile,
                        binding.State == StoreBindingState.Planned || rootReplaced
                            ? WorkspaceRegistryState.Ready
                            : WorkspaceRegistryState.LoadedExisting);
                },
                winnerArtifactUsable: () =>
                {
                    if (rootReplaced)
                        return false;
                    winnerArtifactUsable = TryReadReadyStoreBinding(
                        canonicalRoot,
                        stableWorkspaceId,
                        out binding);
                    return winnerArtifactUsable;
                },
                wait: BootstrapScanLockWait(),
                pollInterval: BootstrapScanLockPollInterval,
                utcNow: () => DateTimeOffset.UtcNow,
                sleep: delay =>
                {
                    // Name what is being waited for on the FIRST failed acquire. Deliberately not
                    // DescribeBootstrapLockHolder: that reads leader.json, which does not exist when the
                    // blocker is a store importer — which is the case that actually stalls a cold start.
                    if (leaseWaitPolls++ == 0)
                    {
                        _logger.LogInformation(
                            "Bootstrap is waiting for a readable family-store view for {Root}; the writer lock on " +
                            "{MillerDir} is held. Polling every {PollMs} ms for up to {WaitSeconds}s.",
                            canonicalRoot,
                            millerDir,
                            (int)BootstrapScanLockPollInterval.TotalMilliseconds,
                            (int)BootstrapScanLockWait().TotalSeconds);
                    }

                    Thread.Sleep(delay);
                });
            if (leaseWaitPolls > 0)
            {
                _logger.LogInformation(
                    "Bootstrap writer-lock wait ended as {Outcome} after {ElapsedMs} ms and {Polls} poll(s).",
                    storeLease.Outcome,
                    leaseWaitClock.ElapsedMilliseconds,
                    leaseWaitPolls);
            }

            using SingleWriterLock? bootstrapLease = storeLease.Lease;
            if (storeLease.Outcome == BootstrapLeaseOutcome.TimedOut)
            {
                throw new InvalidOperationException(
                    $"Timed out waiting for the Miller writer lock on {millerDir}, and no readable family-store " +
                    $"view exists for {canonicalRoot}. {DescribeBootstrapLockHolder(millerDir)}");
            }

            if (binding is null)
            {
                throw new InvalidOperationException(
                    "The family-store bootstrap did not resolve a binding after the writer-lock decision.");
            }
            long? scanRevision = null;
            bool didScan = false;
            if (binding.State == StoreBindingState.Planned || rootReplaced)
            {
                if (binding.Replan == StoreViewReplan.VanishedFromCatalog)
                {
                    _logger.LogError(
                        "The family store {StoreRoot} no longer carries view {ViewId} for {Root}, but this " +
                        "workspace recorded a completed scan before. Rebuilding the view by full import.",
                        binding.StoreRoot, binding.ViewId, canonicalRoot);
                }
                else if (binding.Replan == StoreViewReplan.NeverPublished)
                {
                    _logger.LogWarning(
                        "The family store {StoreRoot} has no view {ViewId} for {Root} and this workspace never " +
                        "completed a scan. Importing the view for the first time.",
                        binding.StoreRoot, binding.ViewId, canonicalRoot);
                }

                using ScanGovernorAdmission? admission =
                    AcquireBootstrapScanAdmission(canonicalRoot, "store-bootstrap");
                if (admission is null)
                {
                    throw new ScanAdmissionTimeoutException(
                        $"Timed out waiting for machine-wide scan admission to initialize family store " +
                        $"'{binding.StoreRoot}'. {_governor.DescribeHolder()}");
                }

                // A store import writes into a SHARED family. store_meta.binary_version is family-wide, so an
                // older bundled extractor importing here would take the family backwards for every member view.
                // Gate the write the same way IndexerService gates the lease. Skip the gate when the family
                // carries no version yet: that is a genuine first import and nothing can go backwards from it.
                //
                // Read the floor AFTER admission, never before. Admission can wait minutes, and a sibling
                // worktree publishing a newer generation inside that wait would leave this process importing on
                // a stale approval. This narrows the window to the import itself; it cannot close it, because
                // the invariant is family-wide while every lock Miller holds here is workspace-wide. Closing it
                // fully needs the comparison inside julie-extract under the family writer lease.
                if (!StoreArtifactVersionReader.TryReadFamilyWriterFloor(
                        binding, out string? familyVersion, out FamilyStoreReadException? unreadableFamily))
                {
                    throw new InvalidOperationException(
                        $"The family store '{binding.StoreRoot}' is unreadable, so Miller cannot prove that " +
                        $"importing view '{binding.ViewId}' is safe. {unreadableFamily!.Message}",
                        unreadableFamily);
                }

                if (familyVersion is not null)
                {
                    LeadershipVerdict writerVerdict = LeadershipEligibility.Evaluate(
                        ProbeBundledExtractorVersion(workspace.ToolsRoot),
                        familyVersion,
                        Environment.GetEnvironmentVariable("MILLER_ALLOW_EXTRACTOR_DOWNGRADE") == "1");
                    if (!writerVerdict.Eligible)
                    {
                        throw new InvalidOperationException(
                            $"Refusing to import view '{binding.ViewId}' into family store " +
                            $"'{binding.StoreRoot}': {writerVerdict.Reason}. Upgrade Miller, or set " +
                            $"MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1 to override.");
                    }
                }

                PersistedScanFailurePolicy failurePolicy =
                    PersistedScanFailurePolicy.For(canonicalDbPath, canonicalRoot);
                ScanAttemptDecision attempt = failurePolicy.Evaluate(
                    rootReplaced ? ScanIntent.RootRebind : ScanIntent.IncrementalReconcile,
                    bypassBackoff: true);
                StoreWorkspaceCoordinator coordinator = StoreWorkspaceCoordinator.CreateWithPhaseSink(
                    workspace,
                    canonicalRoot,
                    () => IndexLevels.ResolveForWorkspace(workspace.RegistryDbPath, stableWorkspaceId),
                    rootReplaced,
                    _phaseSink);
                coordinator.SetSupportedExtensions(
                    SupportedExtensionCatalog.ForToolsRoot(workspace.ToolsRoot));
                TestScanObserver?.Invoke();
                ExtractReport report = RunRecordedScan(
                    failurePolicy,
                    attempt,
                    () => coordinator.Scan(attempt.EffectiveIntent, attempt.Jobs));
                scanRevision = report.Revision;
                didScan = !report.IsNoChange;
                binding = StoreWorkspaceCoordinator.ResolveBinding(workspace, canonicalRoot, rootReplaced);
                if (binding.State != StoreBindingState.Ready)
                    throw new InvalidOperationException("The family store import completed without a readable view binding.");
            }
            else
            {
                _logger.LogInformation(
                    "Reusing family store {StoreRoot} view {ViewId} for {Root}.",
                    binding.StoreRoot,
                    binding.ViewId,
                    canonicalRoot);
            }

            StoreFamilyBinding servingBinding = binding;
            using FamilyStoreReadSession session = FamilyStoreReadSession.Open(servingBinding, stableWorkspaceId);
            WorkspaceIndexFacts indexFacts = WorkspaceIndexFactsReader.ReadSession(session);
            string indexIdentity = session.Snapshot.IndexIdentity;
            long builtRevision = session.Snapshot.Freshness.StoreLogSequence ?? throw new InvalidOperationException(
                "The family-store bootstrap snapshot has no store_log sequence.");
            scanRevision ??= builtRevision;

            Directory.CreateDirectory(Path.GetDirectoryName(workspace.TelemetryDbPath)!);
            int pruned;
            TelemetryLedger? existingLedger = Volatile.Read(ref _bound)?.Ledger;
            if (existingLedger is null)
            {
                ledger = OpenAndPrune(
                    workspace.TelemetryDbPath,
                    stableWorkspaceId,
                    workspace.WorkspaceRoot,
                    retentionDays: 30,
                    out pruned);
            }
            else
            {
                ledger = existingLedger;
                usesExistingLedger = true;
                pruned = 0;
            }

            var holder = new IndexHolder(
                () => LoadStoreGeneration(servingBinding, stableWorkspaceId, indexIdentity),
                builtRevision,
                checked((int)indexFacts.DocumentCount),
                indexFacts.KnownExtensionsCount,
                indexIdentity);
            var resolver = new SmartTargetResolver(holder);
            TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            return new BootstrapRunResult(
                new BoundWorkspace(holder, resolver, workspace, ledger),
                stableWorkspaceId,
                WorkspaceRegistryState.Ready,
                didScan,
                scanRevision,
                builtRevision,
                pruned,
                (long)elapsed.TotalMilliseconds,
                checked((int)indexFacts.DocumentCount),
                usesExistingLedger,
                lineage);
        }
        catch
        {
            if (ledger is not null && !usesExistingLedger)
                ledger.Dispose();
            throw;
        }
    }

    // Mirrors FreshnessService.LoadPinnedStoreIndex: a generation promoted between bootstrap and
    // materialization re-resolves to the CURRENT identity instead of failing; only an identity that keeps
    // moving on every attempt still throws.
    private static MillerRepositoryIndex LoadStoreGeneration(
        StoreFamilyBinding binding,
        string stableWorkspaceId,
        string expectedIdentity)
    {
        string expected = expectedIdentity;
        for (int attempt = 0; attempt < StoreSidecarCatalog.ReadableOpenAttempts; attempt++)
        {
            using FamilyStoreReadSession session = FamilyStoreReadSession.Open(binding, stableWorkspaceId);
            if (string.Equals(session.Snapshot.IndexIdentity, expected, StringComparison.Ordinal))
                return RepositoryIndexLoader.LoadSession(session);
            expected = session.Snapshot.IndexIdentity;
        }
        throw new InvalidOperationException(
            $"The family-store generation changed during every one of {StoreSidecarCatalog.ReadableOpenAttempts} load attempts; retry after freshness converges.");
    }

    private static bool TryReadReadyStoreBinding(
        string canonicalRoot,
        string stableWorkspaceId,
        out StoreFamilyBinding? binding)
    {
        binding = null;
        try
        {
            StoreWorkspacePointerDocument? pointer = StoreWorkspacePointer.Read(canonicalRoot);
            if (pointer is null)
                return false;

            var candidate = new StoreFamilyBinding(
                pointer.FamilyId,
                pointer.StoreRoot,
                pointer.ViewId,
                pointer.WorkspaceRoot,
                StoreBindingState.Ready);
            using FamilyStoreReadSession session = FamilyStoreReadSession.Open(candidate, stableWorkspaceId);
            binding = candidate;
            return true;
        }
        catch (Exception ex) when (
            ex is FamilyStoreReadException or StorePointerFormatException or IOException
                or UnauthorizedAccessException or ArgumentException or SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// Report why a bootstrap fell back to a full scan instead of rebinding a sibling checkout's index. An
    /// <see cref="RebindBootstrapOutcome.Kind.Ineligible"/> outcome logs one Information line naming the reason:
    /// it used to log NOTHING, which left the difference between a rebind and a full extraction diagnosable only
    /// by reading the code (2026-08-06 P4 scale validation §6). Fresh-workspace bootstraps are rare, so one line
    /// per bootstrap costs nothing. A failure keeps its warning.
    /// </summary>
    internal static void LogRebindFallback(
        ILogger logger, string canonicalRoot, RebindBootstrapOutcome rebind)
    {
        if (rebind.Result == RebindBootstrapOutcome.Kind.Failed)
        {
            logger.LogWarning(
                "Rebinding the index of {SourceRoot} into {Root} failed at {Stage}: {Reason}. " +
                "Falling back to a full scan.",
                rebind.SourceRoot, canonicalRoot, rebind.Stage, rebind.Reason);
            return;
        }

        logger.LogInformation(
            "Worktree rebind not eligible for {Root} ({Reason}); scanning it in full.",
            canonicalRoot, rebind.Reason);
    }

    private bool PublishBoundWorkspace(BootstrapRunResult result, int runGeneration)
    {
        lock (_gate)
        {
            if (_phase != BootstrapPhase.Running || _runGeneration != runGeneration)
                return false;

            if (result.DidScan)
                MarkRegistryScanned(
                    result.Bound.Workspace, result.StableWorkspaceId,
                    result.ScanRevision ?? result.BuiltRevision, result.Lineage);
            else
                RegisterBootstrapWorkspace(
                    result.Bound.Workspace,
                    result.StableWorkspaceId,
                    result.RegistryStateAfterLoad,
                    result.BuiltRevision,
                    result.Lineage);

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

    /// <summary>
    /// End a bootstrap that was cut short by host shutdown. It ends the run and unblocks waiters like a failure,
    /// but says so honestly and writes NO registry error: a shutdown is not a workspace fault, and persisting one
    /// would leave a later run reporting a timeout that never happened.
    /// </summary>
    private void MarkBootstrapAbandoned(string canonicalRoot, int runGeneration, Exception cancellation)
    {
        lock (_gate)
        {
            if (_phase != BootstrapPhase.Running || _runGeneration != runGeneration)
                return;

            string message = "Bootstrap was abandoned because the Miller host is shutting down; " +
                "the next run indexes this workspace.";
            _phase = BootstrapPhase.Failed;
            _snapshotRoot = canonicalRoot;
            _failureMessage = message;
            _lastFailureMessage = message;
            _runCompletion.TrySetResult();
        }

        _logger.LogInformation(
            cancellation, "Bootstrap for {Root} abandoned during host shutdown.", canonicalRoot);
    }

    private void MarkBootstrapFailed(
        string canonicalRoot, WorkspaceBindingResolver.WorkspaceSource source, int runGeneration, Exception error)
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
            // A vanished root must stay vanished: CreateDirectory recreates every missing parent, and a
            // resurrected empty root reads as "root returned" to WorkspaceRootPresenceMonitor.
            if (Directory.Exists(canonicalRoot))
                Directory.CreateDirectory(Path.GetDirectoryName(canonicalDbPath)!);
            var workspace = CreateWorkspaceContext(canonicalRoot) with
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

        if (error is StoreRollbackRetryException)
            ScheduleRollbackRetry(canonicalRoot, source, runGeneration);
        else if (error is ScanAdmissionTimeoutException)
            ScheduleAdmissionRetry(canonicalRoot, source, runGeneration);
    }

    /// <summary>
    /// Re-run a bootstrap that ended in <see cref="ScanAdmissionTimeoutException"/>, once, after a jittered delay.
    /// Unbounded by design: each cycle is one BOUNDED admission wait that runs no scan and writes no scan-failure
    /// record, so the cost of retrying forever is a poll, while the cost of giving up is a server that serves
    /// nothing until a person restarts it (2026-08-06 P4 scale validation §3). Host shutdown cancels the pending
    /// delay; a generation or phase change means a newer run already owns the workspace, so the stale retry exits.
    ///
    /// <para>The retry RE-VALIDATES the captured root at fire time (<see cref="RetryRootStillBindable"/>), because
    /// the bind-time sensitive-root guard speaks only for the tree that existed when the wait started. A root that
    /// is missing, swapped for another directory, or now sensitive drops the retry; that path belongs to the normal
    /// bind and root-replacement flows.</para>
    /// </summary>
    private void ScheduleAdmissionRetry(
        string canonicalRoot, WorkspaceBindingResolver.WorkspaceSource source, int failedGeneration)
        => ScheduleBootstrapRetry(canonicalRoot, source, failedGeneration, "scan admission timeout");

    private void ScheduleRollbackRetry(
        string canonicalRoot, WorkspaceBindingResolver.WorkspaceSource source, int failedGeneration)
        => ScheduleBootstrapRetry(canonicalRoot, source, failedGeneration, "store rollback export failure");

    private void ScheduleBootstrapRetry(
        string canonicalRoot, WorkspaceBindingResolver.WorkspaceSource source, int failedGeneration, string reason)
    {
        CancellationToken shutdown;
        try
        {
            shutdown = _shutdown.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        TimeSpan delay = JitterAdmissionRetryDelay(
            TestAdmissionRetryDelay ?? DefaultAdmissionRetryDelay, Random.Shared.NextDouble());

        _logger.LogInformation(
            "Bootstrap for {Root} failed due to {Reason}; retrying in {DelaySeconds}s.",
            canonicalRoot, reason, Math.Round(delay.TotalSeconds, 1));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!RetryRootStillBindable(canonicalRoot))
                return;

            int runGeneration;
            lock (_gate)
            {
                if (shutdown.IsCancellationRequested ||
                    _phase != BootstrapPhase.Failed ||
                    _runGeneration != failedGeneration)
                {
                    return;
                }

                runGeneration = StartRunLocked(canonicalRoot);
            }

            RunBootstrapInBackground(canonicalRoot, source, runGeneration);
        });
    }

    /// <summary>
    /// True when <paramref name="canonicalRoot"/> is still the directory the bind validated, so a scheduled
    /// admission retry may start a run against it. The retry captured a path, not a directory: during the wait the
    /// path can be deleted, or replaced by a symlink that resolves somewhere else, and either case would make the
    /// retry recreate a deleted workspace's <c>.miller</c> or scan a new occupant under the old workspace identity
    /// with the bind-time sensitive-root guard already spent. Re-canonicalizing catches all three shapes: a missing
    /// root throws, a swapped root resolves to a different canonical path, and a now-sensitive root is refused.
    /// Filesystem work only — the caller must not hold <c>_gate</c>.
    /// </summary>
    private bool RetryRootStillBindable(string canonicalRoot)
    {
        string? rejection;
        try
        {
            string revalidated = WorkspaceRootSafety.CanonicalizeAndRejectSensitiveRoot(
                canonicalRoot, fromCwd: false);
            rejection = !Directory.Exists(revalidated)
                ? "the workspace root no longer exists"
                : !RootPathsEqual(revalidated, canonicalRoot)
                    ? $"the path now resolves to '{revalidated}'"
                    : null;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            rejection = ex.Message;
        }

        if (rejection is null)
            return true;

        _logger.LogInformation("Dropping the bootstrap retry for {Root}: {Reason}.", canonicalRoot, rejection);
        return false;
    }

    /// <summary>
    /// The retry delay: the base wait plus up to a quarter of it, so a machine full of workspaces that all lost
    /// the same admission race does not re-queue in lockstep. <paramref name="sample"/> is clamped to [0,1].
    /// </summary>
    internal static TimeSpan JitterAdmissionRetryDelay(TimeSpan baseDelay, double sample) =>
        baseDelay + (baseDelay * (AdmissionRetryJitterFraction * Math.Clamp(sample, 0d, 1d)));

    private const double AdmissionRetryJitterFraction = 0.25;

    /// <summary>
    /// How long a failed admission wait rests before the bootstrap re-runs itself. Long enough that a retry
    /// storm cannot itself become the contention, short enough that a workspace freed within the hour binds
    /// without operator action.
    /// </summary>
    internal static readonly TimeSpan DefaultAdmissionRetryDelay = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Test-only override for <see cref="DefaultAdmissionRetryDelay"/>, so a retry test does not sit out the
    /// production minute.
    /// </summary>
    internal TimeSpan? TestAdmissionRetryDelay { get; set; }

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
    /// converge on the current root; a matching root that carries a committed revision reuses the existing DB.
    /// </summary>
    /// <param name="hasCommittedRevision">
    /// Whether the artifact carries a revision this process can SEE — the same committed-data test
    /// <see cref="WinnerArtifactProbe.IsFinished"/> applies. julie-extract writes <c>artifact_metadata</c>
    /// (<c>root_path</c> included) in autocommit the moment it opens the writer, then streams every file/symbol row
    /// into one long transaction, so for the whole duration of a first scan there is a DB on disk that matches this
    /// root and holds zero committed rows. Reusing it binds an empty index and serves a silent wrong answer to
    /// every agent query until freshness happens to reconcile. Deciding to scan instead routes the caller into the
    /// lease block, where the winner-artifact probe makes a loser wait for the winner's finished artifact. It stays
    /// a DELTA reconcile: a finished artifact reached here is correct and cheap to reconcile, and forcing would
    /// turn every cold-start race into a full rebuild.
    /// </param>
    internal static BootstrapScanDecision DecideBootstrapScan(
        bool dbExists, string? existingRootPath, string canonicalRoot, bool hasCommittedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);

        if (!dbExists)
            return new BootstrapScanDecision(
                ShouldScan: true, ScanIntent.IncrementalReconcile, WorkspaceRegistryState.Ready);

        if (!RootPathsEqual(existingRootPath, canonicalRoot))
            return new BootstrapScanDecision(
                ShouldScan: true, ScanIntent.RootRebind, WorkspaceRegistryState.Ready);

        if (!hasCommittedRevision)
            return new BootstrapScanDecision(
                ShouldScan: true, ScanIntent.IncrementalReconcile, WorkspaceRegistryState.Ready);

        return new BootstrapScanDecision(
            ShouldScan: false, ScanIntent.IncrementalReconcile, WorkspaceRegistryState.LoadedExisting);
    }

    /// <summary>
    /// Fold "a DIFFERENT checkout now occupies this path" into whatever <see cref="DecideBootstrapScan"/> read off
    /// the artifact. Workspace identity is the canonical ROOT PATH, so an artifact the previous occupant left
    /// behind records a <c>root_path</c> that still matches and would be REUSED — serving the removed worktree's
    /// symbols under the new one's name, with a matching freshness cursor to make it look current.
    /// <see cref="ScanIntent.RootRebind"/> names exactly that condition ("the artifact describes another tree")
    /// and is never downgradable, so a workspace whose scans have been failing cannot quietly turn the rebuild
    /// into a delta against the foreign artifact.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="decision"/> is null.</exception>
    internal static BootstrapScanDecision EscalateForReplacedRoot(BootstrapScanDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new BootstrapScanDecision(
            ShouldScan: true,
            ScanIntentPolicy.Strongest(new[] { decision.Intent, ScanIntent.RootRebind }),
            WorkspaceRegistryState.Ready);
    }

    internal static BootstrapScanDecision EscalateForStoreRollback(BootstrapScanDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new BootstrapScanDecision(
            ShouldScan: true,
            ScanIntentPolicy.Strongest(new[] { decision.Intent, ScanIntent.CorruptionHeal }),
            WorkspaceRegistryState.Ready);
    }

    /// <summary>
    /// Whether the registry row was written by a DIFFERENT checkout generation than the one occupying the root
    /// now. Such a row and the artifact beside it both describe the previous occupant, so this open must rebuild
    /// (<see cref="EscalateForReplacedRoot"/>) and must not seed itself from a sibling worktree's index.
    ///
    /// <para>The stored identity is the persisted half of the fact
    /// <see cref="WorkspaceRootPresenceMonitor"/> samples in memory; persisting it is what makes
    /// <c>git worktree remove</c> followed by <c>git worktree add</c> visible when no Miller was running to watch
    /// it happen. Missing evidence never counts as a replacement — an unregistered workspace, a row written
    /// before the lineage columns existed, and an unreadable current layout all answer false, because the verdict
    /// costs a whole-repo rebuild. <see cref="WorkspaceRootIdentity.IsReplacement"/> enforces that rule for both
    /// operands; the null row is the only case this method adds.</para>
    /// </summary>
    internal static bool DisqualifiesRebind(WorkspaceRegistryRow? stored, WorkspaceRootIdentity current) =>
        stored is not null
        && WorkspaceRootIdentity.IsReplacement(
            new WorkspaceRootIdentity(stored.GitDir, stored.GitDirCreatedAtUtc), current);

    internal static bool MayStandDownForWinnerArtifact(
        bool rootReplaced,
        bool persistedRootReplaced,
        bool rollbackRequiresSourceRebuild,
        Func<bool> artifactIsFinished)
    {
        ArgumentNullException.ThrowIfNull(artifactIsFinished);
        return !rootReplaced && !persistedRootReplaced && !rollbackRequiresSourceRebuild && artifactIsFinished();
    }

    /// <summary>
    /// The repository lineage of <paramref name="canonicalRoot"/>: the key every worktree of one repository
    /// shares, plus the generation of the checkout occupying the root right now. Null when the root resolves no
    /// git layout, which <see cref="WorkspaceRegistry.UpsertSeen"/> reads as "leave the stored lineage alone".
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="canonicalRoot"/> is null or blank.</exception>
    internal static WorkspaceLineage? CaptureLineage(string canonicalRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);

        if (GitWorktreeLayout.Resolve(canonicalRoot) is not { } layout)
            return null;

        var identity = WorkspaceRootIdentity.Capture(canonicalRoot);
        return new WorkspaceLineage(
            layout.CommonDir, layout.IsLinkedWorktree, identity.GitDir, identity.GitDirCreatedAtUtc);
    }

    /// <summary>The checkout-generation half of a captured lineage, or unknown when no git layout resolved.</summary>
    internal static WorkspaceRootIdentity IdentityOf(WorkspaceLineage? lineage) =>
        lineage is null
            ? WorkspaceRootIdentity.Unknown
            : new WorkspaceRootIdentity(lineage.GitDir, lineage.GitDirCreatedAtUtc);

    private static WorkspaceRegistryRow? ReadRegistryRow(WorkspaceContext workspace, string stableWorkspaceId)
    {
        using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        return registry.Get(stableWorkspaceId);
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
    internal static bool RootPathsEqual(string? recordedRootPath, string canonicalRoot) =>
        ArtifactRootIdentity.Matches(recordedRootPath, canonicalRoot);

    internal static StringComparison RootPathComparison(bool isWindows, bool isMacOS) =>
        ArtifactRootIdentity.ComparisonFor(isWindows, isMacOS);

    /// <summary>
    /// Bounded-wait acquisition of the workspace single-writer lock for the bootstrap auto-rebuild promote.
    /// Returns <c>null</c> when another instance still holds it after the wait.
    /// </summary>
    private static SingleWriterLock? AcquireWriteLockForAutoRebuild(string canonicalDbPath)
    {
        string? millerDir = Path.GetDirectoryName(Path.GetFullPath(canonicalDbPath));
        if (string.IsNullOrEmpty(millerDir))
            return null;

        var deadline = DateTimeOffset.UtcNow + AutoRebuildLockWait;
        while (true)
        {
            if (SingleWriterLock.TryAcquire(millerDir) is { } acquired)
                return acquired;
            if (DateTimeOffset.UtcNow >= deadline)
                return null;
            Thread.Sleep(AutoRebuildLockPollInterval);
        }
    }

    private static readonly TimeSpan AutoRebuildLockWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutoRebuildLockPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Own-version probe for the store writer gate, mirroring <c>IndexerService.ProbeBundledExtractorVersion</c>.
    /// Null on ANY failure: the eligibility matrix then reads a null own version against a non-null family
    /// version as INELIGIBLE, which is correct — Miller cannot prove it is not older. A null family version
    /// never reaches the gate, so a transient probe failure cannot block a genuine first import.
    /// </summary>
    private string? ProbeBundledExtractorVersion(string toolsRoot)
    {
        try
        {
            return JulieExtractRunner.Locate(toolsRoot).QueryVersion();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException
            or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not probe the bundled julie-extract version before a store import.");
            return null;
        }
    }

    /// <summary>
    /// Machine-wide admission for a bootstrap scan, on the SAME budget as the workspace lock wait: both are one
    /// user-visible "the first index is coming" state, already rendered as the friendly not-ready text. Returns
    /// null on refusal; THROWS <see cref="OperationCanceledException"/> when the host is shutting down, because a
    /// shutdown is not a budget expiry and must not be recorded as a failed bootstrap blaming a timeout.
    /// </summary>
    /// <exception cref="OperationCanceledException">The host is shutting down.</exception>
    private ScanGovernorAdmission? AcquireBootstrapScanAdmission(string canonicalRoot, string reason)
    {
        try
        {
            return ScanGovernorAdmission.TryAcquire(
                _governor,
                ScanGovernorState.Shared,
                new ScanGovernorRequest(canonicalRoot, reason, ExtractJobsPolicy.FromEnvironment()),
                TestBootstrapScanAdmissionWait ?? BootstrapScanLockWait(),
                _shutdown.Token);
        }
        catch (ObjectDisposedException)
        {
            throw new OperationCanceledException(
                $"The bootstrap scan admission wait for '{canonicalRoot}' ({reason}) was abandoned because the " +
                "Miller host is shutting down.");
        }
    }

    /// <summary>
    /// How long a bootstrap waits for the workspace writer lock before giving up. Deliberately far longer than
    /// <see cref="AutoRebuildLockWait"/>: that is a give-up budget for an already-loaded workspace, while this
    /// covers the entire first-index experience, during which the bootstrap phase stays
    /// <see cref="BootstrapPhase.Running"/> and tool calls render the friendly not-ready text. Overridden by
    /// <c>MILLER_BOOTSTRAP_SCAN_LOCK_WAIT_SECONDS</c>.
    /// </summary>
    internal static readonly TimeSpan DefaultBootstrapScanLockWait = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan BootstrapScanLockPollInterval = TimeSpan.FromMilliseconds(500);

    private static TimeSpan BootstrapScanLockWait() =>
        ParseBootstrapScanLockWait(Environment.GetEnvironmentVariable("MILLER_BOOTSTRAP_SCAN_LOCK_WAIT_SECONDS"));

    /// <summary>
    /// Parse the bootstrap lock-wait override in seconds. An absent, unparsable, negative, non-finite, or
    /// out-of-range value falls back to <see cref="DefaultBootstrapScanLockWait"/>; this never throws.
    /// </summary>
    /// <remarks>
    /// <c>double.TryParse</c> with <see cref="NumberStyles.Float"/> accepts <c>Infinity</c>, <c>NaN</c>, and
    /// magnitudes past <see cref="TimeSpan.MaxValue"/>, all of which make <see cref="TimeSpan.FromSeconds(double)"/>
    /// throw — which would fail the whole bootstrap over a typo in an env var. Same guard shape as
    /// <c>FullRebuildPromotion.ReadTimeout</c>.
    /// </remarks>
    internal static TimeSpan ParseBootstrapScanLockWait(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultBootstrapScanLockWait;

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
            !double.IsNaN(seconds) &&
            !double.IsInfinity(seconds) &&
            seconds >= 0 &&
            seconds <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : DefaultBootstrapScanLockWait;
    }

    /// <summary>
    /// A bootstrap scan decision together with the <c>root_path</c> the artifact recorded, which the caller logs
    /// when it force-rebinds.
    /// </summary>
    internal sealed record BootstrapArtifactDecision(string? ExistingRootPath, BootstrapScanDecision Decision);

    /// <summary>
    /// Read the scan decision from the artifact on disk. An artifact that is absent, or unreadable because it is
    /// mid-write, reads as "still needs a scan" rather than failing the bootstrap: a torn read while another
    /// instance promotes is exactly the transient this path must survive. This owns ALL the I/O behind the pure
    /// <see cref="DecideBootstrapScan"/>, including the committed-revision probe.
    /// </summary>
    internal static BootstrapArtifactDecision ReadBootstrapScanDecision(string dbPath, string canonicalRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        try
        {
            bool dbExists = File.Exists(dbPath);
            string? existingRootPath = dbExists ? ExtractReader.ReadRootPath(dbPath) : null;
            return new BootstrapArtifactDecision(
                existingRootPath,
                DecideBootstrapScan(
                    dbExists, existingRootPath, canonicalRoot,
                    dbExists && HasCommittedRevision(dbPath, canonicalRoot)));
        }
        catch (FileNotFoundException)
        {
            return new BootstrapArtifactDecision(
                null,
                DecideBootstrapScan(
                    dbExists: false, existingRootPath: null, canonicalRoot, hasCommittedRevision: false));
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            return new BootstrapArtifactDecision(
                null,
                DecideBootstrapScan(
                    dbExists: true, existingRootPath: null, canonicalRoot, hasCommittedRevision: false));
        }
    }

    /// <summary>
    /// Whether the artifact at <paramref name="dbPath"/> carries a revision this process can see. Uncommitted rows
    /// are invisible to other connections, so a visible revision is exactly the proof that a scan's long insert
    /// transaction landed — the same test <see cref="WinnerArtifactProbe.IsFinished"/> applies, over the same
    /// <see cref="ReadLatestRevisionOrZero"/> seam.
    /// </summary>
    /// <remarks>
    /// <see cref="ReadLatestRevisionOrZero"/> propagates a locked/corrupt/misconfigured DB loudly, because seeding
    /// the HOLDER's revision from a degraded read would mask the problem. Here the same failure has a safe answer
    /// that costs one delta scan — "no committed revision yet, so scan" — and the caller's contract is that no
    /// artifact read may fail the bootstrap. So every probe failure reads as absent rather than propagating.
    /// </remarks>
    private static bool HasCommittedRevision(string dbPath, string canonicalRoot)
    {
        try
        {
            return ReadLatestRevisionOrZero(dbPath, WorkspaceId.FromCanonicalRoot(canonicalRoot)) > 0;
        }
        catch (Exception ex) when (IsArtifactProbeFailure(ex))
        {
            return false;
        }
    }

    private static bool IsArtifactProbeFailure(Exception ex) =>
        ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
            or IncompatibleExtractException;

    /// <summary>
    /// The stand-down gate for a bootstrap that lost the workspace writer lock: it answers whether the artifact
    /// the holder is writing is FINISHED, so this instance may load it instead of scanning. Sampling is stateful
    /// (an identity must repeat across polls), so this is an object rather than a static.
    /// </summary>
    internal sealed class WinnerArtifactProbe
    {
        private readonly string _dbPath;
        private readonly string _canonicalRoot;
        private readonly string _workspaceId;
        private string? _sampledArtifactId;

        internal WinnerArtifactProbe(string dbPath, string canonicalRoot, string workspaceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
            _dbPath = dbPath;
            _canonicalRoot = canonicalRoot;
            _workspaceId = workspaceId;
        }

        /// <summary>
        /// True only when the artifact records THIS root, its identity stopped moving between polls, it carries a
        /// COMMITTED revision, and this build can read its schema. Any probe failure reads as "not finished yet",
        /// never as a bootstrap failure.
        /// </summary>
        /// <remarks>
        /// The committed-revision conjunct is the load-bearing one. julie-extract writes <c>artifact_metadata</c>
        /// — including <c>artifact_id</c> and <c>root_path</c> — in autocommit the moment it opens the writer,
        /// then streams every file/symbol row into one long transaction, and a first scan writes IN PLACE with no
        /// promote to move the id. Identity alone therefore goes stable about a second into a scan that has
        /// committed nothing, and a loser accepting it would bind an empty index for the whole duration of the
        /// winner's insert. Uncommitted rows are invisible to other connections, so a visible revision is exactly
        /// the proof that the write landed.
        ///
        /// The schema conjunct keeps a winner THIS build cannot read from being accepted: standing down on it
        /// turns a self-heal into a hard bootstrap failure, because the auto-rebuild cannot take the lock the
        /// winner is still holding. Refusing it here leaves this process waiting to heal the artifact itself.
        /// </remarks>
        internal bool IsFinished()
        {
            string? previous = _sampledArtifactId;
            string? current = TryReadArtifactIdForThisRoot();
            _sampledArtifactId = current;

            if (current is null || !string.Equals(current, previous, StringComparison.Ordinal))
                return false;

            try
            {
                if (ReadLatestRevisionOrZero(_dbPath, _workspaceId) <= 0)
                    return false;

                SqliteSymbolReader.VerifyCompatible(_dbPath);
                return true;
            }
            catch (Exception ex) when (IsArtifactProbeFailure(ex))
            {
                return false;
            }
        }

        private string? TryReadArtifactIdForThisRoot()
        {
            try
            {
                return File.Exists(_dbPath) &&
                    RootPathsEqual(ExtractReader.ReadRootPath(_dbPath), _canonicalRoot)
                    ? SymbolsArtifactIdentity.TryRead(_dbPath).ArtifactId
                    : null;
            }
            catch (Exception ex) when (IsArtifactProbeFailure(ex))
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Acquire the workspace single-writer lock for a bootstrap scan, or stand down in favour of the artifact the
    /// current holder produced. Generic over the lease type (which is sealed in production) so the fast suite can
    /// drive the whole control flow with fakes, exactly as <see cref="LoadIndexWithAutoRebuild{T}"/> is driven.
    /// The returned <see cref="BootstrapScanLease{TLease}.Decision"/> is always evaluated after the outcome is
    /// known, so the caller never acts on the stale pre-lock probe.
    /// </summary>
    /// <remarks>
    /// A loser NEVER waits for the lock to be released: a live <c>IndexerService</c> leader in another process
    /// holds it for that process's entire lifetime, so a release-wait could not terminate. It exits the moment
    /// <paramref name="winnerArtifactUsable"/> reports a finished artifact, and the caller loads that instead.
    /// Handing the work to the holder through <c>LeaderScanRequestQueue</c> is a deliberate non-choice here: it
    /// moves the write to another process and needs its own bootstrap-time requester design.
    /// </remarks>
    internal static BootstrapScanLease<TLease> AcquireBootstrapScanLease<TLease>(
        Func<TLease?> tryAcquire,
        Func<BootstrapScanDecision> decide,
        Func<bool> winnerArtifactUsable,
        TimeSpan wait,
        TimeSpan pollInterval,
        Func<DateTimeOffset> utcNow,
        Action<TimeSpan> sleep) where TLease : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(tryAcquire);
        ArgumentNullException.ThrowIfNull(decide);
        ArgumentNullException.ThrowIfNull(winnerArtifactUsable);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(sleep);

        DateTimeOffset deadline = utcNow() + wait;
        while (true)
        {
            if (tryAcquire() is { } lease)
                return Resolve(BootstrapLeaseOutcome.Acquired, lease);

            if (winnerArtifactUsable())
                return Resolve(BootstrapLeaseOutcome.WinnerArtifactUsable, null);

            if (utcNow() >= deadline)
                return Resolve(BootstrapLeaseOutcome.TimedOut, null);

            sleep(pollInterval);
        }

        // A throwing decide() must not orphan a lease this method just won: the caller has no reference to
        // release, and the FileShare.None handle would then survive to finalization, making every later
        // bootstrap on this workspace wait out the full lock timeout and fail.
        BootstrapScanLease<TLease> Resolve(BootstrapLeaseOutcome outcome, TLease? lease)
        {
            try
            {
                return new BootstrapScanLease<TLease>(outcome, lease, decide());
            }
            catch
            {
                lease?.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Who holds the writer lock, from the leader identity sidecar, so a bootstrap that waited on it names the
    /// holder instead of leaving an invisible-owner mystery. Identity is advisory (a crash leaves a stale file; a
    /// holder mid-startup has not written one yet), so each state reports exactly what it proves.
    /// </summary>
    internal static string DescribeBootstrapLockHolder(string millerDir)
    {
        LeaderIdentity? identity;
        try
        {
            identity = LeaderIdentityFile.TryRead(millerDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            identity = null;
        }

        if (identity is null)
        {
            return "No leader identity is recorded for the holder — it is likely mid-startup, or exited " +
                "without recording one.";
        }

        return LeaderIdentityFile.IsProcessAlive(identity)
            ? $"The recorded leader is miller pid {identity.Pid} (version {identity.Version}), and it is alive."
            : $"The recorded leader (miller pid {identity.Pid}, version {identity.Version}) is no longer " +
              "running — the actual holder has not recorded an identity (likely mid-startup or a " +
              "crash-looping instance).";
    }

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
        Func<ScanIntent, long?> forceRescan,
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
            return RebuildAndRetry(load, forceRescan, ScanIntent.SchemaHeal, onBeforeRetry);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            // A torn/truncated/half-written DB — e.g. the optional writer/indexer was killed (Ctrl-C, OOM, power
            // loss) mid-scan, leaving symbols.db malformed. Rather than crash startup (surfacing as "MCP failed to
            // connect"), force-rebuild once with the bundled julie-extract and reload — the same self-heal the
            // incompatible path uses. A SECOND corruption after rebuild escapes (we never loop).
            onCorrupt(ex);
            return RebuildAndRetry(load, forceRescan, ScanIntent.CorruptionHeal, onBeforeRetry);
        }
    }

    // SQLITE_CORRUPT (11) and SQLITE_NOTADB (26): the codes a torn/truncated extract DB raises on open/read.
    private static bool IsCorruption(SqliteException ex) => ex.SqliteErrorCode is 11 or 26;

    // Force-rebuild the DB out-of-process, drop pooled read connections still bound to the pre-rescan inode (so the
    // retry opens a fresh handle on the rebuilt artifact, not the old inode's stale snapshot), then reload ONCE.
    // A second failure on the retry load propagates — fail loudly rather than loop on a DB the tool cannot fix.
    // A null revision means the rescan was SKIPPED (the lock was busy), so nothing was rebuilt; the barrier still
    // runs because the holder may have promoted a new inode under us.
    private static IndexLoadResult<T> RebuildAndRetry<T>(
        Func<T> load, Func<ScanIntent, long?> forceRescan, ScanIntent healIntent, Action onBeforeRetry)
    {
        long? rebuiltRevision = forceRescan(healIntent);
        onBeforeRetry();
        return new IndexLoadResult<T>(load(), Rebuilt: rebuiltRevision is not null, RebuiltRevision: rebuiltRevision);
    }

    /// <summary>
    /// Run a bootstrap scan through the persisted scan-failure record: a success clears the failure history when
    /// the completed intent satisfies the recorded one, a throw extends it (with julie's exit code, so a SIGKILL
    /// clamps the next attempt's <c>--jobs</c>) before propagating. This is what makes a bootstrap failure visible
    /// to every LATER automatic path — the bootstrap itself never defers on the record.
    /// </summary>
    private static ExtractReport RunRecordedScan(
        IScanFailurePolicy failurePolicy, ScanAttemptDecision attempt, Func<ExtractReport> scan)
    {
        try
        {
            ExtractReport report = scan();
            failurePolicy.RecordSuccess(attempt.EffectiveIntent);
            return report;
        }
        catch (Exception ex)
        {
            failurePolicy.RecordFailure(
                attempt.EffectiveIntent,
                JulieExtractException.ExitCodeOf(ex),
                attempt.Jobs ?? ExtractJobsPolicy.FromEnvironment());
            throw;
        }
    }

    /// <summary>
    /// Attempt to seed this workspace from its repository's main-checkout artifact instead of extracting the
    /// whole tree (rebind contract design §7). Called INSIDE the bootstrap writer lease and the one governor
    /// admission the scan already holds — a multi-GB snapshot copy is the same class of machine load a scan is,
    /// and the sequence never takes the SOURCE workspace's writer lock, so the lock order is untouched.
    ///
    /// <para>The seams without a default are the two that need this bootstrap's located runner plus the report
    /// describer, which lives in this layer rather than in <c>Miller.Indexing</c>. The delta
    /// scan is a NON-force scan pointed at the staging file, so it inherits the whole shared scan chokepoint —
    /// <c>--jobs</c>, the invariant ignore file, and supervision paths resolved from the staging file's own
    /// <c>.miller</c> directory — while a force scan would delete the seed it is meant to reconcile.</para>
    /// </summary>
    private RebindBootstrapOutcome TryRebindFromMainCheckout(
        string canonicalRoot,
        string canonicalDbPath,
        WorkspaceContext ctx,
        JulieExtractRunner runner,
        IScanFailurePolicy failurePolicy,
        ScanAttemptDecision attempt,
        IndexLevelPolicy levelPolicy,
        bool rootReplacementDetected)
    {
        int jobs = attempt.Jobs ?? ExtractJobsPolicy.FromEnvironment();
        return RebindBootstrap.TryRebind(
            new RebindBootstrapRequest
            {
                TargetRoot = canonicalRoot,
                TargetDbPath = canonicalDbPath,
                RegistryDbPath = ctx.RegistryDbPath,
                RootReplacementDetected = rootReplacementDetected,
                TargetLevelPolicy = levelPolicy,
                FailurePolicy = failurePolicy,
                Jobs = jobs,
            },
            new RebindBootstrapSeams
            {
                Rebind = (snapshotDb, targetRoot, ct) => runner.Rebind(snapshotDb, targetRoot, ct),
                RunDeltaScan = (snapshotDb, level) =>
                    runner.Scan(canonicalRoot, snapshotDb, force: false, jobs, level),
                DescribeScanWarning = ExtractReportLog.DescribeWarning,
            },
            _shutdown.Token);
    }

    internal static WorkspaceRegistryRow RegisterBootstrapWorkspace(
        WorkspaceContext workspace,
        string stableWorkspaceId,
        WorkspaceRegistryState state,
        long? revision,
        WorkspaceLineage? lineage = null)
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
                state,
                lineage: lineage);
            if (revision is null)
                return row;

            if (state == WorkspaceRegistryState.LoadedExisting)
                return registry.MarkLoadedExisting(stableWorkspaceId, revision.Value);

            return row;
        }
    }

    /// <summary>
    /// The lineage-free shape, kept as its own overload because callers bind it as a
    /// <see cref="Func{T1, T2, T3, TResult}"/> method group, which an optional parameter cannot satisfy.
    /// </summary>
    internal static WorkspaceRegistryRow MarkRegistryScanned(
        WorkspaceContext workspace, string stableWorkspaceId, long? revision) =>
        MarkRegistryScanned(workspace, stableWorkspaceId, revision, lineage: null);

    internal static WorkspaceRegistryRow MarkRegistryScanned(
        WorkspaceContext workspace, string stableWorkspaceId, long? revision, WorkspaceLineage? lineage)
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
            WorkspaceRegistryState.Ready,
            lineage: lineage);
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

    /// <summary>
    /// Record that the workspace's root is no longer on disk. The registry lives outside the workspace
    /// (<c>~/.miller/workspaces.db</c>), so it survives the deletion and is the one place a fleet-wide view can
    /// tell "this worktree is gone" from "this worktree is idle".
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stableWorkspaceId"/> is null or blank.</exception>
    internal static WorkspaceRegistryRow MarkRegistryMissing(
        WorkspaceContext workspace, string stableWorkspaceId, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableWorkspaceId);

        var (canonicalRoot, canonicalDbPath) = RequireCanonicalWorkspacePaths(workspace);
        using var registry = WorkspaceRegistry.Open(workspace.RegistryDbPath);
        registry.UpsertSeen(
            stableWorkspaceId,
            WorkspaceId.Display(canonicalRoot, stableWorkspaceId),
            canonicalRoot,
            canonicalDbPath,
            WorkspaceRegistryState.Ready);
        return registry.MarkMissing(stableWorkspaceId, reason);
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TryCancelShutdown();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        TryCancelShutdown();
        _shutdown.Dispose();
        Volatile.Read(ref _bound)?.Ledger?.Dispose();
    }

    private void TryCancelShutdown()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
