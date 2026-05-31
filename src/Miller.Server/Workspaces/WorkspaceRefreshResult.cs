namespace Miller.Server.Workspaces;

public enum WorkspaceRefreshStatus
{
    Refreshed,
    ObservedRevision,
    Missing,
    LockBusy,
    Error,
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
            WorkspaceRefreshStatus.ObservedRevision => "observed_revision",
            WorkspaceRefreshStatus.Missing => "missing",
            WorkspaceRefreshStatus.LockBusy => "lock_busy",
            WorkspaceRefreshStatus.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(Status), Status, "Unknown workspace refresh status."),
        };
}
