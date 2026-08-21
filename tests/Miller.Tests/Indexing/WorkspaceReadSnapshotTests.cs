using Miller.Indexing.Reads;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class WorkspaceReadSnapshotTests
{
    [Fact]
    public void FamilyStore_GenerationIdentityComposesOnlyGenerationFields()
    {
        WorkspaceReadSnapshot snapshot = StoreSnapshot();

        Assert.Equal("ctgen1:store:fam-1:view-1:gen-000002", snapshot.IndexGenerationIdentity);
    }

    [Fact]
    public void FamilyStore_RoutineDeltaImportDoesNotChangeTheGenerationIdentity()
    {
        // Live-store reality (2026-08-21 evidence): a routine delta import moves the manifest
        // generation, the manifest hash, and the store log sequence. Family, view, and the
        // generation name stay. The identity must stay byte-identical across such an import.
        WorkspaceReadSnapshot before = StoreSnapshot(
            revision: 32161,
            storeLogSequence: 32161,
            manifestGeneration: 736,
            manifestHash: "8db774a379ec7c56");
        WorkspaceReadSnapshot after = StoreSnapshot(
            revision: 32459,
            storeLogSequence: 32459,
            manifestGeneration: 750,
            manifestHash: "071acf8182e55c97");

        Assert.Equal(before.IndexGenerationIdentity, after.IndexGenerationIdentity);
        // The full identity still moves — that is the old key the generation identity replaces.
        Assert.NotEqual(before.IndexIdentity, after.IndexIdentity);
    }

    [Fact]
    public void FamilyStore_ExcludedFieldsDoNotChangeTheGenerationIdentity()
    {
        WorkspaceReadSnapshot plain = StoreSnapshot();
        WorkspaceReadSnapshot decorated = StoreSnapshot(
            revision: 99,
            storeLogSequence: 250,
            manifestGeneration: 750,
            manifestHash: "mh-9",
            resolutionStamp: "res-9",
            indexLevel: "l1",
            levelStampL1: "stamp-a",
            resolutionState: "unbound",
            resolutionBaseId: "base-3",
            resolutionDeltaGeneration: 12,
            resolutionExactAt: 77);

        Assert.Equal(plain.IndexGenerationIdentity, decorated.IndexGenerationIdentity);
    }

    [Fact]
    public void FamilyStore_GenerationScaleEventsChangeTheGenerationIdentity()
    {
        WorkspaceReadSnapshot current = StoreSnapshot();

        // A store generation promotion flips the CURRENT pointer to gen-<n+1> (contract §12).
        WorkspaceReadSnapshot promoted = StoreSnapshot(generationName: "gen-000003");
        // A replanned recovery mints a new view id (StoreFamilyResolver / StoreViewReplan).
        WorkspaceReadSnapshot mintedView = StoreSnapshot(viewId: "view-2");
        // A recreated store mints a new family id.
        WorkspaceReadSnapshot otherFamily = StoreSnapshot(familyId: "fam-2");

        Assert.NotEqual(current.IndexGenerationIdentity, promoted.IndexGenerationIdentity);
        Assert.NotEqual(current.IndexGenerationIdentity, mintedView.IndexGenerationIdentity);
        Assert.NotEqual(current.IndexGenerationIdentity, otherFamily.IndexGenerationIdentity);
    }

    [Fact]
    public void Legacy_GenerationIdentityIsThePrefixedArtifactIdPlusHashAlgorithm()
    {
        WorkspaceReadSnapshot snapshot = LegacySnapshot();

        Assert.Equal("ctgen1:artifact:art-1:blake3", snapshot.IndexGenerationIdentity);
    }

    [Fact]
    public void Legacy_RevisionAdvanceDoesNotChangeTheGenerationIdentityButAnArtifactChangeDoes()
    {
        WorkspaceReadSnapshot before = LegacySnapshot(revision: 1);
        WorkspaceReadSnapshot after = LegacySnapshot(revision: 2);
        WorkspaceReadSnapshot rebuilt = LegacySnapshot(artifactId: "art-2", revision: 1);

        Assert.Equal(before.IndexGenerationIdentity, after.IndexGenerationIdentity);
        Assert.NotEqual(before.IndexGenerationIdentity, rebuilt.IndexGenerationIdentity);
    }

    [Fact]
    public void GenerationIdentityNeverEqualsALegacyFormatIdentity()
    {
        WorkspaceReadSnapshot store = StoreSnapshot();
        WorkspaceReadSnapshot legacy = LegacySnapshot();

        Assert.StartsWith("ctgen1:", store.IndexGenerationIdentity, StringComparison.Ordinal);
        Assert.StartsWith("ctgen1:", legacy.IndexGenerationIdentity, StringComparison.Ordinal);
        // The old-format identities can never carry the prefix, so no stored row can collide.
        Assert.False(store.IndexIdentity.StartsWith("ctgen1:", StringComparison.Ordinal));
        Assert.False(legacy.IndexIdentity.StartsWith("ctgen1:", StringComparison.Ordinal));
        Assert.NotEqual(store.IndexIdentity, store.IndexGenerationIdentity);
        Assert.NotEqual(legacy.IndexIdentity, legacy.IndexGenerationIdentity);
    }

    internal static WorkspaceReadSnapshot StoreSnapshot(
        long revision = 42,
        long? storeLogSequence = 42,
        string familyId = "fam-1",
        string viewId = "view-1",
        string generationName = "gen-000002",
        long manifestGeneration = 7,
        string manifestHash = "mh-1",
        string? resolutionStamp = null,
        string indexLevel = "full",
        string? levelStampL1 = null,
        string? resolutionState = null,
        string? resolutionBaseId = null,
        long? resolutionDeltaGeneration = null,
        long? resolutionExactAt = null) =>
        new(
            WorkspaceRoot: "ws-root",
            WorkspaceId: "ws-1",
            ArtifactOrStoreId: familyId,
            ViewId: viewId,
            Freshness: new WorkspaceFreshnessToken(
                familyId,
                revision,
                manifestHash,
                storeLogSequence,
                resolutionStamp,
                StoreInstanceId: $"{familyId}:{generationName}",
                ViewId: viewId,
                GenerationName: generationName,
                ManifestGeneration: manifestGeneration,
                IndexLevel: indexLevel,
                LevelStampL1: levelStampL1),
            IndexLevel: indexLevel,
            Mode: WorkspaceReadMode.FamilyStore,
            GenerationName: generationName,
            ManifestGeneration: manifestGeneration,
            ResolutionState: resolutionState,
            ResolutionBaseId: resolutionBaseId,
            ResolutionDeltaGeneration: resolutionDeltaGeneration,
            ResolutionExactAt: resolutionExactAt);

    internal static WorkspaceReadSnapshot LegacySnapshot(string artifactId = "art-1", long revision = 1) =>
        new(
            WorkspaceRoot: "ws-root",
            WorkspaceId: "ws-1",
            ArtifactOrStoreId: artifactId,
            ViewId: "legacy",
            Freshness: new WorkspaceFreshnessToken(artifactId, revision),
            IndexLevel: "full",
            Mode: WorkspaceReadMode.LegacyArtifact);
}
