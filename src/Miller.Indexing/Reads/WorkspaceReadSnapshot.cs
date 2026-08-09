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
    WorkspaceReadMode Mode,
    string? GenerationName = null,
    long? ManifestGeneration = null,
    string? ResolutionState = null,
    string? ResolutionBaseId = null,
    long? ResolutionDeltaGeneration = null,
    long? ResolutionExactAt = null)
{
    public string IndexIdentity => Mode == WorkspaceReadMode.FamilyStore
        ? string.Join(
            ':',
            "store",
            ArtifactOrStoreId,
            ViewId,
            GenerationName,
            ManifestGeneration,
            Freshness.ManifestHash,
            ResolutionBaseId,
            ResolutionDeltaGeneration,
            ResolutionExactAt)
        : ArtifactOrStoreId;
}
