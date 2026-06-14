using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Miller.Indexing;

namespace Miller.Server.Hosting;

internal sealed class WorkspaceRegistryScanPublisher
{
    private readonly Func<WorkspaceContext, string, long?, WorkspaceRegistryRow> _markScanned;
    private readonly ILogger _logger;

    public WorkspaceRegistryScanPublisher(ILogger? logger = null)
        : this(IndexBootstrapService.MarkRegistryScanned, logger ?? NullLogger.Instance)
    {
    }

    internal WorkspaceRegistryScanPublisher(
        Func<WorkspaceContext, string, long?, WorkspaceRegistryRow> markScanned,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(markScanned);
        ArgumentNullException.ThrowIfNull(logger);

        _markScanned = markScanned;
        _logger = logger;
    }

    public WorkspaceRegistryRow MarkScanned(WorkspaceContext workspace, string workspaceId, long? revision) =>
        _markScanned(workspace, workspaceId, revision);

    public bool TryMarkScanned(WorkspaceContext workspace, string? workspaceId, long revision)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return false;

        try
        {
            _markScanned(workspace, workspaceId, revision);
            return true;
        }
        catch (Exception ex) when (
            ex is SqliteException or IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogWarning(ex,
                "Failed to update workspace registry revision after index convergence; status views may show stale revision metadata.");
            return false;
        }
    }
}
