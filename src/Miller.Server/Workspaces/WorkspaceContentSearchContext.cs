using Miller.Indexing;

namespace Miller.Server.Workspaces;

/// <summary>
/// The resolved content/docs search read-model for one workspace (phase 3): a freshness-verified
/// <see cref="IContentSearchIndex"/> plus the same identity/freshness envelope the symbol-search context
/// carries, so the search tool can render content hits and a freshness banner without re-resolving. Built
/// from the docs-like file corpus by <see cref="ContentSearchProjectionLoader"/> — never the full graph.
/// </summary>
public sealed record WorkspaceContentSearchContext(
    IContentSearchIndex Index,
    string IndexDbPath,
    string? WorkspaceId,
    string WorkspaceRoot,
    long Revision,
    bool? IndexFresh,
    string FreshnessStatus,
    string? WarningText,
    string? DisplayId = null);
