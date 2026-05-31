using Miller.Indexing;
using Miller.Server.Resolution;

namespace Miller.Server.Workspaces;

/// <summary>
/// The immutable read surface for one tool call. The index and resolver are built over the same captured
/// <see cref="MillerRepositoryIndex"/> so a concurrent holder swap cannot split resolution across two revisions.
/// </summary>
public sealed record WorkspaceReadContext(
    MillerRepositoryIndex Index,
    SmartTargetResolver Resolver,
    string IndexDbPath,
    string? WorkspaceId,
    string WorkspaceRoot,
    long Revision,
    bool? IndexFresh,
    string FreshnessStatus,
    string? WarningText);
