using Miller.Indexing;

namespace Miller.Server.Workspaces;

/// <summary>
/// The resolved text-content corpus search read model for one workspace. This wraps the revision-fresh
/// <c>content.db</c> sidecar used by explicit source/docs/external/web text modes.
/// </summary>
public sealed record WorkspaceTextContentSearchContext(
    ITextContentSearchIndex Index,
    string IndexDbPath,
    string? WorkspaceId,
    string WorkspaceRoot,
    long Revision,
    bool? IndexFresh,
    string FreshnessStatus,
    string? WarningText,
    string? DisplayId = null,
    bool IsCurrent = true);
