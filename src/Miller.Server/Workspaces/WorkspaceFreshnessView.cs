using Miller.Indexing;

namespace Miller.Server.Workspaces;

internal static class WorkspaceFreshnessView
{
    /// <summary>
    /// The freshness word for a read that served the PINNED view and left a refresh running behind it: nothing is
    /// confirmed, nothing failed, and the next call is expected to see newer data.
    /// </summary>
    public const string RefreshPendingStatus = "refresh_pending";

    /// <summary>
    /// Freshness for the serve-then-refresh arm. A pending refresh means freshness was never CONFIRMED, so
    /// <c>index_fresh</c> is false — the read is honest that it served whatever the pinned view held.
    /// </summary>
    public static bool? IndexFreshFor(
        WorkspaceRefreshResult? refreshResult, WorkspaceRegistryRow row, bool refreshPending) =>
        refreshPending ? false : IndexFreshFor(refreshResult, row);

    /// <summary>
    /// Status for the serve-then-refresh arm. A row that already reports something WORSE than healthy keeps its own
    /// word — an <c>error</c>/<c>missing</c> row is the louder fact, and the pending refresh is its remedy, not a
    /// reason to hide it.
    /// </summary>
    public static string FreshnessStatusFor(
        WorkspaceRefreshResult? refreshResult, WorkspaceRegistryRow row, bool refreshPending) =>
        refreshPending && IndexFreshFor(refreshResult, row) == true
            ? RefreshPendingStatus
            : FreshnessStatusFor(refreshResult, row);

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
