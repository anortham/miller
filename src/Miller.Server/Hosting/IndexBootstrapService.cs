using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miller.Core.Freshness;
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

    private BoundWorkspace? _bound;
    private BootstrapPhase _phase = BootstrapPhase.Idle;
    private string? _snapshotRoot;
    private DateTimeOffset? _startedAtUtc;
    private string? _failureMessage;
    private string? _lastFailureMessage;
    private int _runGeneration;
    private TaskCompletionSource _runCompletion = CreateBindingGate();

    public IndexBootstrapService(ILogger<IndexBootstrapService> logger, ScanGovernor? scanGovernor = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        // Default = OFF, so no fast test ever opens a lease under the real user-global ~/.miller.
        _governor = scanGovernor ?? ScanGovernor.Disabled();
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
            var ctx = CreateWorkspaceContext(canonicalRoot);

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
            // v1 identity is the recorded canonical root_path, not a stored workspace_id (reconciliation #14).
            string? existingRootPath = null;

            BootstrapScanDecision ReadDecision()
            {
                var probe = ReadBootstrapScanDecision(canonicalDbPath, canonicalRoot);
                existingRootPath = probe.ExistingRootPath;
                return probe.Decision;
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
                    var scanLease = AcquireBootstrapScanLease(
                        tryAcquire: () => SingleWriterLock.TryAcquire(millerDir),
                        decide: ReadDecision,
                        winnerArtifactUsable: winnerArtifact.IsFinished,
                        wait: BootstrapScanLockWait(),
                        pollInterval: BootstrapScanLockPollInterval,
                        utcNow: () => DateTimeOffset.UtcNow,
                        sleep: Thread.Sleep);
                    bootstrapLease = scanLease.Lease;
                    scanDecision = scanLease.Decision;

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
                        throw new InvalidOperationException(
                            $"Timed out waiting for machine-wide scan admission to index {canonicalRoot}, and no " +
                            $"usable index exists at {canonicalDbPath}. {_governor.DescribeHolder()}");
                    }

                    TestScanObserver?.Invoke();
                    ScanAttemptDecision attempt =
                        failurePolicy.Evaluate(scanDecision.Intent, bypassBackoff: true);
                    ExtractReport report = RunRecordedScan(
                        failurePolicy, attempt,
                        () => runner.Scan(canonicalRoot, canonicalDbPath, scanDecision.Force, attempt.Jobs));
                    scanned = true;
                    scanRevision = report.Revision;
                    _logger.LogInformation(
                        "Scan complete: {Symbols} symbols extracted (revision {Rev}).",
                        report.SymbolsExtracted, report.Revision);
                    if (ExtractReportLog.DescribeWarning(report) is { } warning)
                        _logger.LogWarning("Bootstrap scan: {Warning}", warning);
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
                            () => runner.Scan(canonicalRoot, canonicalDbPath, force: true, attempt.Jobs));
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
                ShouldScan: true, ScanIntent.IncrementalReconcile, WorkspaceRegistryState.Ready);

        if (!RootPathsEqual(existingRootPath, canonicalRoot))
            return new BootstrapScanDecision(
                ShouldScan: true, ScanIntent.RootRebind, WorkspaceRegistryState.Ready);

        return new BootstrapScanDecision(
            ShouldScan: false, ScanIntent.IncrementalReconcile, WorkspaceRegistryState.LoadedExisting);
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
    /// instance promotes is exactly the transient this path must survive.
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
                existingRootPath, DecideBootstrapScan(dbExists, existingRootPath, canonicalRoot));
        }
        catch (FileNotFoundException)
        {
            return new BootstrapArtifactDecision(
                null, DecideBootstrapScan(dbExists: false, existingRootPath: null, canonicalRoot));
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            return new BootstrapArtifactDecision(
                null, DecideBootstrapScan(dbExists: true, existingRootPath: null, canonicalRoot));
        }
    }

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
            catch (Exception ex) when (IsProbeFailure(ex))
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
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return null;
            }
        }

        private static bool IsProbeFailure(Exception ex) =>
            ex is SqliteException or IOException or InvalidOperationException or UnauthorizedAccessException
                or IncompatibleExtractException;
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
