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

/// <param name="ScanDuration">Wall time of the julie-extract scan attempt when one ran — including a FAILED or
/// killed scan (a timeout kill reports ~the timeout). Null when no scan ran (lock busy, missing root, …).
/// Recorded so fleet sweeps get per-workspace extract durations for free (2026-06-11 openclaw triage: a slow
/// scan under sweep load was indistinguishable from a hang without a measured duration).</param>
/// <param name="TotalDuration">Wall time of the whole refresh attempt (lock wait/poll + scan + sidecar
/// convergence), when measured.</param>
public sealed record WorkspaceRefreshResult(
    WorkspaceRefreshStatus Status,
    string WorkspaceId,
    string WorkspaceRoot,
    string IndexDbPath,
    long? Revision = null,
    bool Scanned = false,
    string? WarningText = null,
    string? Error = null,
    TimeSpan? ScanDuration = null,
    TimeSpan? TotalDuration = null,
    string? ArtifactId = null)
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
