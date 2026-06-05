using Miller.Indexing;

namespace Miller.Server.Workspaces;

/// <summary>
/// The resolved source-region search read model for one workspace. Region text is served only from the
/// revision-fresh <c>search.db</c> sidecar; there is no in-memory fallback.
/// </summary>
public sealed record WorkspaceRegionSearchContext(
    IRegionSearchIndex Index,
    string IndexDbPath,
    string? WorkspaceId,
    string WorkspaceRoot,
    long Revision,
    bool? IndexFresh,
    string FreshnessStatus,
    string? WarningText,
    string? DisplayId = null);
