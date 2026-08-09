namespace Miller.Indexing.Reads;

public enum WorkspaceReadMode
{
    LegacyArtifact,
    FamilyStore,
}

public sealed record WorkspaceFreshnessToken(
    string ArtifactOrStoreId,
    long Revision,
    string? ManifestHash = null,
    long? StoreLogSequence = null,
    string? ResolutionStamp = null,
    string? SearchStamp = null,
    string? ContentStamp = null,
    string? VectorStamp = null);

public sealed record WorkspaceReadSnapshot(
    string WorkspaceRoot,
    string? WorkspaceId,
    string ArtifactOrStoreId,
    string ViewId,
    WorkspaceFreshnessToken Freshness,
    string IndexLevel,
    WorkspaceReadMode Mode);
