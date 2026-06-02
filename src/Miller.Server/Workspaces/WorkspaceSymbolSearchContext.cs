using Miller.Indexing;

namespace Miller.Server.Workspaces;

public sealed record WorkspaceSymbolSearchContext(
    ISymbolLookupIndex Index,
    string IndexDbPath,
    string? WorkspaceId,
    string WorkspaceRoot,
    long Revision,
    bool? IndexFresh,
    string FreshnessStatus,
    string? WarningText,
    string? DisplayId = null);
