namespace Miller.Server.Workspaces;

public enum WorkspaceRefreshStatus
{
    Refreshed,
    Unchanged,
    LockBusy,
    MissingRoot,
    MissingIndex,
    Failed,
}

public sealed record WorkspaceRefreshResult(
    WorkspaceRefreshStatus Status,
    string WorkspaceId,
    string WorkspaceRoot,
    string IndexDbPath,
    long? Revision = null,
    bool Scanned = false,
    string? WarningText = null,
    string? Error = null)
{
    public string StatusText =>
        Status switch
        {
            WorkspaceRefreshStatus.Refreshed => "refreshed",
            WorkspaceRefreshStatus.Unchanged => "unchanged",
            WorkspaceRefreshStatus.LockBusy => "lock_busy",
            WorkspaceRefreshStatus.MissingRoot => "missing_root",
            WorkspaceRefreshStatus.MissingIndex => "missing_index",
            WorkspaceRefreshStatus.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(Status), Status, "Unknown workspace refresh status."),
        };
}
