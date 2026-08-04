using Miller.Indexing;

namespace Miller.Server.Workspaces;

/// <param name="IndexLevel">
/// The artifact's <c>artifact_metadata.index_level</c>, read from <paramref name="IndexDbPath"/> when the context
/// is built. This context carries no index at all, so the level is its consumers' only way to tell a
/// symbols-level <c>structural_facts</c> table apart from a repository that genuinely has no patterns in it.
/// Defaults to <see cref="IndexLevels.FullMetadataValue"/>, the value under which no guard fires.
/// </param>
public sealed record WorkspaceArtifactContext(
    string IndexDbPath,
    string? WorkspaceId,
    string WorkspaceRoot,
    long Revision,
    bool? IndexFresh,
    string FreshnessStatus,
    string? WarningText,
    string? DisplayId = null,
    string IndexLevel = IndexLevels.FullMetadataValue);
