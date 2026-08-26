using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Store;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the family-store sidecar reclaim on <see cref="WorkspaceRegistryPrune"/>: a pruned row's per-view
/// sidecars go with it, a surviving member's do not, a dry run deletes nothing, and an absent store root or a
/// busy sidecar lease degrades without failing the prune. Temp registry and fabricated sidecar files only.
/// </summary>
public sealed class WorkspaceRegistryPruneTests : IDisposable
{
    private readonly string _dir;
    private readonly WorkspaceRegistry _registry;

    public WorkspaceRegistryPruneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-prune-reclaim-" + Guid.NewGuid().ToString("N"));
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

    private string Register(string workspaceId, string display, bool rootExists)
    {
        string root = Path.Combine(_dir, display);
        if (rootExists)
            Directory.CreateDirectory(root);
        _registry.UpsertSeen(
            workspaceId, display, root, Path.Combine(root, ".miller", "symbols.db"),
            WorkspaceRegistryState.Ready);
        return root;
    }

    private void JoinFamily(StoreFamilyRegistryRow family, string workspaceId, string root, string viewId) =>
        _registry.UpsertStoreMember(workspaceId, family.FamilyId, viewId, root, WorkspaceRootIdentity.Unknown);

    private static IReadOnlyList<string> WriteSidecars(string storeRoot, string viewId)
    {
        var paths = new List<string>();
        foreach (StoreSidecarKind kind in Enum.GetValues<StoreSidecarKind>())
        {
            string path = StoreSidecarCatalog.PathFor(storeRoot, kind, viewId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[256]);
            paths.Add(path);
        }

        return paths;
    }

    [Fact]
    public void Run_RecordsTheOwedReclaimBeforeTheRegistryRowIsDeleted()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-intent");
        string goneRoot = Register("ws-prune-intent-1", "intent-repo", rootExists: false);
        JoinFamily(family, "ws-prune-intent-1", goneRoot, "view-prune-intent");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-prune-intent");
        string sidecarDir = StoreSidecarCatalog.DirectoryFor(family.StoreRoot);

        // Only the FIRST lease request is the reclaim of this row; the prune asks again for its trailing
        // discharge sweep, by which time the busy-lease path has written a record of its own.
        int leaseRequests = 0;
        bool recordedAtLeaseTime = false;
        bool rowGoneAtLeaseTime = false;
        WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            acquireSidecarLease: _ =>
            {
                if (++leaseRequests == 1)
                {
                    recordedAtLeaseTime = Directory
                        .GetFiles(sidecarDir, "*" + StoreSidecarReclaim.OwedRecordSuffix).Length == 1;
                    rowGoneAtLeaseTime = _registry.Get("ws-prune-intent-1") is null;
                }

                return null;
            });

        Assert.True(rowGoneAtLeaseTime, "the registry row must already be gone when the reclaim runs");
        Assert.True(recordedAtLeaseTime, "the owed record must be written before the registry row is deleted");
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Run_PrunedRow_ReclaimsItsViewAndSparesTheSurvivingMember()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune");
        string goneRoot = Register("ws-prune-gone-0001", "gone-repo", rootExists: false);
        string liveRoot = Register("ws-prune-live-0001", "live-repo", rootExists: true);
        JoinFamily(family, "ws-prune-gone-0001", goneRoot, "view-gone");
        JoinFamily(family, "ws-prune-live-0001", liveRoot, "view-live");
        IReadOnlyList<string> reclaimed = WriteSidecars(family.StoreRoot, "view-gone");
        IReadOnlyList<string> keep = WriteSidecars(family.StoreRoot, "view-live");

        WorkspaceRegistryPrune.Result result =
            WorkspaceRegistryPrune.Run(_registry, protectedWorkspaceId: null, dryRun: false);

        Assert.Single(result.Pruned);
        Assert.Equal(1, result.Kept);
        Assert.Equal(3, result.SidecarReclaim.FilesDeleted);
        Assert.Equal(3 * 256, result.SidecarReclaim.BytesReclaimed);
        Assert.All(reclaimed, p => Assert.False(File.Exists(p)));
        Assert.All(keep, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Run_DryRun_DeletesNoSidecars()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-dry");
        string goneRoot = Register("ws-prune-dry-00001", "dry-repo", rootExists: false);
        JoinFamily(family, "ws-prune-dry-00001", goneRoot, "view-dry");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-dry");

        WorkspaceRegistryPrune.Result result =
            WorkspaceRegistryPrune.Run(_registry, protectedWorkspaceId: null, dryRun: true);

        Assert.Single(result.Pruned);
        Assert.False(result.SidecarReclaim.HasReport);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.NotNull(_registry.GetStoreMember("ws-prune-dry-00001"));
    }

    [Fact]
    public void Run_WithoutAMaintainer_SpawnsNothingAndReportsNoMaintenance()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-no-maintain");
        string goneRoot = Register("ws-prune-nom-00001", "no-maintain-repo", rootExists: false);
        JoinFamily(family, "ws-prune-nom-00001", goneRoot, "view-no-maintain");

        WorkspaceRegistryPrune.Result result =
            WorkspaceRegistryPrune.Run(_registry, protectedWorkspaceId: null, dryRun: false);

        Assert.Single(result.Pruned);
        Assert.False(result.StoreMaintenance.HasReport);
    }

    [Fact]
    public void Run_MaintainsEveryRegisteredFamilyAndSumsThePrunedRows()
    {
        StoreFamilyRegistryRow first = SeedFamily("lineage-maintain-one");
        StoreFamilyRegistryRow second = SeedFamily("lineage-maintain-two");
        string goneRoot = Register("ws-prune-mnt-00001", "maintain-repo", rootExists: false);
        JoinFamily(first, "ws-prune-mnt-00001", goneRoot, "view-maintain");
        var visited = new List<string>();

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            maintainStore: storeRoot =>
            {
                visited.Add(storeRoot);
                return new StoreMaintenanceOutcome(7, null);
            });

        Assert.Equal(
            new[] { first.StoreRoot, second.StoreRoot }.Order(StringComparer.Ordinal),
            visited.Order(StringComparer.Ordinal));
        Assert.Equal(14, result.StoreMaintenance.PrunedRequestRows);
        Assert.Null(result.StoreMaintenance.Error);
    }

    [Fact]
    public void Run_AFailedMaintenanceIsReportedAndNeverFailsThePrune()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-maintain-fails");
        string goneRoot = Register("ws-prune-mff-00001", "maintain-fails-repo", rootExists: false);
        JoinFamily(family, "ws-prune-mff-00001", goneRoot, "view-maintain-fails");

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            maintainStore: _ => new StoreMaintenanceOutcome(0, "store maintenance timed out"));

        Assert.Single(result.Pruned);
        Assert.Null(_registry.GetStoreMember("ws-prune-mff-00001"));
        Assert.Equal("store maintenance timed out", result.StoreMaintenance.Error);
    }

    [Fact]
    public void Run_DryRun_RunsNoMaintenance()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-maintain-dry");
        string goneRoot = Register("ws-prune-mdr-00001", "maintain-dry-repo", rootExists: false);
        JoinFamily(family, "ws-prune-mdr-00001", goneRoot, "view-maintain-dry");

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: true,
            maintainStore: _ => throw new InvalidOperationException("a dry run must never maintain"));

        Assert.Single(result.Pruned);
        Assert.False(result.StoreMaintenance.HasReport);
    }

    [Fact]
    public void Run_MissingStoreRoot_PrunesAndReportsNothing()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-absent", createStoreRoot: false);
        string goneRoot = Register("ws-prune-abs-00001", "absent-repo", rootExists: false);
        JoinFamily(family, "ws-prune-abs-00001", goneRoot, "view-absent");

        WorkspaceRegistryPrune.Result result =
            WorkspaceRegistryPrune.Run(_registry, protectedWorkspaceId: null, dryRun: false);

        Assert.Single(result.Pruned);
        Assert.False(result.SidecarReclaim.HasReport);
        Assert.False(Directory.Exists(family.StoreRoot));
        Assert.Null(_registry.Get("ws-prune-abs-00001"));
    }

    [Fact]
    public void Run_SidecarLeaseUnavailable_PrunesAnywayAndReportsTheSkip()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-busy");
        string goneRoot = Register("ws-prune-busy-0001", "busy-repo", rootExists: false);
        JoinFamily(family, "ws-prune-busy-0001", goneRoot, "view-busy");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-busy");

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: null, dryRun: false, acquireSidecarLease: _ => null);

        Assert.Single(result.Pruned);
        Assert.Null(_registry.Get("ws-prune-busy-0001"));
        Assert.Equal(StoreSidecarReclaim.LeaseBusyReason, result.SidecarReclaim.SkipReason);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Run_NonStoreWorkspace_ReportsNoReclaim()
    {
        Register("ws-prune-plain-0001", "plain-repo", rootExists: false);

        WorkspaceRegistryPrune.Result result =
            WorkspaceRegistryPrune.Run(_registry, protectedWorkspaceId: null, dryRun: false);

        Assert.Single(result.Pruned);
        Assert.False(result.SidecarReclaim.HasReport);
    }

    [Fact]
    public void Run_ProtectedRow_IsNeverReclaimed()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-protected");
        string goneRoot = Register("ws-prune-prot-0001", "protected-repo", rootExists: false);
        JoinFamily(family, "ws-prune-prot-0001", goneRoot, "view-protected");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-protected");

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: "ws-prune-prot-0001", dryRun: false);

        Assert.Empty(result.Pruned);
        Assert.Equal(1, result.Kept);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Run_DischargesAReclaimAnEarlierBusyLeaseOwed()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-owed");
        string goneRoot = Register("ws-prune-owed-0001", "owed-repo", rootExists: false);
        JoinFamily(family, "ws-prune-owed-0001", goneRoot, "view-prune-owed");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-prune-owed");
        WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: null, dryRun: false, acquireSidecarLease: _ => null);
        Assert.All(paths, p => Assert.True(File.Exists(p)));

        WorkspaceRegistryPrune.Result result =
            WorkspaceRegistryPrune.Run(_registry, protectedWorkspaceId: null, dryRun: false);

        Assert.Empty(result.Pruned);
        Assert.Equal(3, result.SidecarReclaim.FilesDeleted);
        Assert.All(paths, p => Assert.False(File.Exists(p)));
    }

    [Fact]
    public void Run_DryRun_DischargesNothing()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-owed-dry");
        string goneRoot = Register("ws-prune-owdry-01", "owed-dry-repo", rootExists: false);
        JoinFamily(family, "ws-prune-owdry-01", goneRoot, "view-prune-owed-dry");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-prune-owed-dry");
        WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: null, dryRun: false, acquireSidecarLease: _ => null);

        WorkspaceRegistryPrune.Result result =
            WorkspaceRegistryPrune.Run(_registry, protectedWorkspaceId: null, dryRun: true);

        Assert.False(result.SidecarReclaim.HasReport);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }
}
