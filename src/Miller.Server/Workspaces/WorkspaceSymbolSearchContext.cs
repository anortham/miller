using Miller.Indexing;

namespace Miller.Server.Workspaces;

/// <param name="IndexLevel">
/// The artifact's <c>artifact_metadata.index_level</c>, read from <paramref name="IndexDbPath"/> when the context
/// is built. Search itself is complete at symbols level, but the marker route reads source-region text that a
/// symbols-level scan has not extracted, so the route decides from this rather than from the runtime type of
/// <paramref name="Index"/> — which is an FTS sidecar or a lean projection, never proof of a level. Defaults to
/// <see cref="IndexLevels.FullMetadataValue"/>, the value under which no guard fires.
/// </param>
public sealed record WorkspaceSymbolSearchContext(
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
