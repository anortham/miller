using Miller.Indexing;
using Miller.Server.Workspaces;
using Microsoft.Extensions.Logging;

namespace Miller.Server.Hosting;

internal interface IEditRecoveryFallback
{
    bool TryFallbackRefresh();
}

/// <summary>Converges an edit against a registered workspace that is not serviced by this process's leader.</summary>
public sealed class RegisteredWorkspaceWriteThrough : IEditWriteThrough, IEditRecoveryFallback
{
    private readonly string _workspaceId;
    private readonly string _workspaceRoot;
    private readonly string _millerDir;
    private readonly Func<long?> _readRevision;
    private readonly Func<WorkspaceRefreshResult> _fallbackRefresh;
    private readonly ILogger<RegisteredWorkspaceWriteThrough> _logger;
    private long? _recoveryBaselineRevision;
    private bool _recoveryRequested;

    /// <summary>Construct the registered-target queue and bounded refresh fallback.</summary>
    public RegisteredWorkspaceWriteThrough(
        string workspaceId,
        string workspaceRoot,
        string indexDbPath,
        WorkspaceRegistry registry,
        CrossWorkspaceRefreshService refreshService,
        ILogger<RegisteredWorkspaceWriteThrough> logger)
        : this(
            workspaceId,
            workspaceRoot,
            indexDbPath,
            registry,
            _ => refreshService.Refresh(
                workspaceId,
                scanAdmission: ScanAdmissionBudget.Of(TimeSpan.Zero),
                bypassBackoff: true),
            logger)
    {
        ArgumentNullException.ThrowIfNull(refreshService);
    }

    internal RegisteredWorkspaceWriteThrough(
        string workspaceId,
        string workspaceRoot,
        string indexDbPath,
        WorkspaceRegistry registry,
        Func<string, WorkspaceRefreshResult> fallbackRefresh,
        ILogger<RegisteredWorkspaceWriteThrough> logger)
        : this(
            workspaceId,
            workspaceRoot,
            indexDbPath,
            () => registry.Get(workspaceId)?.LastRevision,
            () => fallbackRefresh(workspaceId),
            logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(fallbackRefresh);
    }

    internal RegisteredWorkspaceWriteThrough(
        string workspaceId,
        string workspaceRoot,
        string indexDbPath,
        Func<long?> readRevision,
        Func<WorkspaceRefreshResult> fallbackRefresh,
        ILogger<RegisteredWorkspaceWriteThrough> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexDbPath);
        ArgumentNullException.ThrowIfNull(readRevision);
        ArgumentNullException.ThrowIfNull(fallbackRefresh);
        ArgumentNullException.ThrowIfNull(logger);
        _workspaceId = workspaceId;
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _millerDir = Path.GetDirectoryName(Path.GetFullPath(indexDbPath))
            ?? throw new ArgumentException("The target index path has no parent directory.", nameof(indexDbPath));
        _readRevision = readRevision;
        _fallbackRefresh = fallbackRefresh;
        _logger = logger;
    }

    public void Converge(IReadOnlyList<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        string[] targetFiles = TargetFiles(changedFiles);
        if (targetFiles.Length == 0)
            return;

        TryRequest(targetFiles);
    }

    public StaleRecoveryAttempt TryRecoverStaleFile(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        string[] targetFiles = TargetFiles([fullPath]);
        if (targetFiles.Length == 0)
            return StaleRecoveryAttempt.None;

        _recoveryBaselineRevision = ReadRevision();
        _recoveryRequested = true;
        TryRequest(targetFiles);
        return StaleRecoveryAttempt.Requested;
    }

    bool IEditRecoveryFallback.TryFallbackRefresh() => TryFallbackRefresh();

    internal bool TryFallbackRefresh()
    {
        if (!_recoveryRequested)
            return false;

        _recoveryRequested = false;
        long? after = ReadRevision();
        if (after is { } advanced &&
            (_recoveryBaselineRevision is null || advanced > _recoveryBaselineRevision.Value))
        {
            return true;
        }

        try
        {
            WorkspaceRefreshResult result = _fallbackRefresh();
            return result.Status is WorkspaceRefreshStatus.Refreshed or WorkspaceRefreshStatus.Unchanged;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Registered edit target refresh fallback failed for {WorkspaceId}.", _workspaceId);
            return false;
        }
    }

    private long? ReadRevision()
    {
        try
        {
            return _readRevision();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Could not read registered edit target revision for {WorkspaceId}.", _workspaceId);
            return null;
        }
    }

    private bool TryRequest(IReadOnlyList<string> targetFiles)
    {
        try
        {
            LeaderScanRequestQueue.RequestFileConverge(_millerDir, _workspaceId, targetFiles);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Could not queue registered edit target convergence for {WorkspaceId}.", _workspaceId);
            return false;
        }
    }

    private string[] TargetFiles(IReadOnlyList<string> paths)
    {
        var targetFiles = new List<string>(paths.Count);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                continue;

            string fullPath = Path.GetFullPath(path);
            string relative = Path.GetRelativePath(_workspaceRoot, fullPath);
            if (relative is "." or ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            targetFiles.Add(fullPath);
        }

        return targetFiles.ToArray();
    }
}
