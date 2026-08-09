using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Server.Resolution;

namespace Miller.Server.Workspaces;

/// <summary>
/// The immutable read surface for one tool call. The index and resolver are built over the same captured
/// <see cref="MillerRepositoryIndex"/> so a concurrent holder swap cannot split resolution across two revisions.
/// </summary>
/// <param name="IndexLevel">
/// The artifact's <c>artifact_metadata.index_level</c>, read through <paramref name="ReadSession"/> when the context
/// is built. Which layers exist is a property of the ARTIFACT, not of the index implementation serving the read,
/// so tools decide from this rather than from the runtime type of <paramref name="Index"/>. Defaults to
/// <see cref="IndexLevels.FullMetadataValue"/> — the level of every pre-levels artifact, and the value under which
/// no guard fires.
/// </param>
public sealed record WorkspaceReadContext(
    MillerRepositoryIndex Index,
    SmartTargetResolver Resolver,
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
