using Miller.Indexing;

namespace Miller.Server.Workspaces;

internal static class WorkspaceFreshnessView
{
    public static bool? IndexFreshFor(WorkspaceRefreshResult? refreshResult, WorkspaceRegistryRow row) =>
        refreshResult?.Status switch
        {
            WorkspaceRefreshStatus.Refreshed => true,
            WorkspaceRefreshStatus.Unchanged => true,
            WorkspaceRefreshStatus.LockBusy => false,
            WorkspaceRefreshStatus.MissingRoot => false,
            WorkspaceRefreshStatus.MissingIndex => false,
            WorkspaceRefreshStatus.Failed => false,
            null => row.State is WorkspaceRegistryState.Current
                or WorkspaceRegistryState.Ready,
            _ => false,
        };

    public static string FreshnessStatusFor(WorkspaceRefreshResult? refreshResult, WorkspaceRegistryRow row) =>
        refreshResult?.Status switch
        {
            WorkspaceRefreshStatus.LockBusy => "unconfirmed_lock_busy",
            null => row.StateText,
            _ => refreshResult.StatusText,
        };

    public static string? WarningTextFor(WorkspaceRefreshResult? refreshResult) =>
        refreshResult?.Status == WorkspaceRefreshStatus.LockBusy
            ? refreshResult.WarningText
            : refreshResult?.WarningText;
}
