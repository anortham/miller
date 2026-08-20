using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Miller.Server.Hosting;
using Miller.Server.Logging;
using Miller.Server.Tools;

namespace Miller.Server.Workspaces;

/// <summary>
/// A caller's tolerance for the machine-wide scan-admission wait on a FORCED refresh: how long it may queue, and
/// the token that abandons the queue early. The two travel together because they are ONE decision — a one-shot
/// CLI or dashboard refresh has no retry and may wait out the operator budget with no token, while an in-server
/// MCP call takes seconds and must give up when the host shuts down.
/// </summary>
public readonly record struct ScanAdmissionBudget(TimeSpan Wait, CancellationToken CancellationToken)
{
    /// <summary>A budget with no cancellation — the one-shot CLI and dashboard shape.</summary>
    public static ScanAdmissionBudget Of(TimeSpan wait) => new(wait, CancellationToken.None);
}

public sealed class CrossWorkspaceRefreshService
{
    private static readonly TimeSpan DefaultLockBusyWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultFullScanRequestWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultLockBusyPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly WorkspaceRegistry _registry;
    private readonly Func<string, string, bool, int?, ExtractIndexLevel, ExtractReport> _scan;
    private readonly Func<string, IDisposable?> _acquireLock;
    private readonly Func<string, long> _readLatestRevision;
    private readonly Func<string, string?> _readArtifactId;
    private readonly Func<string, string, string?, WorkspaceFreshnessProbe> _readStoreProbe;
    private readonly Action<string, string, long> _requestFullScan;
    private readonly TimeSpan _lockBusyWait;
    private readonly TimeSpan _fullScanRequestWait;
    private readonly TimeSpan _lockBusyPollInterval;
    private readonly Action<TimeSpan> _sleep;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SymbolSearchSidecar _sidecar;
    private readonly ContentCorpusSidecar _contentSidecar;
    private readonly Func<string, LeadershipVerdict>? _eligibilityGate;
    private readonly ScanGovernor _governor;
    private readonly TimeSpan _governorForceWait;
    private readonly Func<string, string, IScanFailurePolicy> _failurePolicyFor;
    private readonly Func<bool> _storeEnabled;
    private readonly IJulieStoreClient? _storeClient;
    private readonly Func<string, string, IJulieStoreClient, IDisposable?, StoreRollbackExportResult> _exportStoreRollback;
    private readonly Func<string, string, IDisposable, string?> _deleteStorePointerAfterSourceRebuild;
    private readonly IndexerSidecarConverger _sidecarConverger;
    private readonly IIndexerPhaseSink _phaseSink;

    // Appended to the eligibility verdict's reason when a one-shot refresh is refused (D2): the remedy is a
    // restore/upgrade, and the env hatch exists only for INTENTIONAL downgrades — never as a routine unblock.
    internal const string IneligibleRemedy =
        ". Refusing to rebuild the index with an outdated extractor: run scripts/restore-julie-extract.sh " +
        "(or scripts/restore-julie-extract.ps1) or upgrade miller; set MILLER_ALLOW_EXTRACTOR_DOWNGRADE=1 " +
        "only for an intentional downgrade.";

    public CrossWorkspaceRefreshService(
        WorkspaceRegistry registry,
        JulieExtractRunner runner,
        SymbolSearchSidecar sidecar,
        ScanGovernor governor,
        ContentCorpusSidecar? contentSidecar = null,
        Func<bool>? storeEnabled = null,
        ILogger<CrossWorkspaceRefreshService>? logger = null)
        : this(
            registry,
            (root, db, force, jobs, level) => runner.Scan(root, db, force, jobs, level),
            millerDir => SingleWriterLock.TryAcquire(millerDir),
            ReadLatestRevision,
            DefaultLockBusyWait,
            DefaultLockBusyPollInterval,
            Thread.Sleep,
            () => DateTimeOffset.UtcNow,
            sidecar,
            contentSidecar,
            LeaderScanRequestQueue.RequestFullScan,
            DefaultFullScanRequestWait,
            // The production D2 gate for every one-shot writer (CLI refresh/full/open, MCP cross-workspace
            // refresh, dashboard): probe the bundled binary, read the artifact's recorded binary_version, and
            // let the shared eligibility matrix decide. Evaluated only after the lock is acquired, so the
            // lock-busy enqueue-to-leader path is never affected.
            dbPath =>
            {
                bool allowDowngrade =
                    Environment.GetEnvironmentVariable("MILLER_ALLOW_EXTRACTOR_DOWNGRADE") == "1";
                string? ownVersion = runner.QueryVersion();
                try
                {
                    return LeadershipEligibility.Evaluate(
                        ownVersion,
                        WorkspaceReadSessionFactory.StoreEnabledFromEnvironment()
                            ? StoreArtifactVersionReader.ReadForLeadership(dbPath, ExtractBinaryVersionReader.TryRead)
                            : ExtractBinaryVersionReader.TryRead(dbPath),
                        allowDowngrade);
                }
                catch (StoreArtifactVersionReadException) when (allowDowngrade)
                {
                    return LeadershipEligibility.Evaluate(
                        ownVersion,
                        artifactBinaryVersion: null,
                        allowDowngrade: true);
                }
                catch (StoreArtifactVersionReadException ex)
                {
                    return new LeadershipVerdict(false, false, ex.Message);
                }
            },
            readArtifactId: ReadArtifactId,
            readStoreProbe: null,
            governor: governor,
            storeClient: new JulieStoreClient(runner.BinaryPath),
            storeEnabled: storeEnabled,
            phaseSink: new LoggingIndexerPhaseSink(logger ?? NullLogger<CrossWorkspaceRefreshService>.Instance))
    {
    }

    internal CrossWorkspaceRefreshService(
        WorkspaceRegistry registry,
        Func<string, string, bool, int?, ExtractIndexLevel, ExtractReport> scan,
        Func<string, IDisposable?> acquireLock,
        Func<string, long> readLatestRevision,
        TimeSpan lockBusyWait,
        TimeSpan lockBusyPollInterval,
        Action<TimeSpan> sleep,
        Func<DateTimeOffset> utcNow,
        SymbolSearchSidecar sidecar,
        ContentCorpusSidecar? contentSidecar = null,
        Action<string, string, long>? requestFullScan = null,
        TimeSpan? fullScanRequestWait = null,
        Func<string, LeadershipVerdict>? eligibilityGate = null,
        Func<string, string?>? readArtifactId = null,
        Func<string, string, string?, WorkspaceFreshnessProbe>? readStoreProbe = null,
        ScanGovernor? governor = null,
        TimeSpan? governorForceWait = null,
        Func<string, string, IScanFailurePolicy>? failurePolicyFor = null,
        IJulieStoreClient? storeClient = null,
        Func<bool>? storeEnabled = null,
        Action<string>? deleteStorePointer = null,
        Func<string, string, IJulieStoreClient, IDisposable?, StoreRollbackExportResult>? exportStoreRollback = null,
        IIndexerPhaseSink? phaseSink = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(acquireLock);
        ArgumentNullException.ThrowIfNull(readLatestRevision);
        ArgumentNullException.ThrowIfNull(sleep);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(sidecar);
        if (lockBusyWait < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockBusyWait), lockBusyWait, "Wait must be non-negative.");
        if (lockBusyPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(lockBusyPollInterval), lockBusyPollInterval, "Poll interval must be positive.");
        if (fullScanRequestWait is { } requestWait && requestWait < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(fullScanRequestWait), fullScanRequestWait, "Wait must be non-negative.");

        _registry = registry;
        _scan = scan;
        _acquireLock = acquireLock;
        _readLatestRevision = readLatestRevision;
        _readArtifactId = readArtifactId ?? ReadArtifactId;
        _readStoreProbe = readStoreProbe
            ?? ((dbPath, root, workspaceId) =>
                WorkspaceReadSessionFactory.Probe(dbPath, root, workspaceId, storeEnabled: true));
        _requestFullScan = requestFullScan ?? LeaderScanRequestQueue.RequestFullScan;
        _lockBusyWait = lockBusyWait;
        _fullScanRequestWait = fullScanRequestWait ?? DefaultFullScanRequestWait;
        _lockBusyPollInterval = lockBusyPollInterval;
        _sleep = sleep;
        _utcNow = utcNow;
        _sidecar = sidecar;
        _contentSidecar = contentSidecar ?? new ContentCorpusSidecar();
        _eligibilityGate = eligibilityGate;
        // Default = OFF, so no fast test ever opens a lease under the real user-global ~/.miller.
        _governor = governor ?? ScanGovernor.Disabled();
        _governorForceWait = governorForceWait ?? ScanGovernor.WaitFromEnvironment();
        _failurePolicyFor = failurePolicyFor
            ?? ((dbPath, canonicalRoot) => PersistedScanFailurePolicy.For(dbPath, canonicalRoot));
        _storeEnabled = storeEnabled ?? WorkspaceReadSessionFactory.StoreEnabledFromEnvironment;
        _storeClient = storeClient;
        _phaseSink = phaseSink ?? new LoggingIndexerPhaseSink(NullLogger<CrossWorkspaceRefreshService>.Instance);
        _exportStoreRollback = exportStoreRollback ?? StoreRollbackExporter.ExportIfRequired;
        if (deleteStorePointer is null)
        {
            _deleteStorePointerAfterSourceRebuild =
                static (root, databasePath, lease) =>
                    StoreRollbackExporter.DeletePointerAfterSourceRebuild(root, databasePath, lease);
        }
        else
        {
            _deleteStorePointerAfterSourceRebuild = (root, _, _) =>
            {
                deleteStorePointer(root);
                return null;
            };
        }
        _sidecarConverger = new IndexerSidecarConverger(
            _sidecar,
            _contentSidecar,
            NullLogger.Instance,
            phaseSink: _phaseSink);
    }

    /// <summary>
    /// Refresh one registered workspace. <paramref name="scanAdmission"/> is the FORCED path's machine-wide
    /// scan-admission budget and belongs to the CALLER, not this service: a one-shot CLI or dashboard refresh has
    /// no retry and may wait out <c>MILLER_SCAN_GOVERNOR_WAIT</c>, while an in-server MCP caller must pass a few
    /// seconds because a stuck call jams every agent sharing the connection. Null uses the configured default.
    /// A non-forced <c>ensure_fresh</c> read always uses the short lock-busy budget regardless.
    ///
    /// <para><paramref name="bypassBackoff"/> also belongs to the caller, and defaults to false because most
    /// traffic through here is NOT a person asking. Pass true only for a direct request — CLI
    /// <c>workspace refresh/full/open</c>, the MCP <c>workspace</c> tool, the dashboard. The automatic
    /// refresh-first path behind every cross-workspace read (<see cref="WorkspaceIndexProvider"/>, which
    /// <c>ReadToolWorkspaceRouting.ResolveEnsureFresh</c> turns on for ANY explicit <c>workspace_id</c>) must
    /// leave it false: ten cross-workspace searches against a workspace whose extractor is being OOM-killed would
    /// otherwise spawn ten more extractor processes. A deferred attempt serves the existing artifact with a
    /// <see cref="WorkspaceRefreshStatus.LockBusy"/>-shaped result rather than failing the read.</para>
    /// </summary>
    public WorkspaceRefreshResult Refresh(
        string workspaceId,
        bool force = false,
        ScanAdmissionBudget? scanAdmission = null,
        bool bypassBackoff = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (scanAdmission is { Wait: var requested } && requested < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(scanAdmission), requested, "Wait must be non-negative.");
        WorkspaceRegistryRow row = GetRequiredRow(workspaceId);
        var total = Stopwatch.StartNew();
        bool useStore = _storeEnabled();

        if (!Directory.Exists(row.CanonicalRoot))
        {
            string error = $"Workspace root not found: {row.CanonicalRoot}";
            _registry.MarkMissing(workspaceId, error, _utcNow());
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.MissingRoot,
                row.WorkspaceId,
                row.CanonicalRoot,
                row.IndexDbPath,
                row.LastRevision,
                Scanned: false,
                Error: error,
                TotalDuration: total.Elapsed,
                ArtifactId: TryReadArtifactId(row, useStore));
        }

        try
        {
            WorkspaceRootSafety.RejectSensitiveRoot(row.CanonicalRoot, fromCwd: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _registry.MarkError(workspaceId, ex.Message, _utcNow());
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Failed,
                row.WorkspaceId,
                row.CanonicalRoot,
                row.IndexDbPath,
                row.LastRevision,
                Scanned: false,
                Error: ex.Message,
                TotalDuration: total.Elapsed,
                ArtifactId: TryReadArtifactId(row, useStore));
        }

        string millerDir = Path.GetDirectoryName(row.IndexDbPath)
            ?? throw new InvalidOperationException(
                $"Cannot determine the .miller directory for index DB path '{row.IndexDbPath}'.");

        using IDisposable? lease = _acquireLock(millerDir);
        if (lease is null)
            return WaitForExternalRevision(row, force, millerDir, total, useStore);

        string? rollbackWarning = null;
        bool sourceRebuildRequired = false;
        if (!useStore && _storeClient is { } storeClient)
        {
            try
            {
                StoreRollbackExportResult rollback = _exportStoreRollback(
                    row.CanonicalRoot,
                    row.IndexDbPath,
                    storeClient,
                    lease);
                rollbackWarning = rollback.Warning;
                sourceRebuildRequired = rollback.RequiresSourceRebuild;
                if (rollback.RequiresPointerCleanup)
                {
                    string error = rollback.Warning
                        ?? "The legacy artifact was promoted, but the store pointer could not be removed.";
                    _registry.MarkError(row.WorkspaceId, error, _utcNow());
                    return new WorkspaceRefreshResult(
                        WorkspaceRefreshStatus.Failed,
                        row.WorkspaceId,
                        row.CanonicalRoot,
                        row.IndexDbPath,
                        row.LastRevision,
                        Scanned: false,
                        WarningText: error,
                        Error: error,
                        TotalDuration: total.Elapsed,
                        ArtifactId: TryReadArtifactId(row, useStore));
                }
            }
            catch (Exception ex) when (StoreRollbackExporter.IsOperationalFailure(ex))
            {
                string error = $"Store rollback export failed: {ex.Message}";
                _registry.MarkError(row.WorkspaceId, error, _utcNow());
                return new WorkspaceRefreshResult(
                    WorkspaceRefreshStatus.Failed,
                    row.WorkspaceId,
                    row.CanonicalRoot,
                    row.IndexDbPath,
                    row.LastRevision,
                    Scanned: false,
                    Error: error,
                    TotalDuration: total.Elapsed,
                    ArtifactId: TryReadArtifactId(row, useStore));
            }
        }
        bool unreadableStoreRecoveryAllowed =
            Environment.GetEnvironmentVariable("MILLER_ALLOW_EXTRACTOR_DOWNGRADE") == "1";
        bool storeRootRebindRequired = useStore && StoreArtifactVersionReader.RequiresRootRebind(
            row.IndexDbPath,
            unreadableStoreRecoveryAllowed);
        bool effectiveForce = force || sourceRebuildRequired || storeRootRebindRequired;

        // D2 gate, AFTER winning the lock (so a busy lock still enqueues to the live leader above, which
        // enforces its own gate): an outdated extractor must never rewrite an artifact built by a newer one.
        // A refusal is not a workspace error — the index stays valid, so the registry row is left untouched.
        if (_eligibilityGate is { } gate)
        {
            LeadershipVerdict verdict = gate(row.IndexDbPath);
            if (!verdict.Eligible)
            {
                return new WorkspaceRefreshResult(
                    WorkspaceRefreshStatus.IneligibleExtractor,
                    row.WorkspaceId,
                    row.CanonicalRoot,
                    row.IndexDbPath,
                    row.LastRevision,
                    Scanned: false,
                    Error: verdict.Reason + IneligibleRemedy,
                    TotalDuration: total.Elapsed,
                    ArtifactId: TryReadArtifactId(row, useStore));
            }
        }

        // Evaluated BEFORE machine-wide admission is taken: an attempt the backoff will not allow must not first
        // queue for (or hold) the one lease every other workspace's scan is waiting on.
        IScanFailurePolicy failurePolicy = _failurePolicyFor(row.IndexDbPath, row.CanonicalRoot);
        ScanIntent intent = storeRootRebindRequired
            ? ScanIntent.RootRebind
            : sourceRebuildRequired
            ? ScanIntent.CorruptionHeal
            : effectiveForce ? ScanIntent.UserFullRebuild : ScanIntent.IncrementalReconcile;
        ScanAttemptDecision attempt = failurePolicy.Evaluate(intent, bypassBackoff);
        if (!attempt.Attempt)
            return DeferredByScanBackoff(row, attempt, total, useStore);

        // Machine-wide scan admission, inside the workspace writer lock (SingleWriterLock -> ScanGovernor),
        // spanning the extract subprocess ONLY. The sidecar convergence below stays protected by the writer lock
        // this method holds, so it does not need the machine-wide lease — and holding the lease across ~200s of
        // per-workspace SQLite work serialized a worktree fleet on it (2026-08-06 P4 scale validation §3).
        using ScanGovernorAdmission? admission = TryAcquireScanAdmission(
            row.CanonicalRoot,
            effectiveForce ? scanAdmission?.Wait ?? _governorForceWait : _lockBusyWait,
            effectiveForce ? scanAdmission?.CancellationToken ?? CancellationToken.None : CancellationToken.None);
        if (admission is null)
            return RefusedScanAdmission(row, effectiveForce, millerDir, total, useStore);

        string? artifactIdBeforeScan = TryReadArtifactId(row, useStore);
        var scanClock = Stopwatch.StartNew();
        try
        {
            ExtractReport report;
            try
            {
                report = useStore
                    ? RunStoreScan(row, attempt)
                    : _scan(
                        row.CanonicalRoot,
                        row.IndexDbPath,
                        ScanIntentPolicy.RequiresForce(attempt.EffectiveIntent),
                        attempt.Jobs,
                        IndexLevels.LevelForScan(
                            attempt.EffectiveIntent, !File.Exists(row.IndexDbPath),
                            IndexLevels.Resolve(row.LevelPolicy)));
                scanClock.Stop();
            }
            finally
            {
                admission.Dispose();
            }

            if (attempt.Downgraded)
                failurePolicy.RecordDowngradedServe();
            else
                failurePolicy.RecordSuccess(attempt.EffectiveIntent);

            // No workspace_id echo to cross-check in v1: julie-extract self-rejects a DB built for a different
            // root (exit 3 RootMismatch, design §4.1), so a wrong-DB scan throws and is handled by the catch below.
            long revision = report.Revision ?? ReadLatestRevision(row, useStore);
            // A downgraded serve carries its own warning even though no caller can reach one today (every
            // force caller passes bypassBackoff). Without it, the result below is indistinguishable from the
            // rebuild that was asked for — the same lie the leader path's third outcome exists to prevent, and
            // the next caller that drops the bypass would ship it silently.
            string? warning = JoinNotes(
                attempt.Downgraded ? ScanFailurePolicy.DescribeDowngrade(intent, attempt) : null,
                JoinNotes(rollbackWarning, ExtractReportLog.DescribeWarning(report)));

            string? pointerCleanupError = null;
            if (sourceRebuildRequired)
            {
                try
                {
                    string? markerCleanupWarning = _deleteStorePointerAfterSourceRebuild(
                        row.CanonicalRoot,
                        row.IndexDbPath,
                        lease);
                    if (markerCleanupWarning is not null)
                    {
                        pointerCleanupError =
                            "Source reconciliation completed, but rollback cleanup was incomplete: " +
                            markerCleanupWarning;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    pointerCleanupError =
                        $"Source reconciliation completed, but rollback cleanup failed: {ex.Message}";
                }
            }

            warning = JoinNotes(warning, pointerCleanupError);

            if (pointerCleanupError is not null)
            {
                _registry.MarkError(row.WorkspaceId, pointerCleanupError, _utcNow());
                return new WorkspaceRefreshResult(
                    WorkspaceRefreshStatus.Failed,
                    row.WorkspaceId,
                    row.CanonicalRoot,
                    row.IndexDbPath,
                    revision,
                    Scanned: false,
                    WarningText: warning,
                    Error: pointerCleanupError,
                    ScanDuration: scanClock.Elapsed,
                    TotalDuration: total.Elapsed,
                    ArtifactId: report.Artifact?.ArtifactId
                        ?? TryReadArtifactId(row, useStore)
                        ?? artifactIdBeforeScan);
            }

            _registry.MarkScanned(row.WorkspaceId, revision, _utcNow());

            // This is the one safe writer for an external workspace's search.db — it holds the workspace
            // single-writer lock around the scan. Rebuild the sidecar from the scanned symbols.db here (off the
            // search hot path, skipped when already revision-fresh). A sidecar failure must never undo a
            // successful scan/refresh; reads report the sidecar unavailable/stale until the next successful
            // convergence.
            if (useStore)
                warning = JoinNotes(warning, TryConvergeStoreSidecars(row));
            else
                TryConvergeSidecar(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId, revision);

            // Refreshed-vs-unchanged comes from the REPORT, not a revision comparison: a force rebuild of an
            // incompatible artifact deletes and recreates the DB, restarting its revision counter (often on
            // the very number the registry already holds), so comparing revisions misreports a successful
            // from-scratch rebuild as "unchanged" (2026-06-11 Eros fleet finding).
            WorkspaceRefreshStatus status = report.IsNoChange
                ? WorkspaceRefreshStatus.Unchanged
                : WorkspaceRefreshStatus.Refreshed;
            string? artifactId = report.Artifact?.ArtifactId
                ?? TryReadArtifactId(row, useStore)
                ?? artifactIdBeforeScan;
            return new WorkspaceRefreshResult(
                status,
                row.WorkspaceId,
                row.CanonicalRoot,
                row.IndexDbPath,
                revision,
                Scanned: true,
                WarningText: warning,
                ScanDuration: scanClock.Elapsed,
                TotalDuration: total.Elapsed,
                ArtifactId: artifactId);
        }
        catch (Exception ex)
        {
            // Keep the duration of the FAILED scan attempt: a timeout kill reporting ~the timeout is exactly
            // the fact a fleet sweep needs to tell "slow under load" from "instant hard failure".
            scanClock.Stop();
            failurePolicy.RecordFailure(
                attempt.EffectiveIntent,
                JulieExtractException.ExitCodeOf(ex),
                attempt.Jobs ?? ExtractJobsPolicy.FromEnvironment());
            _registry.MarkError(row.WorkspaceId, ex.Message, _utcNow());
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Failed,
                row.WorkspaceId,
                row.CanonicalRoot,
                row.IndexDbPath,
                row.LastRevision,
                Scanned: false,
                Error: ex.Message,
                ScanDuration: scanClock.Elapsed,
                TotalDuration: total.Elapsed,
                ArtifactId: TryReadArtifactId(row, useStore));
        }
    }

    private static string? JoinNotes(string? first, string? second) => (first, second) switch
    {
        (null or "", null or "") => null,
        (null or "", { } only) => only,
        ({ } only, null or "") => only,
        var (a, b) => a + " " + b,
    };

    /// <summary>
    /// The refusal shape for an attempt the persisted scan-failure backoff will not allow yet. Shaped like a busy
    /// governor because it is the same promise: nothing scanned, the latest readable DB is served, retry later.
    /// A root with NO readable index cannot serve anything, so it reports
    /// <see cref="WorkspaceRefreshStatus.MissingIndex"/> (exit 3) rather than a <c>lock_busy</c> exit 0 that would
    /// advertise a workspace with no <c>symbols.db</c>.
    /// </summary>
    private WorkspaceRefreshResult DeferredByScanBackoff(
        WorkspaceRegistryRow row, ScanAttemptDecision attempt, Stopwatch total, bool useStore)
    {
        string reason = $"The previous whole-repo scan of this workspace failed {attempt.ConsecutiveFailures} " +
            $"time(s) in a row; the next automatic attempt is not before {attempt.RetryAtUtc:O}.";

        if (!HasReadableIndex(row, useStore))
        {
            string error = $"{reason} No index exists yet at {row.IndexDbPath}.";
            _registry.MarkMissing(row.WorkspaceId, error, _utcNow());
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.MissingIndex,
                row.WorkspaceId,
                row.CanonicalRoot,
                row.IndexDbPath,
                Revision: null,
                Scanned: false,
                Error: error,
                TotalDuration: total.Elapsed,
                ArtifactId: null);
        }

        return new WorkspaceRefreshResult(
            WorkspaceRefreshStatus.LockBusy,
            row.WorkspaceId,
            row.CanonicalRoot,
            row.IndexDbPath,
            row.LastRevision,
            Scanned: false,
            WarningText: $"{reason} Served the latest readable DB without scanning.",
            TotalDuration: total.Elapsed,
            ArtifactId: TryReadArtifactId(row, useStore));
    }

    /// <summary>
    /// The refusal shape for a busy machine-wide governor. Unlike a busy WRITER lock there is no live leader
    /// converging behind us, so the refusal must not imply one: a forced request is written to the target's
    /// leader queue so a Miller that starts there services it, and a root with NO readable index reports
    /// <see cref="WorkspaceRefreshStatus.MissingIndex"/> (registry row marked, exit 3) instead of a
    /// <c>lock_busy</c> exit 0 that would advertise a Ready workspace with no <c>symbols.db</c> and nothing
    /// scheduled. When a readable index DOES exist it is genuinely being served, so the row is left untouched.
    /// </summary>
    private WorkspaceRefreshResult RefusedScanAdmission(
        WorkspaceRegistryRow row, bool force, string millerDir, Stopwatch total, bool useStore)
    {
        string? requestWarning = null;
        if (force)
        {
            try
            {
                _requestFullScan(millerDir, row.WorkspaceId, row.LastRevision ?? 0);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                requestWarning = $"Miller could not queue a leader full-scan request: {ex.Message} ";
            }
        }

        if (!HasReadableIndex(row, useStore))
        {
            string error = "Machine-wide scan admission is busy and no index exists yet at " +
                $"{row.IndexDbPath}. " + _governor.DescribeHolder();
            _registry.MarkMissing(row.WorkspaceId, error, _utcNow());
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.MissingIndex,
                row.WorkspaceId,
                row.CanonicalRoot,
                row.IndexDbPath,
                Revision: null,
                Scanned: false,
                Error: error,
                TotalDuration: total.Elapsed,
                ArtifactId: null);
        }

        return new WorkspaceRefreshResult(
            WorkspaceRefreshStatus.LockBusy,
            row.WorkspaceId,
            row.CanonicalRoot,
            row.IndexDbPath,
            row.LastRevision,
            Scanned: false,
            WarningText: "Machine-wide scan admission is busy; served the latest readable DB without scanning. " +
                requestWarning + _governor.DescribeHolder(),
            TotalDuration: total.Elapsed,
            ArtifactId: TryReadArtifactId(row, useStore));
    }

    // Machine-wide admission for one governed refresh. A cancelled wait degrades to a refusal rather than
    // throwing: the caller is already going away, and every governed path must be able to serve the existing DB.
    private ScanGovernorAdmission? TryAcquireScanAdmission(
        string canonicalRoot, TimeSpan wait, CancellationToken cancellationToken)
    {
        try
        {
            return ScanGovernorAdmission.TryAcquire(
                _governor,
                ScanGovernorState.Shared,
                new ScanGovernorRequest(
                    canonicalRoot, "cross-workspace-refresh", ExtractJobsPolicy.FromEnvironment()),
                wait,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // Rebuild the external workspace's search.db sidecar best-effort after a scan. Swallows convergence failures by
    // design — the refresh's contract is the scanned symbols.db; the derived sidecar failure is surfaced on read as
    // unavailable/stale and retried on the next refresh.
    private void TryConvergeSidecar(string symbolsDbPath, string workspaceRoot, string? workspaceId, long revision)
    {
        try
        {
            _contentSidecar.EnsureBuilt(symbolsDbPath, workspaceRoot, workspaceId, revision);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException or IncompatibleExtractException)
        {
            // Best-effort: the content corpus is a rebuildable derived artifact; source-mode reads fail visibly.
        }

        try
        {
            _sidecar.EnsureBuilt(symbolsDbPath, revision, workspaceRoot);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException or IncompatibleExtractException)
        {
            // Best-effort: the sidecar is a rebuildable derived artifact; the next refresh retries.
        }

        // Metric-history cheap arm on the one-shot refresh path, mirroring the leader converge. Independent of the
        // sidecar builds above; RecordConverge never throws or blocks, so history failure remains best effort.
        MetricSnapshotAggregates.RecordConverge(
            symbolsDbPath, workspaceId, revision, MillerVersion.Current);
    }

    private ExtractReport RunStoreScan(WorkspaceRegistryRow row, ScanAttemptDecision attempt)
    {
        IJulieStoreClient client = _storeClient ?? throw new InvalidOperationException(
            "Store refresh is enabled but no julie-extract store client is configured.");
        StoreFamilyBinding binding = StoreWorkspaceCoordinator.ResolveBinding(
            _registry,
            row.WorkspaceId,
            row.CanonicalRoot);
        // This is the dominant recovery path: miller refresh, the MCP workspace tool, the dashboard, and every
        // cross-workspace read. It has no logger, so the recovery is recorded as a phase instead.
        if (binding.Replan != StoreViewReplan.None)
        {
            _phaseSink.RecordSafely(
                IndexerPhaseNames.StoreViewRecovery,
                TimeSpan.Zero,
                outcome: binding.Replan == StoreViewReplan.VanishedFromCatalog ? "vanished" : "never_published",
                storeSequence: null,
                didWork: false);
        }

        StoreWorkspaceCoordinator coordinator = StoreWorkspaceCoordinator.CreateWithPhaseSink(
            binding,
            row.WorkspaceId,
            client,
            () => IndexLevels.Resolve(row.LevelPolicy),
            File.Exists(row.IndexDbPath) ? row.IndexDbPath : null,
            _phaseSink);
        coordinator.SetSupportedExtensions(
            SupportedExtensionCatalog.ForToolsRoot(Path.Combine(AppContext.BaseDirectory, ".tools")));
        return coordinator.Scan(attempt.EffectiveIntent, attempt.Jobs);
    }

    private string? TryConvergeStoreSidecars(WorkspaceRegistryRow row)
    {
        try
        {
            using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
                row.IndexDbPath,
                row.CanonicalRoot,
                row.WorkspaceId,
                storeEnabled: true);
            string storeRoot = session.FamilyStoreRoot ?? throw new InvalidOperationException(
                "The store read session did not expose its family root.");
            _sidecarConverger.ConvergeStore(storeRoot, session);
            return null;
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException or TimeoutException)
        {
            return $"Store sidecar convergence is incomplete: {ex.Message}";
        }
    }

    private WorkspaceRefreshResult WaitForExternalRevision(
        WorkspaceRegistryRow row, bool force, string millerDir, Stopwatch total, bool useStore)
    {
        long baseline = row.LastRevision ?? 0;
        // The artifact identity BEFORE the leader acts: a full rebuild PROMOTES a fresh file whose revision
        // counter restarts, so `latest > baseline` alone can never confirm it — a CHANGED artifact_id does
        // (2026-06-11 Eros field report #2). Null (an unreadable/legacy artifact) degrades to revision-only.
        string? baselineArtifactId = TryReadFreshnessIdentity(row, useStore);
        long? lastReadableRevision = row.LastRevision;
        string? requestWarning = null;

        if (force)
        {
            try
            {
                _requestFullScan(millerDir, row.WorkspaceId, baseline);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                requestWarning = "Target workspace indexer lock is busy, and Miller could not write a leader " +
                    $"full-scan request: {ex.Message}";
            }
        }

        TimeSpan wait = force ? _fullScanRequestWait : _lockBusyWait;
        DateTimeOffset deadline = _utcNow() + wait;

        bool unconfirmedForceAdvance = false;
        while (_utcNow() < deadline)
        {
            if (TryReadLatestRevision(row, useStore, out long latest))
            {
                lastReadableRevision = latest;
                bool artifactReplaced = baselineArtifactId is not null
                    && TryReadFreshnessIdentity(row, useStore) is { } currentArtifactId
                    && !string.Equals(currentArtifactId, baselineArtifactId, StringComparison.Ordinal);
                // A force wait with a readable baseline id accepts ONLY a replaced artifact: the leader may
                // legally service the request as a downgraded delta (it evaluates without bypassBackoff), and
                // a delta bumps the revision without promoting — reporting that as a completed rebuild lies to
                // the person who asked for one. Null baseline id keeps the documented revision-only degradation.
                bool confirmed = artifactReplaced
                    || (latest > baseline && (!force || baselineArtifactId is null));
                if (force && !confirmed && latest > baseline)
                    unconfirmedForceAdvance = true;
                if (confirmed)
                {
                    _registry.MarkScanned(row.WorkspaceId, latest, _utcNow());
                    return new WorkspaceRefreshResult(
                        WorkspaceRefreshStatus.Refreshed,
                        row.WorkspaceId,
                        row.CanonicalRoot,
                        row.IndexDbPath,
                        latest,
                        Scanned: false,
                        TotalDuration: total.Elapsed,
                        ArtifactId: TryReadArtifactId(row, useStore));
                }
            }

            _sleep(_lockBusyPollInterval);
        }

        if (!HasReadableIndex(row, useStore))
        {
            string error = $"Workspace index DB not found: {row.IndexDbPath}";
            _registry.MarkMissing(row.WorkspaceId, error, _utcNow());
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.MissingIndex,
                row.WorkspaceId,
                row.CanonicalRoot,
                row.IndexDbPath,
                Revision: null,
                Scanned: false,
                Error: error,
                TotalDuration: total.Elapsed,
                ArtifactId: TryReadArtifactId(row, useStore));
        }

        string warning = (requestWarning ??
            (force
                ? unconfirmedForceAdvance
                    ? "Target workspace indexer lock is busy; the leader advanced the index while we waited but " +
                      "did not replace the artifact, so the requested full rebuild was likely served as a " +
                      "downgraded delta under scan-failure backoff and is still owed (see workspace status " +
                      "scan_failure). Serving the latest readable DB."
                    : "Target workspace indexer lock is busy; requested the leader to run a full scan, but freshness " +
                      "was not confirmed before serving the latest readable DB."
                : "Target workspace indexer lock is busy; freshness was not confirmed before serving the latest readable DB."))
            + " " + DescribeLockHolder(millerDir);
        return new WorkspaceRefreshResult(
            WorkspaceRefreshStatus.LockBusy,
            row.WorkspaceId,
            row.CanonicalRoot,
            row.IndexDbPath,
            lastReadableRevision,
            Scanned: false,
            WarningText: warning,
            TotalDuration: total.Elapsed,
            ArtifactId: TryReadArtifactId(row, useStore));
    }

    /// <summary>
    /// Who holds the busy lock, from the leader identity sidecar — so a lock_busy result names the holder
    /// instead of leaving an invisible-owner mystery. Identity is advisory (a crash leaves a stale file; a
    /// holder mid-startup has not written one yet), so each state is reported as exactly what it proves.
    /// </summary>
    private static string DescribeLockHolder(string millerDir)
    {
        Hosting.LeaderIdentity? identity;
        try
        {
            identity = Hosting.LeaderIdentityFile.TryRead(millerDir);
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

        return Hosting.LeaderIdentityFile.IsProcessAlive(identity)
            ? $"The recorded leader is miller pid {identity.Pid} (version {identity.Version}), and it is alive."
            : $"The recorded leader (miller pid {identity.Pid}, version {identity.Version}) is no longer " +
              "running — the actual holder has not recorded an identity (likely mid-startup or a " +
              "crash-looping instance).";
    }

    private bool TryReadLatestRevision(WorkspaceRegistryRow row, bool useStore, out long revision)
    {
        try
        {
            revision = useStore
                ? ReadStoreProbe(row).Revision
                : ReadLatestRevision(row, useStore: false);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException
                                       or SqliteException)
        {
            revision = 0;
            return false;
        }
    }

    // Best-effort identity probe for the rebuild-confirmation arm: null = unknown (a missing/locked/legacy
    // artifact), which degrades the wait to the historical revision-only comparison, never to a false confirm.
    private string? TryReadArtifactId(WorkspaceRegistryRow row, bool useStore)
    {
        if (useStore)
            return TryReadStoreProbe(row)?.StoreInstanceId;
        try
        {
            return _readArtifactId(row.IndexDbPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException
                                       or SqliteException)
        {
            return null;
        }
    }

    private string? TryReadFreshnessIdentity(WorkspaceRegistryRow row, bool useStore)
    {
        if (!useStore)
            return TryReadArtifactId(row, useStore: false);
        WorkspaceFreshnessProbe? probe = TryReadStoreProbe(row);
        return probe is null
            ? null
            : probe.StoreInstanceId + "|" + probe.ManifestHash;
    }

    private long ReadLatestRevision(WorkspaceRegistryRow row, bool useStore)
    {
        if (!useStore)
            return _readLatestRevision(row.IndexDbPath);
        return ReadStoreProbe(row).Revision;
    }

    private WorkspaceFreshnessProbe ReadStoreProbe(WorkspaceRegistryRow row) =>
        _readStoreProbe(row.IndexDbPath, row.CanonicalRoot, row.WorkspaceId);

    private WorkspaceFreshnessProbe? TryReadStoreProbe(WorkspaceRegistryRow row)
    {
        try
        {
            return ReadStoreProbe(row);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException or IOException
                or InvalidOperationException or SqliteException or UnauthorizedAccessException
                or ArgumentException)
        {
            return null;
        }
    }

    private bool HasReadableIndex(WorkspaceRegistryRow row, bool useStore) =>
        useStore ? TryReadStoreProbe(row) is not null : File.Exists(row.IndexDbPath);

    private WorkspaceRegistryRow GetRequiredRow(string workspaceId) =>
        _registry.Get(workspaceId) ?? throw new KeyNotFoundException(
            $"Workspace registry row '{workspaceId}' was not found.");

    private static long ReadLatestRevision(string dbPath)
    {
        using var reader = new FreshnessReader(dbPath);
        return reader.LatestRevision();
    }

    private static string? ReadArtifactId(string dbPath)
    {
        using var reader = new FreshnessReader(dbPath);
        return reader.ArtifactId();
    }
}
