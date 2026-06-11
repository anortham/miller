namespace Miller.Server.Workspaces;

public enum WorkspaceRefreshStatus
{
    Refreshed,
    Unchanged,
    LockBusy,
    MissingRoot,
    MissingIndex,
    Failed,

    /// <summary>
    /// The one-shot writer refused to scan: its bundled extractor is not eligible to rewrite this artifact
    /// (version-aware leadership D2 — the artifact's <c>binary_version</c> never goes backwards).
    /// </summary>
    IneligibleExtractor,
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
            WorkspaceRefreshStatus.IneligibleExtractor => "ineligible_extractor",
            _ => throw new ArgumentOutOfRangeException(nameof(Status), Status, "Unknown workspace refresh status."),
        };
}
