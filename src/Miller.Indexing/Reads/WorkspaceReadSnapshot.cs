namespace Miller.Indexing.Reads;

public sealed record WorkspaceFreshnessProbe(
    long Revision,
    string? StoreInstanceId,
    string? ViewId,
    long? ManifestGeneration = null,
    string? ManifestHash = null);

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
    string? VectorStamp = null,
    string? StoreInstanceId = null,
    string? ViewId = null,
    string? GenerationName = null,
    long? ManifestGeneration = null,
    string? IndexLevel = null,
    string? LevelStampL1 = null,
    string? LevelStampL2 = null,
    string? LevelStampL3 = null);

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
            Freshness.StoreInstanceId ?? ArtifactOrStoreId,
            Freshness.ViewId ?? ViewId,
            Freshness.GenerationName ?? GenerationName,
            Freshness.ManifestGeneration ?? ManifestGeneration,
            Freshness.ManifestHash,
            Freshness.StoreLogSequence,
            Freshness.IndexLevel ?? IndexLevel,
            Freshness.LevelStampL1,
            Freshness.LevelStampL2,
            Freshness.LevelStampL3,
            Freshness.ResolutionStamp,
            ResolutionBaseId,
            ResolutionDeltaGeneration,
            ResolutionExactAt)
        : ArtifactOrStoreId;
}
