using Miller.Indexing;
using Miller.Indexing.Reads;

namespace Miller.Server.Workspaces;

/// <summary>
/// The resolved source-region search read model for one workspace. Region text is served only from the
/// revision-fresh <c>search.db</c> sidecar; there is no in-memory fallback.
/// </summary>
/// <param name="IndexLevel">
/// The artifact's <c>artifact_metadata.index_level</c>, read through <paramref name="ReadSession"/> when the context
/// is built. <c>source_regions</c> is one of the tables a symbols-level scan leaves empty, so an unguarded region
/// search there returns "no regions" rather than "not extracted yet". Defaults to
/// <see cref="IndexLevels.FullMetadataValue"/>, the value under which no guard fires.
/// </param>
public sealed record WorkspaceRegionSearchContext(
    IRegionSearchIndex Index,
    WorkspaceReadHandle ReadSession,
    string? WorkspaceId,
    string WorkspaceRoot,
    long Revision,
    bool? IndexFresh,
    string FreshnessStatus,
    string? WarningText,
    string? DisplayId = null,
    string IndexLevel = IndexLevels.FullMetadataValue) : IDisposable
{
    public WorkspaceReadSnapshot Snapshot => ReadSession.Snapshot;

    public void Dispose()
    {
        if (Snapshot.Mode == WorkspaceReadMode.FamilyStore)
            ReadSession.Dispose();
    }
}
