using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreFamilyResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "miller-family-resolver-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SameLiveGitLineageSharesOneFamilyWithDistinctViews()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a", "ws-b", "root-b");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);
        DateTimeOffset created = Utc(1);

        StoreFamilyBinding first = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", created));
        StoreFamilyBinding second = resolver.ResolveOrCreate(Facts("ws-b", "root-b", "/repo/.git", created));

        Assert.Equal(first.FamilyId, second.FamilyId);
        Assert.NotEqual(first.ViewId, second.ViewId);
        Assert.Equal(Guid.Parse("11111111-1111-4111-8111-111111111111"), first.FamilyId);
        Assert.Equal(StoreBindingState.Planned, first.State);
        Assert.Equal(StoreBindingState.Planned, second.State);
        Assert.Single(registry.ListStoreFamilies());
        Assert.Equal(2, registry.ListStoreMembers().Count);
    }

    [Fact]
    public void PositiveLineageReplacementMintsANewFamilyButMissingEvidenceKeepsTheOldFamily()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
        ]);
        var resolver = Resolver(registry, ids);

        StoreFamilyBinding original = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", Utc(1)));
        StoreFamilyBinding unknown = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", null));
        StoreFamilyBinding unobserved = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/other/.git", Utc(2)));
        StoreFamilyBinding replacement = resolver.ResolveOrCreate(
            Facts("ws-a", "root-a", "/other/.git", Utc(2), rootReplacementObserved: true));

        // Missing evidence keeps the FAMILY binding. The view id is re-minted on every
        // absent-catalog resolve (review finding F4), so only the family is asserted stable.
        Assert.Equal(original.FamilyId, unknown.FamilyId);
        Assert.Equal(original.FamilyId, unobserved.FamilyId);
        Assert.NotEqual(original.FamilyId, replacement.FamilyId);
        Assert.NotEqual(original.ViewId, replacement.ViewId);
    }

    [Fact]
    public void KnownEvidencePromotesAnUnknownSharedLineageWithoutSplittingTheFamily()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a", "ws-b", "root-b");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);

        StoreFamilyBinding unknown = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", null));
        StoreFamilyBinding known = resolver.ResolveOrCreate(Facts("ws-b", "root-b", "/repo/.git", Utc(1)));

        Assert.Equal(unknown.FamilyId, known.FamilyId);
        Assert.Single(registry.ListStoreFamilies());
        Assert.Equal(Utc(1), registry.GetStoreFamily(known.FamilyId)?.CommonDirCreatedAtUtc);
    }

    [Fact]
    public void NonGitWorkspacesRemainSeparateSingletonFamilies()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a", "ws-b", "root-b");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);

        StoreFamilyBinding first = resolver.ResolveOrCreate(Facts("ws-a", "root-a", null, null));
        StoreFamilyBinding second = resolver.ResolveOrCreate(Facts("ws-b", "root-b", null, null));

        Assert.NotEqual(first.FamilyId, second.FamilyId);
        Assert.Equal(2, registry.ListStoreFamilies().Count);
    }

    [Fact]
    public void StoreCatalogRepairsStaleRegistryAndPointerIdentity()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        Guid catalogFamily = Guid.Parse("99999999-9999-4999-8999-999999999999");
        const string catalogView = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        WriteStoreCatalog(planned.StoreRoot, catalogFamily, catalogView, facts.WorkspaceRoot);
        StoreWorkspacePointer.Write(facts.WorkspaceRoot, planned with
        {
            ViewId = "stale-view",
        });

        StoreFamilyBinding repaired = resolver.ResolveOrCreate(facts);

        Assert.Equal(catalogFamily, repaired.FamilyId);
        Assert.Equal(catalogView, repaired.ViewId);
        Assert.Equal(StoreBindingState.Ready, repaired.State);
        Assert.Equal(catalogFamily, registry.GetStoreMember("ws-a")?.FamilyId);
        Assert.Equal(catalogView, registry.GetStoreMember("ws-a")?.ViewId);
        StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(facts.WorkspaceRoot));
        Assert.Equal(catalogFamily, pointer.FamilyId);
        Assert.Equal(catalogView, pointer.ViewId);
    }

    [Fact]
    public void ValidRootMatchingPointerIsAdoptedIntoAnEmptyRegistry()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        Guid familyId = Guid.Parse("99999999-9999-4999-8999-999999999999");
        const string viewId = "view-adopted";
        string storeRoot = Path.Combine(_directory, "copied-family");
        WriteReadyStore(storeRoot, familyId, viewId, facts.WorkspaceRoot);
        StoreFamilyBinding pointerBinding = new(
            familyId,
            storeRoot,
            viewId,
            facts.WorkspaceRoot,
            StoreBindingState.Ready);
        StoreWorkspacePointer.Write(facts.WorkspaceRoot, pointerBinding);
        using FamilyStoreReadSession direct = FamilyStoreReadSession.Open(pointerBinding, facts.WorkspaceId);

        StoreFamilyBinding adopted = resolver.ResolveOrCreate(facts);

        Assert.Equal(pointerBinding, adopted);
        Assert.Equal(pointerBinding.FamilyId.ToString("D"), direct.Snapshot.ArtifactOrStoreId);
        Assert.Equal(pointerBinding.ViewId, direct.Snapshot.ViewId);
        Assert.Equal(pointerBinding, ToBinding(registry, "ws-a"));
        Assert.Single(registry.ListStoreFamilies());
    }

    [Fact]
    public void ExistingUsableLineageWinsOverMalformedPointer()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        Guid familyId = Guid.Parse("99999999-9999-4999-8999-999999999999");
        const string viewId = "registered-view";
        string storesRoot = Path.Combine(_directory, "stores");
        string commonDir = Path.GetFullPath("/repo/.git");
        StoreFamilyRegistryRow family = registry.GetOrCreateStoreFamily(
            "git|" + commonDir + "|" + Utc(1).ToString("O"),
            commonDir,
            Utc(1),
            storesRoot,
            () => familyId);
        WriteReadyStore(family.StoreRoot, familyId, viewId, facts.WorkspaceRoot);
        Directory.CreateDirectory(Path.Combine(facts.WorkspaceRoot, ".miller"));
        File.WriteAllText(Path.Combine(facts.WorkspaceRoot, ".miller", "store.json"), "not-json");
        var resolver = Resolver(
            registry,
            new Queue<Guid>([Guid.Parse("11111111-1111-4111-8111-111111111111")]));

        StoreFamilyBinding resolved = resolver.ResolveOrCreate(facts);

        Assert.Equal(familyId, resolved.FamilyId);
        Assert.Equal(viewId, resolved.ViewId);
        Assert.Equal(StoreBindingState.Ready, resolved.State);
        StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(facts.WorkspaceRoot));
        Assert.Equal(familyId, pointer.FamilyId);
        Assert.Equal(viewId, pointer.ViewId);
    }

    [Theory]
    [InlineData("family")]
    [InlineData("view-root")]
    [InlineData("store-root")]
    [InlineData("generation")]
    [InlineData("coordinator")]
    [InlineData("root-replaced")]
    public void InvalidPointerCannotMintOrMutateRegistry(string failure)
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8bbb-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        Guid familyId = Guid.Parse("99999999-9999-4999-8999-999999999999");
        const string viewId = "view-adopted";
        string storeRoot = Path.Combine(_directory, "copied-family");
        string recordedRoot = failure == "view-root"
            ? Path.Combine(_directory, "other-root")
            : facts.WorkspaceRoot;
        WriteReadyStore(storeRoot, familyId, viewId, recordedRoot);
        StoreFamilyBinding pointerBinding = new(
            failure == "family"
                ? Guid.Parse("88888888-8888-4888-8888-888888888888")
                : familyId,
            failure == "store-root" ? Path.Combine(_directory, "missing-family") : storeRoot,
            viewId,
            facts.WorkspaceRoot,
            StoreBindingState.Ready);
        if (failure == "generation")
            File.Delete(Path.Combine(storeRoot, "CURRENT"));
        if (failure == "coordinator")
            File.Delete(Path.Combine(storeRoot, "coord.db"));
        if (failure == "base")
            MarkResolutionExactWithoutBase(storeRoot, viewId);
        StoreWorkspacePointer.Write(facts.WorkspaceRoot, pointerBinding);
        StoreFamilyRegistryRow[] familiesBefore = [.. registry.ListStoreFamilies()];
        StoreMemberRegistryRow? memberBefore = registry.GetStoreMember("ws-a");
        byte[] pointerBefore = File.ReadAllBytes(Path.Combine(facts.WorkspaceRoot, ".miller", "store.json"));
        WorkspaceRootFacts resolutionFacts = failure == "root-replaced"
            ? facts with { RootReplacementObserved = true }
            : facts;

        Assert.Throws<StoreBindingMismatchException>(() => resolver.ResolveOrCreate(resolutionFacts));

        Assert.Equal(familiesBefore, registry.ListStoreFamilies());
        Assert.Equal(memberBefore, registry.GetStoreMember("ws-a"));
        Assert.Equal(pointerBefore, File.ReadAllBytes(Path.Combine(facts.WorkspaceRoot, ".miller", "store.json")));
    }

    [Fact]
    public void RootReplacementDoesNotReuseTheServingCatalogView()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Guid.Parse("44444444-4444-4444-8444-444444444444"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts originalFacts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding original = resolver.ResolveOrCreate(originalFacts);
        WriteStoreCatalog(original.StoreRoot, original.FamilyId, original.ViewId, originalFacts.WorkspaceRoot);

        StoreFamilyBinding replacement = resolver.ResolveOrCreate(
            Facts("ws-a", "root-a", "/other.git", Utc(2), rootReplacementObserved: true));

        Assert.NotEqual(original.FamilyId, replacement.FamilyId);
        Assert.NotEqual(original.ViewId, replacement.ViewId);
        Assert.Equal(StoreBindingState.Planned, replacement.State);
    }

    [Fact]
    public void MismatchedStoreRootRefusesWithoutRegistryMutation()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        WriteStoreCatalog(planned.StoreRoot, planned.FamilyId, planned.ViewId, Path.Combine(_directory, "other"));
        StoreMemberRegistryRow before = Assert.IsType<StoreMemberRegistryRow>(registry.GetStoreMember("ws-a"));

        Assert.Throws<StoreBindingMismatchException>(() => resolver.ResolveOrCreate(facts));

        Assert.Equal(before, registry.GetStoreMember("ws-a"));
    }

    [Fact]
    public void MalformedStoreCatalogRefusesWithoutRegistryMutation()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        Directory.CreateDirectory(planned.StoreRoot);
        File.WriteAllText(Path.Combine(planned.StoreRoot, "CURRENT"), "../escape\n");
        StoreFamilyRegistryRow familyBefore = Assert.IsType<StoreFamilyRegistryRow>(
            registry.GetStoreFamily(planned.FamilyId));
        StoreMemberRegistryRow memberBefore = Assert.IsType<StoreMemberRegistryRow>(
            registry.GetStoreMember("ws-a"));

        Assert.Throws<StoreBindingMismatchException>(() => resolver.ResolveOrCreate(facts));

        Assert.Equal(familyBefore, registry.GetStoreFamily(planned.FamilyId));
        Assert.Equal(memberBefore, registry.GetStoreMember("ws-a"));
    }

    [Fact]
    public void EmptyPlannedStoreDirectoryRemainsRecoverableAfterAFailedFirstImport()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        Directory.CreateDirectory(planned.StoreRoot);

        StoreFamilyBinding recovered = resolver.ResolveOrCreate(facts);

        Assert.Equal(planned.FamilyId, recovered.FamilyId);
        Assert.Equal(StoreBindingState.Planned, recovered.State);
        Assert.Equal(StoreViewReplan.None, planned.Replan);
        // Review finding F4: an absent catalog behind an existing member row always mints a fresh
        // view id, because the completed-scan witness cannot prove the old id never served.
        Assert.NotEqual(planned.ViewId, recovered.ViewId);
        Assert.Equal(StoreViewReplan.NeverPublished, recovered.Replan);
    }

    /// <summary>
    /// Proves: a view the store never published is recoverable even when a SIBLING view in the same family
    /// has published. Before this, the resolver threw "The store has no view for the workspace root." and the
    /// workspace was wedged until someone ran workspace remove plus workspace open.
    /// </summary>
    [Fact]
    public void PlannedViewMissingFromASiblingPublishedCatalogReplansInsteadOfThrowing()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a", "ws-b", "root-b");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts factsA = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        WorkspaceRootFacts factsB = Facts("ws-b", "root-b", "/repo/.git", Utc(1));
        StoreFamilyBinding plannedA = resolver.ResolveOrCreate(factsA);
        StoreFamilyBinding plannedB = resolver.ResolveOrCreate(factsB);
        Assert.Equal(plannedA.FamilyId, plannedB.FamilyId);
        // Only workspace A's view was ever published into the serving catalog.
        WriteStoreCatalog(plannedA.StoreRoot, plannedA.FamilyId, plannedA.ViewId, factsA.WorkspaceRoot);

        StoreFamilyBinding recovered = resolver.ResolveOrCreate(factsB);

        Assert.Equal(StoreBindingState.Planned, recovered.State);
        Assert.Equal(plannedB.ViewId, recovered.ViewId);
        Assert.Equal(plannedB.FamilyId, recovered.FamilyId);
        Assert.Equal(StoreViewReplan.NeverPublished, recovered.Replan);
        Assert.Equal(plannedB.ViewId, registry.GetStoreMember("ws-b")?.ViewId);
        StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(factsB.WorkspaceRoot));
        Assert.Equal(plannedB.ViewId, pointer.ViewId);
        Assert.Equal(plannedB.FamilyId, pointer.FamilyId);
    }

    /// <summary>
    /// Proves: the cross-tree throw survives the recovery change, so one tree is never served under another
    /// tree's view. The message assertion discriminates it from the replan branch's former throw — both raise
    /// the same exception type.
    /// </summary>
    [Fact]
    public void AViewIdKnownToTheCatalogUnderAnotherRootStillRefuses()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        WriteStoreCatalog(planned.StoreRoot, planned.FamilyId, planned.ViewId, Path.Combine(_directory, "other"));
        StoreMemberRegistryRow before = Assert.IsType<StoreMemberRegistryRow>(registry.GetStoreMember("ws-a"));

        StoreBindingMismatchException refused = Assert.Throws<StoreBindingMismatchException>(
            () => resolver.ResolveOrCreate(facts));

        Assert.Equal("The store view root does not match the workspace root.", refused.Message);
        Assert.Equal(before, registry.GetStoreMember("ws-a"));
    }

    /// <summary>
    /// Proves: a genuine corruption — a view that WAS published and then vanished — stays distinguishable from
    /// a view that was never published. Both recover, but only this one is loud and is barred from the stale
    /// legacy seed.
    /// </summary>
    [Fact]
    public void AVanishedViewRecoversButIsRecordedAsVanished()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a", "ws-b", "root-b");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts factsA = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        WorkspaceRootFacts factsB = Facts("ws-b", "root-b", "/repo/.git", Utc(1));
        StoreFamilyBinding plannedA = resolver.ResolveOrCreate(factsA);
        StoreFamilyBinding plannedB = resolver.ResolveOrCreate(factsB);
        WriteStoreCatalog(plannedA.StoreRoot, plannedA.FamilyId, plannedA.ViewId, factsA.WorkspaceRoot);
        // Workspace B completed a scan before, so a catalog that no longer carries its view is a LOSS.
        registry.MarkScanned("ws-b", revision: 7, Utc(2));

        StoreFamilyBinding recovered = resolver.ResolveOrCreate(factsB);

        Assert.Equal(StoreBindingState.Planned, recovered.State);
        Assert.Equal(StoreViewReplan.VanishedFromCatalog, recovered.Replan);
        Assert.Equal(plannedB.ViewId, recovered.ViewId);
    }

    /// <summary>
    /// Proves: the family-identity reconciliation runs BEFORE the view is re-planned, so the recovered member
    /// row and pointer never persist a family id the serving catalog contradicts.
    /// </summary>
    [Fact]
    public void ADivergentCatalogFamilyIsAdoptedBeforeTheViewIsReplanned()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        Guid catalogFamily = Guid.Parse("99999999-9999-4999-8999-999999999999");
        Assert.NotEqual(catalogFamily, planned.FamilyId);
        // The catalog carries neither this view id nor this root, and it names a different family.
        WriteStoreCatalog(
            planned.StoreRoot,
            catalogFamily,
            "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            Path.Combine(_directory, "other"));

        StoreFamilyBinding recovered = resolver.ResolveOrCreate(facts);

        Assert.Equal(StoreBindingState.Planned, recovered.State);
        Assert.Equal(catalogFamily, recovered.FamilyId);
        Assert.Equal(planned.ViewId, recovered.ViewId);
        Assert.Equal(catalogFamily, registry.GetStoreMember("ws-a")?.FamilyId);
        StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(facts.WorkspaceRoot));
        Assert.Equal(catalogFamily, pointer.FamilyId);
        Assert.Equal(planned.ViewId, pointer.ViewId);
    }

    [Fact]
    public void PublishedGenerationWithoutCurrentRefusesInsteadOfReplanningThePointer()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        WriteStoreCatalog(planned.StoreRoot, planned.FamilyId, planned.ViewId, facts.WorkspaceRoot);
        File.Delete(Path.Combine(planned.StoreRoot, "CURRENT"));
        StoreFamilyRegistryRow familyBefore = Assert.IsType<StoreFamilyRegistryRow>(
            registry.GetStoreFamily(planned.FamilyId));
        StoreMemberRegistryRow memberBefore = Assert.IsType<StoreMemberRegistryRow>(
            registry.GetStoreMember("ws-a"));
        StoreWorkspacePointerDocument pointerBefore = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(facts.WorkspaceRoot));

        StoreBindingMismatchException error = Assert.Throws<StoreBindingMismatchException>(
            () => resolver.ResolveOrCreate(facts));

        Assert.Contains("CURRENT", error.Message, StringComparison.Ordinal);
        Assert.Equal(familyBefore, registry.GetStoreFamily(planned.FamilyId));
        Assert.Equal(memberBefore, registry.GetStoreMember("ws-a"));
        Assert.Equal(pointerBefore, StoreWorkspacePointer.Read(facts.WorkspaceRoot));
    }

    [Fact]
    public void PublishedGenerationWithoutCurrentRefusesForAMemberlessWorkspace()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a", "ws-b", "root-b");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts firstFacts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding first = resolver.ResolveOrCreate(firstFacts);
        WriteStoreCatalog(first.StoreRoot, first.FamilyId, first.ViewId, firstFacts.WorkspaceRoot);
        File.Delete(Path.Combine(first.StoreRoot, "CURRENT"));

        WorkspaceRootFacts secondFacts = Facts("ws-b", "root-b", "/repo/.git", Utc(1));
        StoreBindingMismatchException error = Assert.Throws<StoreBindingMismatchException>(
            () => resolver.ResolveOrCreate(secondFacts));

        Assert.Contains("CURRENT", error.Message, StringComparison.Ordinal);
        Assert.Null(registry.GetStoreMember("ws-b"));
        Assert.False(File.Exists(Path.Combine(secondFacts.WorkspaceRoot, ".miller", "store.json")));
    }

    [Fact]
    public void PublishedGenerationWithoutCurrentRefusesWhenUnknownLineageIsPromoted()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts unknownFacts = Facts("ws-a", "root-a", "/repo/.git", null);
        StoreFamilyBinding planned = resolver.ResolveOrCreate(unknownFacts);
        WriteStoreCatalog(planned.StoreRoot, planned.FamilyId, planned.ViewId, unknownFacts.WorkspaceRoot);
        File.Delete(Path.Combine(planned.StoreRoot, "CURRENT"));

        StoreBindingMismatchException error = Assert.Throws<StoreBindingMismatchException>(() =>
            resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", Utc(1))));

        Assert.Contains("CURRENT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServingGenerationSymlinkEscapeRefusesWithoutRegistryMutation()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation or Developer Mode on Windows.");
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);
        string outsideStore = Path.Combine(_directory, "outside-store");
        WriteStoreCatalog(outsideStore, planned.FamilyId, planned.ViewId, facts.WorkspaceRoot);
        Directory.CreateDirectory(planned.StoreRoot);
        File.WriteAllText(Path.Combine(planned.StoreRoot, "CURRENT"), "gen-001\n");
        Directory.CreateSymbolicLink(
            Path.Combine(planned.StoreRoot, "gen-001"),
            Path.Combine(outsideStore, "gen-001"));
        StoreFamilyRegistryRow familyBefore = Assert.IsType<StoreFamilyRegistryRow>(
            registry.GetStoreFamily(planned.FamilyId));
        StoreMemberRegistryRow memberBefore = Assert.IsType<StoreMemberRegistryRow>(
            registry.GetStoreMember("ws-a"));

        Assert.Throws<StoreBindingMismatchException>(() => resolver.ResolveOrCreate(facts));

        Assert.Equal(familyBefore, registry.GetStoreFamily(planned.FamilyId));
        Assert.Equal(memberBefore, registry.GetStoreMember("ws-a"));
    }

    [Fact]
    public void PointerRefusesASymlinkEscapeWithoutWritingOutsideTheWorkspace()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation or Developer Mode on Windows.");
        string root = Path.Combine(_directory, "root");
        string outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, ".miller"), outside);
        root = PathCanonicalizer.CanonicalizeRoot(root);
        var binding = new StoreFamilyBinding(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Path.Combine(_directory, "stores", "11111111-1111-4111-8111-111111111111"),
            "view-a",
            root,
            StoreBindingState.Planned);

        Assert.Throws<StorePointerContainmentException>(() => StoreWorkspacePointer.Write(root, binding));
        Assert.False(File.Exists(Path.Combine(outside, "store.json")));
    }

    [Fact]
    public void ResolverRefusesAnUnsafePointerLocationBeforeRegistryMutation()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic-link creation requires elevation or Developer Mode on Windows.");
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        string root = Path.Combine(_directory, "root-a");
        string outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, ".miller"), outside);
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
        ]);
        var resolver = Resolver(registry, ids);

        Assert.Throws<StorePointerContainmentException>(() =>
            resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", Utc(1))));

        Assert.Empty(registry.ListStoreFamilies());
        Assert.Null(registry.GetStoreMember("ws-a"));
        Assert.False(File.Exists(Path.Combine(outside, "store.json")));
    }

    /// <summary>
    /// Defect D4 (2026-08-21 live validation): the workspace pointer and member row survived, the
    /// family store root was DELETED, and the resolver re-planned the import under the SAME family
    /// id, view id, and gen-001. The recreated store restarts the revision counter, so the reused
    /// (family:view:generation) identity let stored CT rows replay as fresh once the counter caught
    /// up — a false green with zero runs executed. A recreate must mint a NEW view id.
    /// </summary>
    [Fact]
    public void ARecreatedStoreRootMintsANewViewIdForAWorkspaceThatHasScanned()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding original = resolver.ResolveOrCreate(facts);
        WriteStoreCatalog(original.StoreRoot, original.FamilyId, original.ViewId, facts.WorkspaceRoot);
        registry.MarkScanned("ws-a", revision: 55, Utc(2));
        // The whole family store root is destroyed: CURRENT, coord.db, every generation.
        Directory.Delete(original.StoreRoot, recursive: true);

        StoreFamilyBinding recreated = resolver.ResolveOrCreate(facts);

        Assert.Equal(StoreBindingState.Planned, recreated.State);
        Assert.Equal(original.FamilyId, recreated.FamilyId);
        Assert.NotEqual(original.ViewId, recreated.ViewId);
        Assert.Equal(StoreViewReplan.VanishedFromCatalog, recreated.Replan);
        Assert.Equal(recreated.ViewId, registry.GetStoreMember("ws-a")?.ViewId);
        StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(facts.WorkspaceRoot));
        Assert.Equal(recreated.ViewId, pointer.ViewId);
        // The composed CT generation identity can therefore never equal the destroyed store's
        // identity, even though the recreated store restarts at gen-001 and replays revisions.
        Assert.NotEqual(
            WorkspaceReadSnapshotTests.StoreSnapshot(
                familyId: original.FamilyId.ToString("D"),
                viewId: original.ViewId,
                generationName: "gen-001").IndexGenerationIdentity,
            WorkspaceReadSnapshotTests.StoreSnapshot(
                familyId: recreated.FamilyId.ToString("D"),
                viewId: recreated.ViewId,
                generationName: "gen-001").IndexGenerationIdentity);
    }

    /// <summary>
    /// The sibling hole to defect D4: the unknown-lineage promotion branch also reused the member's
    /// view id when the serving catalog was absent. A workspace that completed a scan gets the same
    /// recreate treatment there — a fresh view id, recorded as a loss.
    /// </summary>
    [Fact]
    public void ARecreatedStoreRootMintsANewViewIdEvenWhenUnknownLineageIsPromoted()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts unknownFacts = Facts("ws-a", "root-a", "/repo/.git", null);
        StoreFamilyBinding original = resolver.ResolveOrCreate(unknownFacts);
        WriteStoreCatalog(original.StoreRoot, original.FamilyId, original.ViewId, unknownFacts.WorkspaceRoot);
        registry.MarkScanned("ws-a", revision: 7, Utc(2));
        Directory.Delete(original.StoreRoot, recursive: true);

        StoreFamilyBinding recreated = resolver.ResolveOrCreate(Facts("ws-a", "root-a", "/repo/.git", Utc(1)));

        Assert.Equal(StoreBindingState.Planned, recreated.State);
        Assert.Equal(original.FamilyId, recreated.FamilyId);
        Assert.NotEqual(original.ViewId, recreated.ViewId);
        Assert.Equal(StoreViewReplan.VanishedFromCatalog, recreated.Replan);
    }

    /// <summary>
    /// The crash-window side of defect D4 (review finding F4): the completed-scan witness is written
    /// SEPARATELY from the store publication, so a crash between the two leaves a recreated store
    /// behind a member row with NO witness. The registry cannot prove the recorded view id never
    /// served, so an absent catalog behind an existing member row ALWAYS mints a fresh view id — a
    /// fresh id only makes CT results stale, never falsely fresh. Without the witness the recovery
    /// is classified as a first import, which keeps the legacy-to-store seed.
    /// </summary>
    [Fact]
    public void AnAbsentStoreRootForANeverScannedWorkspaceStillMintsAFreshViewId()
    {
        Directory.CreateDirectory(_directory);
        using WorkspaceRegistry registry = OpenRegistry("ws-a", "root-a");
        var ids = new Queue<Guid>(
        [
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
        ]);
        var resolver = Resolver(registry, ids);
        WorkspaceRootFacts facts = Facts("ws-a", "root-a", "/repo/.git", Utc(1));
        StoreFamilyBinding planned = resolver.ResolveOrCreate(facts);

        StoreFamilyBinding retried = resolver.ResolveOrCreate(facts);

        Assert.NotEqual(planned.ViewId, retried.ViewId);
        Assert.Equal(planned.FamilyId, retried.FamilyId);
        Assert.Equal(StoreViewReplan.NeverPublished, retried.Replan);
        Assert.Equal(StoreBindingState.Planned, retried.State);
        Assert.Equal(retried.ViewId, registry.GetStoreMember("ws-a")?.ViewId);
        StoreWorkspacePointerDocument pointer = Assert.IsType<StoreWorkspacePointerDocument>(
            StoreWorkspacePointer.Read(facts.WorkspaceRoot));
        Assert.Equal(retried.ViewId, pointer.ViewId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private WorkspaceRegistry OpenRegistry(params string[] workspaceAndRootNames)
    {
        var registry = WorkspaceRegistry.Open(Path.Combine(_directory, "workspaces.db"));
        for (int i = 0; i < workspaceAndRootNames.Length; i += 2)
        {
            string workspaceId = workspaceAndRootNames[i];
            string root = Path.Combine(_directory, workspaceAndRootNames[i + 1]);
            Directory.CreateDirectory(root);
            root = PathCanonicalizer.CanonicalizeRoot(root);
            registry.UpsertSeen(
                workspaceId,
                workspaceId,
                root,
                Path.Combine(root, ".miller", "symbols.db"));
        }
        return registry;
    }

    private StoreFamilyResolver Resolver(WorkspaceRegistry registry, Queue<Guid> ids) =>
        new(registry, Path.Combine(_directory, "stores"), () => ids.Dequeue());

    private WorkspaceRootFacts Facts(
        string workspaceId,
        string rootName,
        string? commonDir,
        DateTimeOffset? commonDirCreatedAt,
        bool rootReplacementObserved = false) =>
        new(
            workspaceId,
            PathCanonicalizer.CanonicalizeRoot(Path.Combine(_directory, rootName)),
            commonDir,
            commonDirCreatedAt,
            new WorkspaceRootIdentity(
                commonDir is null ? null : commonDir + "/worktrees/" + workspaceId,
                commonDirCreatedAt),
            rootReplacementObserved);

    private static DateTimeOffset Utc(int minute) =>
        new(2026, 8, 9, 10, minute, 0, TimeSpan.Zero);

    private static void WriteStoreCatalog(
        string storeRoot,
        Guid familyId,
        string viewId,
        string workspaceRoot)
    {
        string generation = Path.Combine(storeRoot, "gen-001");
        Directory.CreateDirectory(generation);
        File.WriteAllText(Path.Combine(storeRoot, "CURRENT"), "gen-001\n");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(generation, "store.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE store_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
            CREATE TABLE views(view_id TEXT PRIMARY KEY, root TEXT NOT NULL) STRICT;
            INSERT INTO store_meta(key, value) VALUES
                ('store_sqlite_schema_version', '2'),
                ('store_format_epoch', '1'),
                ('generation_state', 'serving'),
                ('family_id', $family_id);
            INSERT INTO views(view_id, root) VALUES($view_id, $root);
            """;
        command.Parameters.AddWithValue("$family_id", familyId.ToString("D"));
        command.Parameters.AddWithValue("$view_id", viewId);
        command.Parameters.AddWithValue("$root", workspaceRoot);
        command.ExecuteNonQuery();
    }

    private static StoreFamilyBinding ToBinding(WorkspaceRegistry registry, string workspaceId)
    {
        StoreMemberRegistryRow member = Assert.IsType<StoreMemberRegistryRow>(registry.GetStoreMember(workspaceId));
        StoreFamilyRegistryRow family = Assert.IsType<StoreFamilyRegistryRow>(registry.GetStoreFamily(member.FamilyId));
        return new StoreFamilyBinding(
            member.FamilyId,
            family.StoreRoot,
            member.ViewId,
            member.WorkspaceRoot,
            StoreBindingState.Ready);
    }

    private static void WriteReadyStore(
        string storeRoot,
        Guid familyId,
        string viewId,
        string workspaceRoot)
    {
        string generation = Path.Combine(storeRoot, "gen-001");
        Directory.CreateDirectory(Path.Combine(generation, "bases"));
        File.WriteAllText(Path.Combine(storeRoot, "CURRENT"), "gen-001\n");
        using (var coordinator = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storeRoot, "coord.db"),
            Pooling = false,
        }.ToString()))
        {
            coordinator.Open();
            using SqliteCommand coordinatorCommand = coordinator.CreateCommand();
            coordinatorCommand.CommandText = "CREATE TABLE consumer_cursors (consumer_id TEXT PRIMARY KEY, generation_name TEXT NOT NULL, store_log_sequence INTEGER NOT NULL, updated_at INTEGER NOT NULL) STRICT;";
            coordinatorCommand.ExecuteNonQuery();
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(generation, "store.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand storeCommand = connection.CreateCommand();
        storeCommand.CommandText =
            """
            CREATE TABLE store_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
            INSERT INTO store_meta VALUES
              ('family_id',$family_id),
              ('store_sqlite_schema_version','2'),
              ('store_format_epoch','1'),
              ('min_reader_version','2.31.0'),
              ('binary_version','2.31.0'),
              ('extraction_identity_epoch','1'),
              ('generation_state','serving');
            CREATE TABLE views (
              view_id TEXT PRIMARY KEY,
              root TEXT NOT NULL,
              current_generation INTEGER,
              resolution_state TEXT NOT NULL,
              resolution_base_id TEXT,
              resolution_delta_generation INTEGER,
              resolution_exact_at INTEGER,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL) STRICT;
            CREATE TABLE manifests (
              view_id TEXT NOT NULL,
              generation INTEGER NOT NULL,
              manifest_hash TEXT NOT NULL,
              request_id TEXT NOT NULL,
              created_at TEXT NOT NULL,
              PRIMARY KEY(view_id,generation)) STRICT;
            CREATE TABLE file_versions (
              version_id INTEGER PRIMARY KEY,
              path TEXT NOT NULL,
              content_hash TEXT NOT NULL,
              extraction_epoch INTEGER NOT NULL,
              language TEXT NOT NULL,
              content_bytes INTEGER NOT NULL,
              line_count INTEGER,
              metadata_json TEXT,
              complete_l1 INTEGER,
              complete_l2 INTEGER,
              complete_l3 INTEGER) STRICT;
            CREATE TABLE manifest_entries (
              view_id TEXT NOT NULL,
              generation INTEGER NOT NULL,
              path TEXT NOT NULL,
              language TEXT NOT NULL,
              version_id INTEGER,
              status TEXT NOT NULL,
              observed_content_hash TEXT,
              indexed_at TEXT NOT NULL,
              error_class TEXT,
              error_json TEXT,
              PRIMARY KEY(view_id,generation,path)) STRICT;
            CREATE TABLE symbols (
              version_id INTEGER NOT NULL,
              symbol_id TEXT NOT NULL,
              path TEXT NOT NULL,
              language TEXT NOT NULL,
              name TEXT NOT NULL,
              kind TEXT NOT NULL,
              signature TEXT,
              doc_comment TEXT,
              visibility TEXT,
              parent_symbol_id TEXT,
              start_line INTEGER NOT NULL,
              start_column INTEGER NOT NULL,
              end_line INTEGER NOT NULL,
              end_column INTEGER NOT NULL,
              start_byte INTEGER NOT NULL,
              end_byte INTEGER NOT NULL,
              body_start_line INTEGER,
              body_start_column INTEGER,
              body_end_line INTEGER,
              body_end_column INTEGER,
              body_start_byte INTEGER,
              body_end_byte INTEGER,
              body_hash TEXT,
              semantic_group TEXT,
              confidence REAL,
              content_type TEXT,
              is_test INTEGER NOT NULL,
              test_container INTEGER NOT NULL,
              test_lifecycle INTEGER NOT NULL,
              metadata_json TEXT,
              PRIMARY KEY(version_id,symbol_id)) STRICT;
            CREATE TABLE store_log (
              sequence INTEGER PRIMARY KEY,
              request_id TEXT NOT NULL,
              event_kind TEXT NOT NULL,
              view_id TEXT,
              generation INTEGER,
              version_id INTEGER,
              level INTEGER,
              terminal INTEGER NOT NULL,
              payload_json TEXT NOT NULL,
              created_at TEXT NOT NULL) STRICT;
            INSERT INTO views VALUES ($view_id,$workspace_root,1,'unbound',NULL,NULL,NULL,'2026-08-09T00:00:00Z','2026-08-09T00:00:00Z');
            INSERT INTO manifests VALUES ($view_id,1,'manifest-current','request-a','2026-08-09T00:00:00Z');
            INSERT INTO file_versions VALUES (1,'same.cs','blake3:visible',1,'csharp',11,1,NULL,1,2,3);
            INSERT INTO manifest_entries VALUES ($view_id,1,'same.cs','csharp',1,'indexed','blake3:visible','2026-08-09T00:00:00Z',NULL,NULL);
            INSERT INTO store_log VALUES (1,'request-a','manifest_flipped',$view_id,1,NULL,NULL,1,'{}','2026-08-09T00:00:01Z');
            """;
        storeCommand.Parameters.AddWithValue("$family_id", familyId.ToString("D"));
        storeCommand.Parameters.AddWithValue("$view_id", viewId);
        storeCommand.Parameters.AddWithValue("$workspace_root", workspaceRoot);
        storeCommand.ExecuteNonQuery();
    }

    private static void MarkResolutionExactWithoutBase(string storeRoot, string viewId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storeRoot, "gen-001", "store.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE views SET resolution_state='exact', resolution_base_id='base-a', resolution_delta_generation=1, resolution_exact_at=1 WHERE view_id=$view_id;";
        command.Parameters.AddWithValue("$view_id", viewId);
        command.ExecuteNonQuery();
    }
}
