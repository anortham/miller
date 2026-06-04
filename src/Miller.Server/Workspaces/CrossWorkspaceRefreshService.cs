using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Logging;
using Miller.Server.Tools;

namespace Miller.Server.Workspaces;

public sealed class CrossWorkspaceRefreshService
{
    private static readonly TimeSpan DefaultLockBusyWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultLockBusyPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly WorkspaceRegistry _registry;
    private readonly Func<string, string, bool, ExtractReport> _scan;
    private readonly Func<string, IDisposable?> _acquireLock;
    private readonly Func<string, long> _readLatestRevision;
    private readonly TimeSpan _lockBusyWait;
    private readonly TimeSpan _lockBusyPollInterval;
    private readonly Action<TimeSpan> _sleep;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SymbolSearchSidecar _sidecar;

    public CrossWorkspaceRefreshService(
        WorkspaceRegistry registry, JulieExtractRunner runner, SymbolSearchSidecar sidecar)
        : this(
            registry,
            (root, db, force) => runner.Scan(root, db, force),
            millerDir => SingleWriterLock.TryAcquire(millerDir),
            ReadLatestRevision,
            DefaultLockBusyWait,
            DefaultLockBusyPollInterval,
            Thread.Sleep,
            () => DateTimeOffset.UtcNow,
            sidecar)
    {
    }

    internal CrossWorkspaceRefreshService(
        WorkspaceRegistry registry,
        Func<string, string, bool, ExtractReport> scan,
        Func<string, IDisposable?> acquireLock,
        Func<string, long> readLatestRevision,
        TimeSpan lockBusyWait,
        TimeSpan lockBusyPollInterval,
        Action<TimeSpan> sleep,
        Func<DateTimeOffset> utcNow,
        SymbolSearchSidecar sidecar)
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

        _registry = registry;
        _scan = scan;
        _acquireLock = acquireLock;
        _readLatestRevision = readLatestRevision;
        _lockBusyWait = lockBusyWait;
        _lockBusyPollInterval = lockBusyPollInterval;
        _sleep = sleep;
        _utcNow = utcNow;
        _sidecar = sidecar;
    }

    public WorkspaceRefreshResult Refresh(string workspaceId, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        WorkspaceRegistryRow row = GetRequiredRow(workspaceId);

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
                Error: error);
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
                Error: ex.Message);
        }

        string millerDir = Path.GetDirectoryName(row.IndexDbPath)
            ?? throw new InvalidOperationException(
                $"Cannot determine the .miller directory for index DB path '{row.IndexDbPath}'.");

        using IDisposable? lease = _acquireLock(millerDir);
        if (lease is null)
            return WaitForExternalRevision(row);

        try
        {
            ExtractReport report = _scan(row.CanonicalRoot, row.IndexDbPath, force);

            // No workspace_id echo to cross-check in v1: julie-extract self-rejects a DB built for a different
            // root (exit 3 RootMismatch, design §4.1), so a wrong-DB scan throws and is handled by the catch below.
            long revision = report.Revision ?? _readLatestRevision(row.IndexDbPath);
            string? warning = PartialExtractLog.DescribePartial(report);
            _registry.MarkScanned(row.WorkspaceId, revision, _utcNow());

            // Phase 4: this is the one safe writer for an external workspace's search.db — it holds the
            // workspace single-writer lock around the scan. Build the sidecar here (off the search hot path,
            // skipped when already revision-fresh). Best-effort: a sidecar build failure must NEVER undo a
            // successful scan/refresh — reads self-heal to the in-memory index.
            TryBuildSidecar(row.IndexDbPath, revision);

            WorkspaceRefreshStatus status = revision > (row.LastRevision ?? 0)
                ? WorkspaceRefreshStatus.Refreshed
                : WorkspaceRefreshStatus.Unchanged;
            return new WorkspaceRefreshResult(
                status,
                row.WorkspaceId,
                row.CanonicalRoot,
                row.IndexDbPath,
                revision,
                Scanned: true,
                WarningText: warning);
        }
        catch (Exception ex)
        {
            _registry.MarkError(row.WorkspaceId, ex.Message, _utcNow());
            return new WorkspaceRefreshResult(
                WorkspaceRefreshStatus.Failed,
                row.WorkspaceId,
                row.CanonicalRoot,
                row.IndexDbPath,
                row.LastRevision,
                Scanned: false,
                Error: ex.Message);
        }
    }

    // Build the external workspace's search.db sidecar best-effort: enabled-and-stale ⇒ rebuild, else no-op.
    // Swallows build failures by design — the refresh's contract is the scanned symbols.db; the derived sidecar
    // is optional and a failure must not surface as a refresh error (reads fall back to the in-memory index).
    private void TryBuildSidecar(string symbolsDbPath, long revision)
    {
        try
        {
            _sidecar.EnsureBuilt(symbolsDbPath, revision);
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or InvalidOperationException
                or UnauthorizedAccessException or ArgumentException or IncompatibleExtractException)
        {
            // Best-effort: the sidecar is a rebuildable derived artifact; the next refresh retries.
        }
    }

    private WorkspaceRefreshResult WaitForExternalRevision(WorkspaceRegistryRow row)
    {
        long baseline = row.LastRevision ?? 0;
        long? lastReadableRevision = row.LastRevision;
        DateTimeOffset deadline = _utcNow() + _lockBusyWait;

        while (_utcNow() < deadline)
        {
            if (TryReadLatestRevision(row, out long latest))
            {
                lastReadableRevision = latest;
                if (latest > baseline)
                {
                    _registry.MarkScanned(row.WorkspaceId, latest, _utcNow());
                    return new WorkspaceRefreshResult(
                        WorkspaceRefreshStatus.Refreshed,
                        row.WorkspaceId,
                        row.CanonicalRoot,
                        row.IndexDbPath,
                        latest,
                        Scanned: false);
                }
            }

            _sleep(_lockBusyPollInterval);
        }

        if (!File.Exists(row.IndexDbPath))
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
                Error: error);
        }

        string warning =
            "Target workspace indexer lock is busy; freshness was not confirmed before serving the latest readable DB.";
        return new WorkspaceRefreshResult(
            WorkspaceRefreshStatus.LockBusy,
            row.WorkspaceId,
            row.CanonicalRoot,
            row.IndexDbPath,
            lastReadableRevision,
            Scanned: false,
            WarningText: warning);
    }

    private bool TryReadLatestRevision(WorkspaceRegistryRow row, out long revision)
    {
        try
        {
            revision = _readLatestRevision(row.IndexDbPath);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or InvalidOperationException
                                       or SqliteException)
        {
            revision = 0;
            return false;
        }
    }

    private WorkspaceRegistryRow GetRequiredRow(string workspaceId) =>
        _registry.Get(workspaceId) ?? throw new KeyNotFoundException(
            $"Workspace registry row '{workspaceId}' was not found.");

    private static long ReadLatestRevision(string dbPath)
    {
        using var reader = new FreshnessReader(dbPath);
        return reader.LatestRevision();
    }
}
