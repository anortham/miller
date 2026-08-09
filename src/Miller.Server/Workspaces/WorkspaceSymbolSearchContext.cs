using Miller.Indexing;
using Miller.Indexing.Reads;

namespace Miller.Server.Workspaces;

/// <param name="IndexLevel">
/// The artifact's <c>artifact_metadata.index_level</c>, read through <paramref name="ReadSession"/> when the context
/// is built. Search itself is complete at symbols level, but the marker route reads <c>code.marker.v1</c> rows
/// from <c>structural_facts</c>, which a symbols-level scan leaves empty, so the route decides from this rather
/// than from the runtime type of <paramref name="Index"/> — which is an FTS sidecar or a lean projection, never
/// proof of a level. Defaults to <see cref="IndexLevels.FullMetadataValue"/>, the value under which no guard fires.
/// </param>
public sealed record WorkspaceSymbolSearchContext(
    ISymbolLookupIndex Index,
    WorkspaceReadHandle ReadSession,
    string? WorkspaceId,
    string WorkspaceRoot,
    long Revision,
    bool? IndexFresh,
    string FreshnessStatus,
    string? WarningText,
    string? DisplayId = null,
    bool IsCurrent = true,
    string IndexLevel = IndexLevels.FullMetadataValue) : IDisposable
{
    public WorkspaceReadSnapshot Snapshot => ReadSession.Snapshot;

    public void Dispose()
    {
        if (Snapshot.Mode == WorkspaceReadMode.FamilyStore)
            ReadSession.Dispose();
    }
}
