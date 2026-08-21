using Microsoft.Data.Sqlite;
using Miller.Indexing;
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
}
