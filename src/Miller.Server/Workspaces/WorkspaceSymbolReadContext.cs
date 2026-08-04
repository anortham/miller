using Miller.Indexing;

namespace Miller.Server.Workspaces;

/// <param name="IndexLevel">
/// The artifact's <c>artifact_metadata.index_level</c>, read from <paramref name="IndexDbPath"/> when the context
/// is built. A cross-workspace read is served by a lean FTS <see cref="ISymbolLookupIndex"/> rather than a
/// <see cref="MillerRepositoryIndex"/>, so the level has to travel with the context — inferring it from the index
/// type reports "full" for exactly the reads that cannot see it. Defaults to
/// <see cref="IndexLevels.FullMetadataValue"/>, the value under which no guard fires.
/// </param>
public sealed record WorkspaceSymbolReadContext(
    ISymbolLookupIndex Index,
    string IndexDbPath,
    string? WorkspaceId,
    string WorkspaceRoot,
    long Revision,
    bool? IndexFresh,
    string FreshnessStatus,
    string? WarningText,
    string? DisplayId = null,
    bool IsCurrent = true,
    string IndexLevel = IndexLevels.FullMetadataValue);
