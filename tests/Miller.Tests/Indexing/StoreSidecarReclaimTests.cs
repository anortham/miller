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

    private static StoreSidecarCursorKey CursorKey(StoreSidecarReclaimTarget target)
    {
        string familyId = target.FamilyId.ToString();
        const string storeInstanceId = "store-instance-a";
        const string generationName = "gen-001";
        string consumerId = StoreSidecarCursorIdentity.CursorId(
            familyId,
            storeInstanceId,
            target.ViewId,
            StoreSidecarKind.Content,
            generationName);
        return new(
            familyId,
            storeInstanceId,
            target.ViewId,
            StoreSidecarKind.Content,
            generationName,
            consumerId);
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
    public void Reclaim_Releases_exact_journal_id_before_deleting_sidecars()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-cursor-release");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-cursor-0001", "view-cursor");
        IReadOnlyList<string> sidecars = WriteSidecars(family.StoreRoot, target.ViewId, bytesEach: 8);
        StoreSidecarCursorKey key = CursorKey(target);
        var journal = new StoreSidecarCursorJournal(family.StoreRoot, key.FamilyId, key.ViewId);
        journal.UpsertDesired(key, 9);
        _registry.Remove("ws-cursor-0001");
        var released = new List<string>();

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(
            _registry,
            target,
            static _ => new FakeLease(),
            listFiles: null,
            (_, familyId, cursor) =>
            {
                Assert.Equal(key.FamilyId, familyId);
                released.Add(cursor.ConsumerId);
                return new(true, true, cursor.GenerationName, cursor.ConsumerId, null, null);
            });

        Assert.Null(result.SkipReason);
        Assert.Equal([key.ConsumerId], released);
        Assert.False(journal.Exists);
        Assert.All(sidecars, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public void Reclaim_Release_failure_retains_journal_and_sidecars_for_retry()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-cursor-owed");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-cursor-0002", "view-cursor-owed");
        IReadOnlyList<string> sidecars = WriteSidecars(family.StoreRoot, target.ViewId, bytesEach: 8);
        StoreSidecarCursorKey key = CursorKey(target);
        var journal = new StoreSidecarCursorJournal(family.StoreRoot, key.FamilyId, key.ViewId);
        journal.UpsertDesired(key, 9);
        _registry.Remove("ws-cursor-0002");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(
            _registry,
            target,
            static _ => new FakeLease(),
            listFiles: null,
            (_, _, cursor) => new(false, false, cursor.GenerationName, cursor.ConsumerId, null, "busy"));

        Assert.Equal(StoreSidecarReclaim.CursorReleaseFailedReason, result.SkipReason);
        Assert.True(journal.Exists);
        Assert.All(sidecars, path => Assert.True(File.Exists(path)));
        Assert.Single(OwedRecords(family.StoreRoot));
    }

    [Fact]
    public void Reclaim_Survivor_guard_prevents_cursor_release()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-cursor-survivor");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-cursor-0003", "view-cursor-live");
        StoreSidecarCursorKey key = CursorKey(target);
        new StoreSidecarCursorJournal(family.StoreRoot, key.FamilyId, key.ViewId).UpsertDesired(key, 9);
        int calls = 0;

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(
            _registry,
            target,
            static _ => new FakeLease(),
            listFiles: null,
            (_, _, cursor) =>
            {
                calls++;
                return new(true, true, cursor.GenerationName, cursor.ConsumerId, null, null);
            });

        Assert.Equal(StoreSidecarReclaim.StillAMemberReason, result.SkipReason);
        Assert.Equal(0, calls);
        Assert.True(File.Exists(StoreSidecarCursorJournal.PathFor(family.StoreRoot, target.ViewId)));
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
    public void DischargeOwed_ViewClaimedAgain_KeepsTheFilesAndTheDurableIntent()
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
        Assert.Single(Directory.GetFiles(
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

    // ---------- the reclaim intent survives a crash (F1) ----------

    private static string[] OwedRecords(string storeRoot) =>
        Directory.GetFiles(
            StoreSidecarCatalog.DirectoryFor(storeRoot), "*" + StoreSidecarReclaim.OwedRecordSuffix);

    [Fact]
    public void RecordIntent_SurvivesACrashBetweenTheRegistryDeleteAndTheReclaim()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-intent-crash");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-crash-00001", "view-crash");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-crash", bytesEach: 9);

        Assert.True(StoreSidecarReclaim.RecordIntent(target));
        _registry.Remove("ws-crash-00001");

        // The process dies HERE: the member row is gone, the reclaim never ran, and the captured target died
        // with the process. Only the record on disk still says which view these files belong to.
        Assert.Single(OwedRecords(family.StoreRoot));

        StoreSidecarReclaimResult recovered = StoreSidecarReclaim.DischargeOwed(_registry, family.StoreRoot);

        Assert.Equal(6, recovered.FilesDeleted);
        Assert.All(paths, p => Assert.False(File.Exists(p)));
        Assert.Empty(OwedRecords(family.StoreRoot));
    }

    [Fact]
    public void RecordIntent_NullTarget_IsANoOp() => Assert.True(StoreSidecarReclaim.RecordIntent(null));

    [Fact]
    public void RecordIntent_MissingStoreRoot_CreatesNothing()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-intent-absent", createStoreRoot: false);
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-intabs-0001", "view-intent-absent");

        Assert.True(StoreSidecarReclaim.RecordIntent(target));

        Assert.False(Directory.Exists(family.StoreRoot));
    }

    [Fact]
    public void Reclaim_OwedRecordCannotBeWritten_SaysSoInTheSkipReason()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-owed-unwritable");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-owedun-0001", "view-owed-unwritable");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-owed-unwritable", bytesEach: 2);

        // A directory sitting where the record belongs fails every write, the way a denying ACL would.
        Directory.CreateDirectory(Path.Combine(
            StoreSidecarCatalog.DirectoryFor(family.StoreRoot),
            StoreSidecarCatalog.ViewKey("view-owed-unwritable") + StoreSidecarReclaim.OwedRecordSuffix));

        Assert.False(StoreSidecarReclaim.RecordIntent(target));
        _registry.Remove("ws-owedun-0001");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target, _ => null);

        Assert.Equal(
            StoreSidecarReclaim.LeaseBusyReason + StoreSidecarReclaim.NotRecordedSuffix,
            result.SkipReason);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    // ---------- membership is re-checked under the lease (F2) ----------

    private sealed class FakeLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [Fact]
    public void Reclaim_ViewClaimedAgainWhileWaitingForTheLease_KeepsTheFiles()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-lease-race");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-race-000001", "view-race");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-race", bytesEach: 16);
        Assert.True(StoreSidecarReclaim.RecordIntent(target));
        _registry.Remove("ws-race-000001");

        // The lease wait lasts up to two seconds, and `workspace open` on the same root lands inside it: the
        // family resolver reconciles the re-registered workspace onto the SAME view id from the store catalog.
        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(_registry, target, _ =>
        {
            SeedMember(family, "ws-race-000002", "view-race");
            return new FakeLease();
        });

        Assert.Equal(StoreSidecarReclaim.StillAMemberReason, result.SkipReason);
        Assert.Equal(0, result.FilesDeleted);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.Single(OwedRecords(family.StoreRoot));
    }

    // ---------- the owed record is scoped to its own store (F3) ----------

    [Fact]
    public void DischargeOwed_SameViewIdInAnotherFamily_StillReclaimsThisStore()
    {
        StoreFamilyRegistryRow mine = SeedFamily("lineage-scope-mine");
        StoreFamilyRegistryRow other = SeedFamily("lineage-scope-other");
        StoreSidecarReclaimTarget target = SeedMember(mine, "ws-scope-000001", "view-shared");
        IReadOnlyList<string> paths = WriteSidecars(mine.StoreRoot, "view-shared", bytesEach: 7);
        Assert.True(StoreSidecarReclaim.RecordIntent(target));
        _registry.Remove("ws-scope-000001");

        // A live member of a DIFFERENT family holding the same view id claims that family's view, not this
        // store's. Sparing this record would keep the files AND drop the only thing that names them.
        SeedMember(other, "ws-scope-000002", "view-shared");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.DischargeOwed(_registry, mine.StoreRoot);

        Assert.Equal(6, result.FilesDeleted);
        Assert.All(paths, p => Assert.False(File.Exists(p)));
        Assert.Empty(OwedRecords(mine.StoreRoot));
    }

    // ---------- a failed listing is not an empty directory (F4) ----------

    /// <summary>A listing that fails only for the retained-generation pattern, as an ACL denial or a sharing
    /// violation on the sidecar directory would.</summary>
    private static StoreSidecarReclaim.FileLister FailGenerationListing() =>
        (directory, pattern) => pattern.Contains(".gen-", StringComparison.Ordinal)
            ? null
            : Directory.GetFiles(directory, pattern);

    private static (string First, string Second) GenerationPaths(string storeRoot, string viewId)
    {
        string active = StoreSidecarCatalog.PathFor(storeRoot, StoreSidecarKind.Vector, viewId);
        string prefix = Path.Combine(
            Path.GetDirectoryName(active)!, Path.GetFileNameWithoutExtension(active));
        return (prefix + ".gen-aaaa.db", prefix + ".gen-bbbb.db");
    }

    [Fact]
    public void Reclaim_GenerationListingFails_KeepsTheReclaimOwedAndReportsIt()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-listing-failure");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-listfail-0001", "view-listfail");
        WriteSidecars(family.StoreRoot, "view-listfail", bytesEach: 4);
        WriteVectorGenerations(family.StoreRoot, "view-listfail", bytesEach: 40);
        var (first, second) = GenerationPaths(family.StoreRoot, "view-listfail");
        Assert.True(StoreSidecarReclaim.RecordIntent(target));
        _registry.Remove("ws-listfail-0001");

        StoreSidecarReclaimResult result = StoreSidecarReclaim.Reclaim(
            _registry, target, acquireLease: null, listFiles: FailGenerationListing());

        Assert.Equal(StoreSidecarReclaim.ListingFailedReason, result.SkipReason);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Single(OwedRecords(family.StoreRoot));
    }

    [Fact]
    public void DischargeOwed_GenerationListingFails_KeepsTheRecordForTheNextPass()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-listing-discharge");
        StoreSidecarReclaimTarget target = SeedMember(family, "ws-listdis-0001", "view-listdis");
        WriteSidecars(family.StoreRoot, "view-listdis", bytesEach: 4);
        WriteVectorGenerations(family.StoreRoot, "view-listdis", bytesEach: 40);
        var (first, second) = GenerationPaths(family.StoreRoot, "view-listdis");
        Assert.True(StoreSidecarReclaim.RecordIntent(target));
        _registry.Remove("ws-listdis-0001");

        StoreSidecarReclaimResult blocked = StoreSidecarReclaim.DischargeOwed(
            _registry, family.StoreRoot, acquireLease: null, listFiles: FailGenerationListing());

        Assert.Equal(StoreSidecarReclaim.ListingFailedReason, blocked.SkipReason);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Single(OwedRecords(family.StoreRoot));

        // The next pass reads the directory and finishes the job the failed listing left owed.
        StoreSidecarReclaimResult recovered = StoreSidecarReclaim.DischargeOwed(_registry, family.StoreRoot);

        Assert.Equal(2, recovered.FilesDeleted);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Empty(OwedRecords(family.StoreRoot));
    }
}
