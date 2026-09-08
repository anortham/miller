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
    /// Freshness for the serve-then-refresh arm. Background scheduling never forces source freshness to false by
    /// itself: when no source check was observed, source freshness is unknown (null) rather than false.
    /// </summary>
    public static bool? IndexFreshFor(
        WorkspaceRefreshResult? refreshResult, WorkspaceRegistryRow row, bool refreshPending) =>
        IndexFreshFor(refreshResult, row);

    /// <summary>
    /// Status for the serve-then-refresh arm. A row that already reports something WORSE than healthy keeps its own
    /// word — an <c>error</c>/<c>missing</c> row is the louder fact, and the pending refresh is its remedy, not a
    /// reason to hide it.
    /// </summary>
    public static string FreshnessStatusFor(
        WorkspaceRefreshResult? refreshResult, WorkspaceRegistryRow row, bool refreshPending) =>
        refreshPending && IndexFreshFor(refreshResult, row) is true or null
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
            null => row.State switch
            {
                WorkspaceRegistryState.Current or WorkspaceRegistryState.Ready => null,
                _ => false,
            },
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
        WarningTextFor(refreshResult, operationSnapshot: null);

    public static string? WarningTextFor(
        WorkspaceRefreshResult? refreshResult,
        BackgroundRefreshOperationSnapshot? operationSnapshot)
    {
        if (refreshResult?.Status == WorkspaceRefreshStatus.LockBusy)
            return refreshResult.WarningText;

        if (!string.IsNullOrWhiteSpace(refreshResult?.WarningText))
            return refreshResult.WarningText;

        if (operationSnapshot?.State == BackgroundRefreshActivityState.Failed &&
            !string.IsNullOrWhiteSpace(operationSnapshot.FailureMessage))
        {
            return $"Background refresh failed: {operationSnapshot.FailureMessage}";
        }

        return null;
    }
}
