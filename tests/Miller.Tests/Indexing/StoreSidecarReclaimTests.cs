using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins <see cref="StoreSidecarReclaim"/>: the Miller-owned per-view sidecar files of a workspace that has left
/// the family store are deleted, another member's view is never touched, and a missing store root, a missing
/// file, or a busy lease degrades instead of throwing. Temp dirs and fabricated sidecar files only — no
/// julie-extract, no real store.
/// </summary>
public sealed class StoreSidecarReclaimTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkspaceRegistry _registry;

    public StoreSidecarReclaimTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-sidecar-reclaim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registry = WorkspaceRegistry.Open(Path.Combine(_dir, "workspaces.db"));
    }

    public void Dispose()
    {
        _registry.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private StoreFamilyRegistryRow SeedFamily(string lineage, bool createStoreRoot = true)
    {
        StoreFamilyRegistryRow family = _registry.GetOrCreateStoreFamily(
            lineage, canonicalCommonDir: null, commonDirCreatedAtUtc: null,
            storesRoot: Path.Combine(_dir, "stores"));
        if (createStoreRoot)
            Directory.CreateDirectory(Path.Combine(family.StoreRoot, "sidecars"));
        return family;
    }

    private StoreSidecarReclaimTarget SeedMember(StoreFamilyRegistryRow family, string workspaceId, string viewId)
    {
        string root = Path.Combine(_dir, workspaceId);
        Directory.CreateDirectory(root);
        _registry.UpsertSeen(
            workspaceId, workspaceId, root, Path.Combine(root, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);
        _registry.UpsertStoreMember(workspaceId, family.FamilyId, viewId, root, WorkspaceRootIdentity.Unknown);
        return StoreSidecarReclaimTarget.Capture(_registry, workspaceId)!;
    }

    private static IReadOnlyList<string> WriteSidecars(string storeRoot, string viewId, int bytesEach)
    {
        var paths = new List<string>();
        foreach (StoreSidecarKind kind in Enum.GetValues<StoreSidecarKind>())
        {
            string path = StoreSidecarCatalog.PathFor(storeRoot, kind, viewId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytesEach]);
            File.WriteAllBytes(path + "-wal", new byte[bytesEach]);
            paths.Add(path);
            paths.Add(path + "-wal");
        }

        return paths;
    }

    [Fact]
    public void Capture_ReadsViewFromTheMemberRow()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-capture");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-capture-0001", "view-capture");

        Assert.Equal("view-capture", target.ViewId);
        Assert.Equal(family.FamilyId, target.FamilyId);
        Assert.Equal(family.StoreRoot, target.StoreRoot);
    }

    [Fact]
    public void Capture_NonMemberWorkspace_ReturnsNull() =>
        Assert.Null(StoreSidecarReclaimTarget.Capture(_registry, "ws-not-a-member-01"));

    [Fact]
    public void Reclaim_DeletesEveryKindAndSibling_ForTheRemovedView()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-delete");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-delete-0001", "view-delete");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-delete", bytesEach: 64);
        _registry.Remove("ws-delete-0001");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.Equal(6, result.FilesDeleted);
        Assert.Equal(6 * 64, result.BytesReclaimed);
        Assert.Equal(0, result.FilesRetained);
        Assert.Null(result.SkipReason);
        Assert.All(paths, p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void Reclaim_LeavesAnotherViewsSidecarsAlone()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-neighbour");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-going-0001", "view-going");
        SeedMember(family, "ws-staying-001", "view-staying");
        WriteSidecars(family.StoreRoot, "view-going", bytesEach: 32);
        IReadOnlyList<string> keep = WriteSidecars(family.StoreRoot, "view-staying", bytesEach: 32);
        _registry.Remove("ws-going-0001");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.Equal(6, result.FilesDeleted);
        Assert.All(keep, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Reclaim_ViewStillClaimedByALiveMember_DeletesNothing()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-live");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-live-00001", "view-live");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-live", bytesEach: 16);

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(StoreSidecarReclaim.StillAMemberReason, result.SkipReason);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Reclaim_MissingStoreRoot_ReportsNothingAndDoesNotCreateIt()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-absent", createStoreRoot: false);
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-absent-0001", "view-absent");
        _registry.Remove("ws-absent-0001");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.Equal(StoreSidecarReclaimResult.None, result);
        Assert.False(result.HasReport);
        Assert.False(Directory.Exists(family.StoreRoot));
    }

    [Fact]
    public void Reclaim_MissingSidecarFiles_ReportsNothing()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-empty");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-empty-0001", "view-empty");
        _registry.Remove("ws-empty-0001");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.Equal(0, result.FilesDeleted);
        Assert.Null(result.SkipReason);
    }

    [Fact]
    public void Reclaim_LeaseUnavailable_SkipsAndKeepsTheFiles()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-busy");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-busy-00001", "view-busy");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-busy", bytesEach: 8);
        _registry.Remove("ws-busy-00001");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target, _ => null);

        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(StoreSidecarReclaim.LeaseBusyReason, result.SkipReason);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Reclaim_HoldsTheRealFamilyLease()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-real-lease");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-lease-0001", "view-lease");
        WriteSidecars(family.StoreRoot, "view-lease", bytesEach: 4);
        _registry.Remove("ws-lease-0001");

        using (FamilyStoreSidecarWriteLease held =
            FamilyStoreSidecarWriteLease.AcquireFor(family.StoreRoot, TimeSpan.FromSeconds(5)))
        {
            StoreSidecarReclaimResult blocked = StoreSidecarReclaim.Reclaim(_registry, target);
            Assert.Equal(StoreSidecarReclaim.LeaseBusyReason, blocked.SkipReason);
        }

        StoreSidecarReclaimResult freed = StoreSidecarReclaim.Reclaim(_registry, target);
        Assert.Equal(6, freed.FilesDeleted);
    }

    [Fact]
    public void Reclaim_FileHeldOpen_KeepsGoingAndReportsRetention()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-held");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-held-00001", "view-held");
        WriteSidecars(family.StoreRoot, "view-held", bytesEach: 8);
        _registry.Remove("ws-held-00001");

        string pinned = StoreSidecarCatalog.PathFor(family.StoreRoot, StoreSidecarKind.Search, "view-held");
        using var handle = new FileStream(pinned, FileMode.Open, FileAccess.Read, FileShare.None);

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.Equal(6, result.FilesDeleted + result.FilesRetained);
        if (!OperatingSystem.IsWindows())
            return; // POSIX unlinks a file that a reader still holds open, so nothing is retained there.
        Assert.Equal(1, result.FilesRetained);
        Assert.Equal(StoreSidecarReclaim.FilesInUseReason, result.SkipReason);
    }

    [Fact]
    public void Reclaim_NullTarget_IsANoOp() =>
        Assert.Equal(StoreSidecarReclaimResult.None, StoreSidecarReclaim.Reclaim(_registry, target: null));

    private static IReadOnlyList<string> WriteVectorGenerations(string storeRoot, string viewId, int bytesEach)
    {
        string active = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Vector, viewId);
        string prefix = Path.Combine(
            Path.GetDirectoryName(active)!, Path.GetFileNameWithoutExtension(active));
        string[] paths =
        [
            active + ".rebuild",
            active + ".rebuild-wal",
            prefix + ".gen-aaaa.db",
            prefix + ".gen-bbbb.db",
        ];
        foreach (string path in paths)
            File.WriteAllBytes(path, new byte[bytesEach]);
        return paths;
    }

    [Fact]
    public void Reclaim_DeletesTheVectorShadowAndEveryRetainedGeneration()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-generations");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-gener-0001", "view-gener");
        WriteSidecars(family.StoreRoot, "view-gener", bytesEach: 10);
        IReadOnlyList<string> generations = WriteVectorGenerations(family.StoreRoot, "view-gener", bytesEach: 100);
        _registry.Remove("ws-gener-0001");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.All(generations, p => Assert.False(File.Exists(p)));
        Assert.Equal(10, result.FilesDeleted);
        Assert.Equal((6 * 10) + (4 * 100), result.BytesReclaimed);
        Assert.Null(result.SkipReason);
    }

    [Fact]
    public void Reclaim_LeavesAnotherViewsRetainedGenerationsAlone()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-generations-neighbour");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-genergo-001", "view-gener-going");
        SeedMember(family, "ws-generstay-01", "view-gener-staying");
        WriteVectorGenerations(family.StoreRoot, "view-gener-going", bytesEach: 8);
        IReadOnlyList<string> keep =
            WriteVectorGenerations(family.StoreRoot, "view-gener-staying", bytesEach: 8);
        _registry.Remove("ws-genergo-001");

        StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.All(keep, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Reclaim_DeletesTheFreshnessStampAndThePreservationMarker()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-siblings");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-sibling-001", "view-sibling");
        WriteSidecars(family.StoreRoot, "view-sibling", bytesEach: 4);
        string stamp = StoreFreshnessStamp.FilePath(family.StoreRoot, "view-sibling");
        string marker = StoreSidecarCatalog.PathFor(family.StoreRoot, StoreSidecarKind.Content, "view-sibling")
            + ContentCorpusWriter.PreservationFailureSuffix;
        File.WriteAllText(stamp, "{}");
        File.WriteAllText(marker, "{}");
        _registry.Remove("ws-sibling-001");

        StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.False(File.Exists(stamp));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Reclaim_LeaseBusy_OwesTheReclaimSoALaterPassFinishesIt()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-owed");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-owed-00001", "view-owed");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-owed", bytesEach: 12);
        _registry.Remove("ws-owed-00001");

        StoreSidecarReclaimResult blocked = StoreSidecarReclaim.Reclaim(_registry, target, _ => null);
        Assert.Equal(StoreSidecarReclaim.LeaseBusyReason, blocked.SkipReason);
        Assert.All(paths, p => Assert.True(File.Exists(p)));

        StoreSidecarReclaimResult discharged =
            StoreSidecarReclaim.DischargeOwed(_registry, family.StoreRoot);

        Assert.Equal(6, discharged.FilesDeleted);
        Assert.All(paths, p => Assert.False(File.Exists(p)));
        Assert.Empty(Directory.GetFiles(
            StoreSidecarCatalog.DirectoryFor(family.StoreRoot), "*.reclaim-owed"));
    }

    [Fact]
    public void Reclaim_ClearsTheOwedRecordOnceTheFilesAreGone()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-owed-clear");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-owedcl-0001", "view-owed-clear");
        WriteSidecars(family.StoreRoot, "view-owed-clear", bytesEach: 2);
        _registry.Remove("ws-owedcl-0001");
        StoreSidecarReclaim.Reclaim(_registry, target, _ => null);
        Assert.Single(Directory.GetFiles(
            StoreSidecarCatalog.DirectoryFor(family.StoreRoot), "*.reclaim-owed"));

        StoreSidecarReclaim.Reclaim(_registry, target);

        Assert.Empty(Directory.GetFiles(
            StoreSidecarCatalog.DirectoryFor(family.StoreRoot), "*.reclaim-owed"));
    }

    [Fact]
    public void DischargeOwed_ViewClaimedAgain_KeepsTheFilesAndDropsTheRecord()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-owed-reclaimed");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-owedre-0001", "view-owed-back");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-owed-back", bytesEach: 3);
        _registry.Remove("ws-owedre-0001");
        StoreSidecarReclaim.Reclaim(_registry, target, _ => null);
        Assert.Single(Directory.GetFiles(
            StoreSidecarCatalog.DirectoryFor(family.StoreRoot), "*.reclaim-owed"));

        SeedMember(family, "ws-owedre-0002", "view-owed-back");
        StoreSidecarReclaimResult result = StoreSidecarReclaim.DischargeOwed(_registry, family.StoreRoot);

        Assert.Equal(0, result.FilesDeleted);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.Empty(Directory.GetFiles(
            StoreSidecarCatalog.DirectoryFor(family.StoreRoot), "*.reclaim-owed"));
    }

    [Fact]
    public void DischargeOwed_RecordWhoseContentDoesNotHashToItsName_DeletesNothingButTheRecord()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-owed-planted");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-planted", bytesEach: 5);
        string planted = Path.Combine(
            StoreSidecarCatalog.DirectoryFor(family.StoreRoot), "not-a-view-key.reclaim-owed");
        File.WriteAllText(planted, "view-planted");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.DischargeOwed(_registry, family.StoreRoot);

        Assert.Equal(0, result.FilesDeleted);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.False(File.Exists(planted));
    }

    [Fact]
    public void DischargeOwed_NothingOwed_TakesNoLeaseAndReportsNothing()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-owed-empty");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.DischargeOwed(
            _registry,
            family.StoreRoot,
            _ => throw new InvalidOperationException("An empty store must not take the sidecar lease."));

        Assert.Equal(StoreSidecarReclaimResult.None, result);
    }

    [Fact]
    public void Reclaim_MissingStoreRoot_LeaseBusyPathCreatesNothing()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-absent-owed", createStoreRoot: false);
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-absown-0001", "view-absent-owed");
        _registry.Remove("ws-absown-0001");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target, _ => null);

        Assert.Equal(StoreSidecarReclaimResult.None, result);
        Assert.False(Directory.Exists(family.StoreRoot));
    }
}
