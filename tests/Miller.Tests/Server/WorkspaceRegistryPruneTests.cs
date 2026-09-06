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
///
/// <para>It also pins the linked-worktree removal proof that gates the destructive half: which recorded lineage
/// shapes confirm a removal, which refuse it, and how many producer retirements one run may spend.</para>
/// </summary>
public sealed class WorkspaceRegistryPruneTests : IDisposable
{
    private readonly string _dir;
    private readonly string _registryDb;
    private readonly WorkspaceRegistry _registry;

    public WorkspaceRegistryPruneTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-prune-reclaim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "common"));
        _registryDb = Path.Combine(_dir, "workspaces.db");
        _registry = WorkspaceRegistry.Open(_registryDb);
    }

    public void Dispose()
    {
        _registry.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string CommonDir => Path.Combine(_dir, "common");

    private string AdminDirFor(string workspaceId) =>
        Path.Combine(CommonDir, "worktrees", workspaceId);

    private StoreFamilyRegistryRow SeedFamily(
        string lineage,
        bool createStoreRoot = true,
        string? canonicalCommonDir = null)
    {
        StoreFamilyRegistryRow family = _registry.GetOrCreateStoreFamily(
            lineage, canonicalCommonDir, commonDirCreatedAtUtc: null,
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

    private void MarkLinkedLineage(
        string workspaceId,
        bool adminDirExists = false,
        bool isLinked = true,
        string? commonDir = null,
        string? adminDir = null)
    {
        WorkspaceRegistryRow row = _registry.Get(workspaceId) ?? throw new InvalidOperationException();
        string admin = adminDir ?? AdminDirFor(workspaceId);
        if (adminDirExists)
            Directory.CreateDirectory(admin);
        _registry.UpsertSeen(
            row.WorkspaceId,
            row.DisplayId,
            row.CanonicalRoot,
            row.IndexDbPath,
            row.State,
            lineage: new WorkspaceLineage(
                commonDir ?? CommonDir,
                IsLinkedWorktree: isLinked,
                GitDir: admin,
                GitDirCreatedAtUtc: DateTimeOffset.UtcNow));
    }

    private void JoinFamily(
        StoreFamilyRegistryRow family,
        string workspaceId,
        string root,
        string viewId,
        bool markConfirmedRemovedLinked = true,
        WorkspaceRootIdentity rootIdentity = default)
    {
        if (markConfirmedRemovedLinked)
            MarkLinkedLineage(workspaceId);
        _registry.UpsertStoreMember(workspaceId, family.FamilyId, viewId, root, rootIdentity);
    }

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

    private static StoreViewRetirementOutcome RetireView(StoreSidecarReclaimTarget target, bool apply) =>
        new(
            apply ? StoreViewRetirementDisposition.Retired : StoreViewRetirementDisposition.Planned,
            target.FamilyId,
            target.ViewId,
            apply ? 1 : 0,
            0,
            0,
            null);

    private (WorkspaceRegistryPrune.Result Result, int ProducerCalls) RunCountingProducerCalls()
    {
        int producerCalls = 0;
        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (target, apply) =>
            {
                producerCalls++;
                return RetireView(target, apply);
            });
        return (result, producerCalls);
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
            },
            retireView: RetireView);

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

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: RetireView);

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

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: true,
            retireView: RetireView);

        Assert.Single(result.Pruned);
        Assert.False(result.SidecarReclaim.HasReport);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.NotNull(_registry.GetStoreMember("ws-prune-dry-00001"));
    }

    [Fact]
    public void Run_ConfirmedRemovedLinkedWorktree_AllowsProducerRetirement()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-confirmed-removal");
        string goneRoot = Register("ws-prune-confirmed-0001", "confirmed-repo", rootExists: false);
        JoinFamily(family, "ws-prune-confirmed-0001", goneRoot, "view-confirmed");
        var calls = new List<bool>();

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (target, apply) =>
            {
                calls.Add(apply);
                return RetireView(target, apply);
            });

        Assert.Single(result.Pruned);
        Assert.Empty(result.RetirementFailures);
        Assert.Equal(new[] { false, true }, calls);
        Assert.Null(_registry.Get("ws-prune-confirmed-0001"));
    }

    [Fact]
    public void Run_LineageOnlyInTheStoreMember_ConfirmsTheRemoval()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-store-only", canonicalCommonDir: CommonDir);
        string goneRoot = Register("ws-prune-store-only-1", "store-only-repo", rootExists: false);
        JoinFamily(
            family,
            "ws-prune-store-only-1",
            goneRoot,
            "view-store-only",
            markConfirmedRemovedLinked: false,
            rootIdentity: new WorkspaceRootIdentity(
                AdminDirFor("ws-prune-store-only-1"), DateTimeOffset.UtcNow));
        WorkspaceRegistryRow row = _registry.Get("ws-prune-store-only-1")!;
        Assert.Null(row.GitDir);
        Assert.Null(row.GitIsLinked);

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: null, dryRun: false, retireView: RetireView);

        Assert.Single(result.Pruned);
        Assert.Empty(result.RetirementFailures);
        Assert.Null(_registry.Get("ws-prune-store-only-1"));
    }

    [Fact]
    public void Run_AdminDirParentGone_ConfirmsTheRemovalFromThePresentCommonDir()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-parent-gone");
        string goneRoot = Register("ws-prune-parent-gone-1", "parent-gone-repo", rootExists: false);
        JoinFamily(family, "ws-prune-parent-gone-1", goneRoot, "view-parent-gone");
        Assert.False(Directory.Exists(Path.GetDirectoryName(AdminDirFor("ws-prune-parent-gone-1"))));
        Assert.True(Directory.Exists(CommonDir));

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: null, dryRun: false, retireView: RetireView);

        Assert.Single(result.Pruned);
        Assert.Empty(result.RetirementFailures);
        Assert.Null(_registry.Get("ws-prune-parent-gone-1"));
    }

    [Fact]
    public void Run_AdminDirOutsideTheRepositoryWorktreesDirectory_RefusesTheRemoval()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-submodule", canonicalCommonDir: CommonDir);
        string goneRoot = Register("ws-prune-submodule-1", "submodule-repo", rootExists: false);
        JoinFamily(
            family,
            "ws-prune-submodule-1",
            goneRoot,
            "view-submodule",
            markConfirmedRemovedLinked: false,
            rootIdentity: new WorkspaceRootIdentity(
                Path.Combine(CommonDir, "modules", "submodule-a"), DateTimeOffset.UtcNow));

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: null, dryRun: false, retireView: RetireView);

        Assert.Empty(result.Pruned);
        Assert.NotNull(_registry.Get("ws-prune-submodule-1"));
    }

    [Fact]
    public void Run_ASurvivingAdminEntryStillRegistersTheRoot_RefusesTheRemoval()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-renamed-admin");
        string goneRoot = Register("ws-prune-renamed-1", "renamed-admin-repo", rootExists: false);
        JoinFamily(family, "ws-prune-renamed-1", goneRoot, "view-renamed");

        string renamed = Path.Combine(CommonDir, "worktrees", "renamed-admin-repo-moved");
        Directory.CreateDirectory(renamed);
        File.WriteAllText(Path.Combine(renamed, "gitdir"), Path.Combine(goneRoot, ".git"));
        Assert.False(Directory.Exists(AdminDirFor("ws-prune-renamed-1")));

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: null, dryRun: false, retireView: RetireView);

        Assert.Empty(result.Pruned);
        Assert.NotNull(_registry.Get("ws-prune-renamed-1"));
    }

    [Fact]
    public void Run_ASurvivingAdminEntryRegisteringAnotherRoot_StillConfirmsTheRemoval()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-sibling-admin");
        string goneRoot = Register("ws-prune-sibling-1", "sibling-admin-repo", rootExists: false);
        JoinFamily(family, "ws-prune-sibling-1", goneRoot, "view-sibling");

        string sibling = Path.Combine(CommonDir, "worktrees", "some-other-worktree");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(
            Path.Combine(sibling, "gitdir"),
            Path.Combine(_dir, "some-other-worktree", ".git"));

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: null, dryRun: false, retireView: RetireView);

        Assert.Single(result.Pruned);
        Assert.Null(_registry.Get("ws-prune-sibling-1"));
    }

    [Fact]
    public void Run_AdminDirParentUnreadable_RefusesTheRemoval()
    {
        // POSIX-only: File.SetUnixFileMode is unsupported on Windows, and root reads a 000 directory anyway.
        if (OperatingSystem.IsWindows())
            return;
        Assert.SkipWhen(Environment.UserName == "root", "root reads a directory whatever its mode bits say.");

        StoreFamilyRegistryRow family = SeedFamily("lineage-parent-unreadable");
        string goneRoot = Register("ws-prune-unreadable-1", "unreadable-repo", rootExists: false);
        JoinFamily(family, "ws-prune-unreadable-1", goneRoot, "view-unreadable");

        string adminDir = AdminDirFor("ws-prune-unreadable-1");
        string parent = Path.GetDirectoryName(adminDir)!;
        Directory.CreateDirectory(adminDir);
        File.SetUnixFileMode(parent, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(Directory.Exists(adminDir), "The mode bits did not hide the admin dir.");

            WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
                _registry, protectedWorkspaceId: null, dryRun: false, retireView: RetireView);

            Assert.Empty(result.Pruned);
            Assert.NotNull(_registry.Get("ws-prune-unreadable-1"));
        }
        finally
        {
            File.SetUnixFileMode(
                parent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Run_CommonDirAbsent_RefusesTheRemoval()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-common-gone");
        string goneRoot = Register("ws-prune-common-gone-1", "common-gone-repo", rootExists: false);
        JoinFamily(
            family,
            "ws-prune-common-gone-1",
            goneRoot,
            "view-common-gone",
            markConfirmedRemovedLinked: false);
        string unmountedCommon = Path.Combine(_dir, "unmounted-volume", ".git");
        MarkLinkedLineage(
            "ws-prune-common-gone-1",
            commonDir: unmountedCommon,
            adminDir: Path.Combine(unmountedCommon, "worktrees", "ws-prune-common-gone-1"));

        (WorkspaceRegistryPrune.Result result, int producerCalls) = RunCountingProducerCalls();

        Assert.Empty(result.Pruned);
        Assert.Single(result.RetirementFailures);
        Assert.Equal(0, producerCalls);
        Assert.NotNull(_registry.Get("ws-prune-common-gone-1"));
        Assert.NotNull(_registry.GetStoreMember("ws-prune-common-gone-1"));
    }

    [Fact]
    public void Run_CommonDirEqualsAdminDir_RefusesTheRemoval()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-plain-checkout");
        string goneRoot = Register("ws-prune-plain-checkout-1", "plain-checkout-repo", rootExists: false);
        JoinFamily(
            family,
            "ws-prune-plain-checkout-1",
            goneRoot,
            "view-plain-checkout",
            markConfirmedRemovedLinked: false);
        MarkLinkedLineage("ws-prune-plain-checkout-1", commonDir: CommonDir, adminDir: CommonDir);

        (WorkspaceRegistryPrune.Result result, int producerCalls) = RunCountingProducerCalls();

        Assert.Empty(result.Pruned);
        Assert.Single(result.RetirementFailures);
        Assert.Equal(0, producerCalls);
        Assert.NotNull(_registry.Get("ws-prune-plain-checkout-1"));
    }

    [Fact]
    public void Run_RowRecordedAsNotLinked_RefusesTheRemoval()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-not-linked", canonicalCommonDir: CommonDir);
        string goneRoot = Register("ws-prune-not-linked-1", "not-linked-repo", rootExists: false);
        JoinFamily(
            family,
            "ws-prune-not-linked-1",
            goneRoot,
            "view-not-linked",
            markConfirmedRemovedLinked: false,
            rootIdentity: new WorkspaceRootIdentity(
                AdminDirFor("ws-prune-not-linked-1"), DateTimeOffset.UtcNow));
        MarkLinkedLineage("ws-prune-not-linked-1", isLinked: false);

        (WorkspaceRegistryPrune.Result result, int producerCalls) = RunCountingProducerCalls();

        Assert.Empty(result.Pruned);
        Assert.Single(result.RetirementFailures);
        Assert.Equal(0, producerCalls);
        Assert.NotNull(_registry.Get("ws-prune-not-linked-1"));
        Assert.NotNull(_registry.GetStoreMember("ws-prune-not-linked-1"));
    }

    [Fact]
    public void Run_NoRecordedLineageAnywhere_RefusesTheRemoval()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-none");
        string goneRoot = Register("ws-prune-no-lineage-1", "no-lineage-repo", rootExists: false);
        JoinFamily(
            family,
            "ws-prune-no-lineage-1",
            goneRoot,
            "view-no-lineage",
            markConfirmedRemovedLinked: false);

        (WorkspaceRegistryPrune.Result result, int producerCalls) = RunCountingProducerCalls();

        Assert.Empty(result.Pruned);
        Assert.Single(result.RetirementFailures);
        Assert.Equal(0, producerCalls);
        Assert.NotNull(_registry.Get("ws-prune-no-lineage-1"));
    }

    [Fact]
    public void Run_AFailedRetirement_LeavesTheBudgetForTheNextRow()
    {
        StoreFamilyRegistryRow first = SeedFamily("lineage-failed-budget-first");
        StoreFamilyRegistryRow second = SeedFamily("lineage-failed-budget-second");
        string firstRoot = Register("ws-prune-failed-cap-1", "failed-cap-first", rootExists: false);
        string secondRoot = Register("ws-prune-failed-cap-2", "failed-cap-second", rootExists: false);
        JoinFamily(first, "ws-prune-failed-cap-1", firstRoot, "view-failed-cap-first");
        JoinFamily(second, "ws-prune-failed-cap-2", secondRoot, "view-failed-cap-second");
        var attempted = new List<string>();

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (target, _) =>
            {
                attempted.Add(target.ViewId);
                return new StoreViewRetirementOutcome(
                    StoreViewRetirementDisposition.Failed,
                    target.FamilyId,
                    target.ViewId,
                    0,
                    0,
                    0,
                    "producer unavailable");
            },
            maxProducerRetirements: 1);

        Assert.Equal(
            new[] { "view-failed-cap-first", "view-failed-cap-second" },
            attempted.Order(StringComparer.Ordinal));
        Assert.Empty(result.Pruned);
        Assert.Equal(2, result.RetirementFailures.Count);
        Assert.All(
            result.RetirementFailures,
            failure => Assert.Equal("producer unavailable", failure.Outcome.Error));
    }

    [Fact]
    public void Run_DefaultsToFiveProducerRetirementsPerRun()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-default-budget");
        for (int i = 0; i < 6; i++)
        {
            string workspaceId = $"ws-prune-default-cap-{i}";
            string goneRoot = Register(workspaceId, $"default-cap-repo-{i}", rootExists: false);
            JoinFamily(family, workspaceId, goneRoot, $"view-default-cap-{i}");
        }

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry, protectedWorkspaceId: null, dryRun: false, retireView: RetireView);

        Assert.Equal(5, result.Pruned.Count);
        Assert.Equal(1, result.Kept);
        Assert.Single(result.RetirementFailures);
        Assert.Contains(
            "rerun prune",
            result.RetirementFailures[0].Outcome.Error!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_AdminDirStillPresent_KeepsMemberAndReportsExactRemovalAction()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-unavailable");
        string goneRoot = Register("ws-prune-unavailable-1", "unavailable-repo", rootExists: false);
        JoinFamily(
            family,
            "ws-prune-unavailable-1",
            goneRoot,
            "view-unavailable",
            markConfirmedRemovedLinked: false);
        MarkLinkedLineage("ws-prune-unavailable-1", adminDirExists: true);
        int producerCalls = 0;

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (_, _) =>
            {
                producerCalls++;
                return default;
            });

        Assert.Empty(result.Pruned);
        Assert.Equal(1, result.Kept);
        Assert.Single(result.RetirementFailures);
        string error = result.RetirementFailures[0].Outcome.Error!;
        Assert.Contains("linked-worktree removal is not confirmed", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(error.Contains("producer is unavailable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("workspace remove", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, producerCalls);
        Assert.NotNull(_registry.Get("ws-prune-unavailable-1"));
        Assert.NotNull(_registry.GetStoreMember("ws-prune-unavailable-1"));
    }

    [Fact]
    public void Run_DryRun_ConsumesTheConfirmedProducerBudget()
    {
        StoreFamilyRegistryRow first = SeedFamily("lineage-budget-dry-first");
        StoreFamilyRegistryRow second = SeedFamily("lineage-budget-dry-second");
        string firstRoot = Register("ws-prune-budget-dry-1", "budget-dry-first", rootExists: false);
        string secondRoot = Register("ws-prune-budget-dry-2", "budget-dry-second", rootExists: false);
        JoinFamily(first, "ws-prune-budget-dry-1", firstRoot, "view-budget-dry-first");
        JoinFamily(second, "ws-prune-budget-dry-2", secondRoot, "view-budget-dry-second");
        var calls = new List<bool>();

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: true,
            retireView: (target, apply) =>
            {
                calls.Add(apply);
                return RetireView(target, apply);
            },
            maxProducerRetirements: 1);

        Assert.Single(result.Pruned);
        Assert.Equal(1, result.Kept);
        Assert.Equal(new[] { false }, calls);
        Assert.Single(result.RetirementFailures);
        Assert.Contains("rerun prune", result.RetirementFailures[0].Outcome.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(_registry.Get("ws-prune-budget-dry-2"));
    }

    [Fact]
    public void Run_Apply_ConsumesTheConfirmedProducerBudget()
    {
        StoreFamilyRegistryRow first = SeedFamily("lineage-budget-apply-first");
        StoreFamilyRegistryRow second = SeedFamily("lineage-budget-apply-second");
        string firstRoot = Register("ws-prune-budget-apply-1", "budget-apply-first", rootExists: false);
        string secondRoot = Register("ws-prune-budget-apply-2", "budget-apply-second", rootExists: false);
        JoinFamily(first, "ws-prune-budget-apply-1", firstRoot, "view-budget-apply-first");
        JoinFamily(second, "ws-prune-budget-apply-2", secondRoot, "view-budget-apply-second");
        var calls = new List<bool>();

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (target, apply) =>
            {
                calls.Add(apply);
                return RetireView(target, apply);
            },
            maxProducerRetirements: 1);

        Assert.Single(result.Pruned);
        Assert.Equal(1, result.Kept);
        Assert.Equal(new[] { false, true }, calls);
        Assert.Single(result.RetirementFailures);
        Assert.Contains("rerun prune", result.RetirementFailures[0].Outcome.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_registry.Get("ws-prune-budget-apply-1"));
        Assert.NotNull(_registry.Get("ws-prune-budget-apply-2"));
    }

    [Fact]
    public void Run_MissingNonStoreWorkspace_PreservesLegacyPrune()
    {
        Register("ws-prune-plain-0002", "plain-repo-2", rootExists: false);
        int producerCalls = 0;

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (_, _) =>
            {
                producerCalls++;
                return default;
            });

        Assert.Single(result.Pruned);
        Assert.Empty(result.RetirementFailures);
        Assert.Equal(0, producerCalls);
        Assert.Null(_registry.Get("ws-prune-plain-0002"));
    }

    [Fact]
    public void Run_RetiresCapturedViewBeforeDeletingRegistryMember()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-retire-order");
        string goneRoot = Register("ws-prune-retire-order-1", "retire-order-repo", rootExists: false);
        JoinFamily(family, "ws-prune-retire-order-1", goneRoot, "view-prune-retire-order");
        var calls = new List<(bool Apply, bool MemberPresent, StoreSidecarReclaimTarget Target)>();

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (target, apply) =>
            {
                calls.Add((apply, _registry.Get("ws-prune-retire-order-1") is not null, target));
                return new StoreViewRetirementOutcome(
                    apply ? StoreViewRetirementDisposition.Retired : StoreViewRetirementDisposition.Planned,
                    target.FamilyId,
                    target.ViewId,
                    apply ? 1 : 0,
                    0,
                    0,
                    null);
            });

        Assert.Single(result.Pruned);
        Assert.Equal(2, calls.Count);
        Assert.Equal(
            new StoreSidecarReclaimTarget(family.FamilyId, "view-prune-retire-order", family.StoreRoot),
            calls[0].Target);
        Assert.False(calls[0].Apply);
        Assert.True(calls[0].MemberPresent);
        Assert.True(calls[1].Apply);
        Assert.True(calls[1].MemberPresent);
        Assert.Null(_registry.Get("ws-prune-retire-order-1"));
    }

    [Fact]
    public void Run_RetirementFailure_KeepsMemberAndSkipsReclaimAndMaintenance()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-retire-fails");
        string goneRoot = Register("ws-prune-retire-fails-1", "retire-fails-repo", rootExists: false);
        JoinFamily(family, "ws-prune-retire-fails-1", goneRoot, "view-prune-retire-fails");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-prune-retire-fails");
        int leaseRequests = 0;
        int maintenanceRequests = 0;

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            acquireSidecarLease: _ =>
            {
                leaseRequests++;
                return null;
            },
            maintainStore: _ =>
            {
                maintenanceRequests++;
                return StoreMaintenanceOutcome.None;
            },
            retireView: (target, apply) => new StoreViewRetirementOutcome(
                StoreViewRetirementDisposition.Failed,
                target.FamilyId,
                target.ViewId,
                0,
                0,
                0,
                "producer unavailable"));

        Assert.Empty(result.Pruned);
        Assert.Single(result.RetirementFailures);
        Assert.Equal("ws-prune-retire-fails-1", result.RetirementFailures[0].WorkspaceId);
        Assert.Equal("producer unavailable", result.RetirementFailures[0].Outcome.Error);
        Assert.NotNull(_registry.Get("ws-prune-retire-fails-1"));
        Assert.NotNull(_registry.GetStoreMember("ws-prune-retire-fails-1"));
        Assert.Equal(0, leaseRequests);
        Assert.Equal(0, maintenanceRequests);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Run_Intent_write_failure_keeps_member_without_producer_retirement()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-intent-fail");
        const string workspaceId = "ws-prune-intent-fail-1";
        string goneRoot = Register(workspaceId, "intent-fail-repo", rootExists: false);
        const string viewId = "view-prune-intent-fail";
        JoinFamily(family, workspaceId, goneRoot, viewId);
        string owedPath = Path.Combine(
            StoreSidecarCatalog.DirectoryFor(family.StoreRoot),
            StoreSidecarCatalog.ViewKey(viewId) + StoreSidecarReclaim.OwedRecordSuffix);
        Directory.CreateDirectory(owedPath);
        int producerCalls = 0;

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (target, apply) =>
            {
                producerCalls++;
                return RetireView(target, apply);
            });

        Assert.Empty(result.Pruned);
        Assert.Single(result.RetirementFailures);
        Assert.Equal(StoreSidecarReclaim.IntentNotRecordedReason, result.RetirementFailures[0].Outcome.Error);
        Assert.Equal(0, producerCalls);
        Assert.NotNull(_registry.Get(workspaceId));
    }

    [Fact]
    public void Run_MalformedStoreMemberKeepsRegistryRowAndSkipsProducer()
    {
        string goneRoot = Register("ws-prune-malformed-1", "malformed-repo", rootExists: false);
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-malformed");
        _registry.UpsertStoreMember(
            "ws-prune-malformed-1", family.FamilyId, "view-prune-malformed", goneRoot, WorkspaceRootIdentity.Unknown);
        using (var connection = new SqliteConnection($"Data Source={_registryDb};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_families SET store_root = '' WHERE family_id = $family_id";
            command.Parameters.AddWithValue("$family_id", family.FamilyId.ToString("D"));
            command.ExecuteNonQuery();
        }
        int producerCalls = 0;

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (_, _) =>
            {
                producerCalls++;
                return default;
            });

        Assert.Empty(result.Pruned);
        Assert.Single(result.RetirementFailures);
        Assert.Equal(0, producerCalls);
        Assert.NotNull(_registry.Get("ws-prune-malformed-1"));
        Assert.NotNull(_registry.GetStoreMember("ws-prune-malformed-1"));
    }

    [Fact]
    public void Run_WithoutAMaintainer_SpawnsNothingAndReportsNoMaintenance()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-no-maintain");
        string goneRoot = Register("ws-prune-nom-00001", "no-maintain-repo", rootExists: false);
        JoinFamily(family, "ws-prune-nom-00001", goneRoot, "view-no-maintain");

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: RetireView);

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
            },
            retireView: RetireView);

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
            maintainStore: _ => new StoreMaintenanceOutcome(0, "store maintenance timed out"),
            retireView: RetireView);

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
            maintainStore: _ => throw new InvalidOperationException("a dry run must never maintain"),
            retireView: RetireView);

        Assert.Single(result.Pruned);
        Assert.False(result.StoreMaintenance.HasReport);
    }

    [Fact]
    public void Run_MissingStoreRoot_PrunesAndReportsNothing()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-absent", createStoreRoot: false);
        string goneRoot = Register("ws-prune-abs-00001", "absent-repo", rootExists: false);
        JoinFamily(family, "ws-prune-abs-00001", goneRoot, "view-absent");

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: RetireView);

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
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            acquireSidecarLease: _ => null,
            retireView: RetireView);

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
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            acquireSidecarLease: _ => null,
            retireView: RetireView);
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
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            acquireSidecarLease: _ => null,
            retireView: RetireView);

        WorkspaceRegistryPrune.Result result =
            WorkspaceRegistryPrune.Run(_registry, protectedWorkspaceId: null, dryRun: true);

        Assert.False(result.SidecarReclaim.HasReport);
        Assert.All(paths, p => Assert.True(File.Exists(p)));
    }

    [Fact]
    public void Run_WhenNotAwaitingProducer_UnregistersWithoutCallingRetireView()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-defer");
        string goneRoot = Register("ws-prune-defer-01", "defer-repo", rootExists: false);
        JoinFamily(family, "ws-prune-defer-01", goneRoot, "view-prune-defer");
        IReadOnlyList<string> paths = WriteSidecars(family.StoreRoot, "view-prune-defer");
        int producerCalls = 0;
        StoreSidecarReclaimTarget? owed = null;

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: false,
            retireView: (target, apply) =>
            {
                producerCalls++;
                return RetireView(target, apply);
            },
            awaitProducerRetirement: false,
            onRetirementOwed: target => owed = target);

        Assert.Single(result.Pruned);
        Assert.Equal(0, producerCalls);
        Assert.Equal(0, result.SidecarReclaim.FilesDeleted);
        Assert.Null(_registry.Get("ws-prune-defer-01"));
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.Equal(
            new StoreSidecarReclaimTarget(family.FamilyId, "view-prune-defer", family.StoreRoot),
            owed);
    }

    [Fact]
    public void Run_DryRunWithoutAwait_DoesNotEnqueue()
    {
        StoreFamilyRegistryRow family = SeedFamily("lineage-prune-defer-dry");
        string goneRoot = Register("ws-prune-defdry-01", "defer-dry-repo", rootExists: false);
        JoinFamily(family, "ws-prune-defdry-01", goneRoot, "view-prune-defer-dry");
        int producerCalls = 0;
        int owedCalls = 0;

        WorkspaceRegistryPrune.Result result = WorkspaceRegistryPrune.Run(
            _registry,
            protectedWorkspaceId: null,
            dryRun: true,
            retireView: (target, apply) =>
            {
                producerCalls++;
                return RetireView(target, apply);
            },
            awaitProducerRetirement: false,
            onRetirementOwed: _ => owedCalls++);

        Assert.Single(result.Pruned);
        Assert.Equal(0, producerCalls);
        Assert.Equal(0, owedCalls);
        Assert.NotNull(_registry.Get("ws-prune-defdry-01"));
    }

}
