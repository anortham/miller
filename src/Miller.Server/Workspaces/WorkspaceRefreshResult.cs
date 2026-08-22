using Miller.Server.Hosting;

namespace Miller.Server.Workspaces;

public sealed record SidecarConvergenceFact(
    string Status,
    bool DidWork,
    bool Pending,
    bool LeaderRequired,
    string? Reason)
{
    public string? BoundedReason => BoundReason(Reason);

    internal static SidecarConvergenceFact From(StoreSidecarConvergenceOutcome outcome) =>
        new(
            outcome.Status,
            outcome.DidWork,
            outcome.Pending,
            outcome.LeaderRequired,
            BoundReason(outcome.Reason));

    private static string? BoundReason(string? reason) => string.IsNullOrWhiteSpace(reason)
        ? null
        : reason.Length <= 240 ? reason : reason[..240];
}

public sealed record SidecarConvergenceFacts(
    long TargetSequence,
    SidecarConvergenceFact Content,
    SidecarConvergenceFact Search,
    SidecarConvergenceFact Vector)
{
    public bool DidWork => Content.DidWork || Search.DidWork || Vector.DidWork;

    public bool Pending => Content.Pending || Search.Pending || Vector.Pending;

    public bool LeaderRequired => Content.LeaderRequired || Search.LeaderRequired || Vector.LeaderRequired;

    public string? Reason => BoundReason(
        string.Join(
            "; ",
            new[] { Content, Search, Vector }
                .Where(static outcome => !string.IsNullOrWhiteSpace(outcome.Reason))
                .Select(static outcome => outcome.Reason)));

    internal static SidecarConvergenceFacts From(StoreSidecarConvergenceResult result) =>
        new(
            result.TargetSequence,
            SidecarConvergenceFact.From(result.Content),
            SidecarConvergenceFact.From(result.Search),
            SidecarConvergenceFact.From(result.Vector));

    private static string? BoundReason(string reason) => string.IsNullOrWhiteSpace(reason)
        ? null
        : reason.Length <= 240 ? reason : reason[..240];
}

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
    string? ArtifactId = null,
    SidecarConvergenceFacts? Sidecars = null)
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
