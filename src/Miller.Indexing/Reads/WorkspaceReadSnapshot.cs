namespace Miller.Indexing.Reads;

public sealed record WorkspaceFreshnessProbe(
    long Revision,
    string? StoreInstanceId,
    string? ViewId,
    long? ManifestGeneration = null,
    string? ManifestHash = null,
    string? StoreRoot = null,
    string? BinaryVersion = null,
    string? IndexGenerationIdentity = null);

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
    public string VectorArtifactId => Mode == WorkspaceReadMode.FamilyStore
        ? $"{Freshness.StoreInstanceId ?? ArtifactOrStoreId}:{Freshness.ViewId ?? ViewId}"
        : ArtifactOrStoreId;

    public long VectorRevision => Mode == WorkspaceReadMode.FamilyStore
        ? Freshness.StoreLogSequence ?? Freshness.Revision
        : Freshness.Revision;

    /// <summary>
    /// CT freshness identity: changes only on a generation-scale event, never on a routine import.
    /// Family mode composes only the components that stay stable across delta AND full imports
    /// (a routine import advances the manifest generation, the manifest hash, and the log sequence,
    /// so none of those may appear here): the family id, the view id, and the store generation name
    /// from the CURRENT pointer. Each event that can restart or reuse the revision counter changes
    /// one of them — a generation promotion flips CURRENT to gen-&lt;n+1&gt; (store contract §12), and a
    /// recreated store (its root destroyed and reimported, which restarts the counter at gen-001)
    /// mints a new view id (StoreFamilyResolver.PlanViewForAbsentCatalog; defect D4, 2026-08-21).
    /// A replanned view inside a LIVING store keeps its view id safely: the family-wide store log
    /// keeps the counter monotonic there, as do in-place imports ("sequence continuity across
    /// promotion", docs/plans/2026-08-07-index-store-v4-contract.md). The <c>ctgen1:</c> prefix
    /// guarantees no legacy-format identity can ever equal a new-format one.
    /// </summary>
    public string IndexGenerationIdentity => Mode == WorkspaceReadMode.FamilyStore
        ? string.Join(
            ':',
            "ctgen1",
            "store",
            ArtifactOrStoreId,
            Freshness.ViewId ?? ViewId,
            Freshness.GenerationName ?? GenerationName)
        : string.Join(
            ':',
            "ctgen1",
            "artifact",
            ArtifactOrStoreId,
            MillerExtractContract.ExpectedHashAlgorithm);

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
