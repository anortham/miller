using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store.Coverage;

public sealed class GenerationStoreTests : IDisposable
{
    private static readonly DateTimeOffset Allocated =
        new(2026, 7, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FailedAt =
        new(2026, 7, 14, 8, 30, 0, TimeSpan.Zero);

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-generation-store-").FullName;

    private readonly HashSet<string> _temps = new(StringComparer.Ordinal);

    private string DbPath => Path.Combine(_dir, CtSchema.DbFileName);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string temp in _temps)
            BestEffortDelete(temp);
        BestEffortDelete(_dir);
    }

    [Fact]
    public void Allocate_then_complete_round_trips_through_list()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.PutCtGenerationAllocated(Allocation("g1", "/build/root", "hub-1"));

        CtGenerationRecord allocated = Assert.Single(store.ListCtGenerations("/build/root"));
        Assert.Equal("g1", allocated.GenerationId);
        Assert.Equal(CtGenerationStates.Allocated, allocated.State);
        Assert.Equal("hub-1", allocated.OwnerToken);
        Assert.Equal(Allocated, allocated.AllocatedAt);
        Assert.Null(allocated.CompletedAt);

        DateTimeOffset completedAt = Allocated.AddMinutes(2);
        Assert.True(store.MarkCtGenerationComplete("/build/root", "g1", completedAt));

        CtGenerationRecord completed = Assert.Single(store.ListCtGenerations("/build/root"));
        Assert.Equal(CtGenerationStates.Complete, completed.State);
        Assert.Equal(completedAt, completed.CompletedAt);
    }

    [Fact]
    public void List_ct_generations_returns_only_the_requested_build_output_root()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.PutCtGenerationAllocated(Allocation("g1", "/build/a", "hub-1"));
        store.PutCtGenerationAllocated(Allocation("g2", "/build/b", "hub-1"));

        Assert.Equal(["g1"], store.ListCtGenerations("/build/a").Select(row => row.GenerationId));
    }

    [Fact]
    public void Repeated_allocation_is_idempotent_and_never_resurrects_a_completed_generation()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.PutCtGenerationAllocated(Allocation("g1", "/build/root", "hub-1"));
        store.PutCtGenerationAllocated(Allocation("g1", "/build/root", "hub-1"));
        Assert.Equal(CtGenerationStates.Allocated, Assert.Single(store.ListCtGenerations("/build/root")).State);

        store.MarkCtGenerationComplete("/build/root", "g1", Allocated.AddMinutes(1));
        store.PutCtGenerationAllocated(Allocation("g1", "/build/root", "hub-2"));

        CtGenerationRecord row = Assert.Single(store.ListCtGenerations("/build/root"));
        Assert.Equal(CtGenerationStates.Complete, row.State);
        Assert.Equal("hub-1", row.OwnerToken);
    }

    [Fact]
    public void Double_complete_keeps_the_first_completion_time()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.PutCtGenerationAllocated(Allocation("g1", "/build/root", "hub-1"));
        DateTimeOffset first = Allocated.AddMinutes(1);
        Assert.True(store.MarkCtGenerationComplete("/build/root", "g1", first));
        Assert.True(store.MarkCtGenerationComplete("/build/root", "g1", first.AddMinutes(5)));
        Assert.Equal(first, Assert.Single(store.ListCtGenerations("/build/root")).CompletedAt);
    }

    [Fact]
    public void Complete_on_an_unknown_generation_reports_no_transition()
    {
        using var store = new ContinuousTestStore(DbPath);
        Assert.False(store.MarkCtGenerationComplete("/build/root", "missing", Allocated));
    }

    [Fact]
    public void Release_stale_owners_flips_only_foreign_allocated_generations()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.PutCtGenerationAllocated(Allocation("live", "/build/a", "hub-now"));
        store.PutCtGenerationAllocated(Allocation("dead", "/build/a", "hub-old"));
        store.PutCtGenerationAllocated(Allocation("other-root", "/build/b", "hub-old"));
        store.PutCtGenerationAllocated(Allocation("done", "/build/a", "hub-old"));
        store.MarkCtGenerationComplete("/build/a", "done", Allocated.AddMinutes(1));

        Assert.Equal(2, store.ReleaseStaleCtGenerationOwners("hub-now"));
        Assert.Equal(0, store.ReleaseStaleCtGenerationOwners("hub-now"));

        Dictionary<string, string> states = store.ListCtGenerations("/build/a")
            .Concat(store.ListCtGenerations("/build/b"))
            .ToDictionary(row => row.GenerationId, row => row.State, StringComparer.Ordinal);
        Assert.Equal(CtGenerationStates.Allocated, states["live"]);
        Assert.Equal(CtGenerationStates.ReapEligible, states["dead"]);
        Assert.Equal(CtGenerationStates.ReapEligible, states["other-root"]);
    }

    [Fact]
    public void Mark_reaped_transitions_complete_and_eligible_but_refuses_live_allocated()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.PutCtGenerationAllocated(Allocation("done", "/build/root", "hub-1"));
        store.MarkCtGenerationComplete("/build/root", "done", Allocated.AddMinutes(1));
        store.PutCtGenerationAllocated(Allocation("live", "/build/root", "hub-1"));
        store.PutCtGenerationAllocated(Allocation("eligible", "/build/root", "hub-1"));
        Assert.True(store.MarkCtGenerationReapEligible("/build/root", "eligible", "hub-1"));

        Assert.True(store.MarkCtGenerationReaped("/build/root", "done"));
        Assert.True(store.MarkCtGenerationReaped("/build/root", "eligible"));
        Assert.False(store.MarkCtGenerationReaped("/build/root", "live"));

        Dictionary<string, string> states = store.ListCtGenerations("/build/root")
            .ToDictionary(row => row.GenerationId, row => row.State, StringComparer.Ordinal);
        Assert.Equal(CtGenerationStates.Reaped, states["done"]);
        Assert.Equal(CtGenerationStates.Reaped, states["eligible"]);
        Assert.Equal(CtGenerationStates.Allocated, states["live"]);
    }

    [Fact]
    public void Mark_reap_eligible_is_owner_guarded_and_refuses_complete_or_reaped()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.PutCtGenerationAllocated(Allocation("own", "/build/root", "hub-1"));
        store.PutCtGenerationAllocated(Allocation("foreign", "/build/root", "hub-2"));
        store.PutCtGenerationAllocated(Allocation("done", "/build/root", "hub-1"));
        store.MarkCtGenerationComplete("/build/root", "done", Allocated.AddMinutes(1));

        Assert.True(store.MarkCtGenerationReapEligible("/build/root", "own", "hub-1"));
        Assert.True(store.MarkCtGenerationReapEligible("/build/root", "own", "hub-1"));
        Assert.False(store.MarkCtGenerationReapEligible("/build/root", "foreign", "hub-1"));
        Assert.False(store.MarkCtGenerationReapEligible("/build/root", "done", "hub-1"));
        Assert.False(store.MarkCtGenerationReapEligible("/build/root", "missing", "hub-1"));

        store.MarkCtGenerationReaped("/build/root", "own");
        Assert.False(store.MarkCtGenerationReapEligible("/build/root", "own", "hub-1"));
    }

    [Fact]
    public void Reap_debt_round_trips_keeps_first_failure_and_is_not_a_run_failure()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.PutTestCase(new ContinuousTestCase(
            Id: "test:1",
            WorkspaceId: "ws:1",
            Name: "Fact",
            QualifiedName: "Fact",
            Selector: "Fact.selector"));
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: "run:1",
                WorkspaceId: "ws:1",
                Status: "running",
                SelectedRevision: "12",
                IndexIdentity: "gen-1",
                Revision: 12),
            ["test:1"]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: "ws:1",
            TestRunId: "run:1",
            SelectedRevision: "12",
            CurrentRevision: "12",
            IndexIdentity: "gen-1",
            Revision: 12,
            Status: "passed",
            Results:
            [
                new ContinuousTestResult(
                    Id: "res:1",
                    WorkspaceId: "ws:1",
                    TestCaseId: "test:1",
                    TestRunId: "run:1",
                    Status: "passed",
                    ResultRevision: "12",
                    IndexIdentity: "gen-1",
                    Revision: 12),
            ]));

        store.UpsertCtGenerationReapDebt("/build/root", "g0007", 4096, FailedAt);
        store.UpsertCtGenerationReapDebt("/build/root", "g0007", 8192, FailedAt.AddMinutes(30));
        store.UpsertCtGenerationReapDebt("/build/root", ".reap-g0007", 2, FailedAt);
        store.UpsertCtGenerationReapDebt("/build/other", "g0007", 4, FailedAt);

        IReadOnlyList<CtGenerationReapDebtRecord> debt = store.ListCtGenerationReapDebt();
        Assert.Equal(3, debt.Count);
        CtGenerationReapDebtRecord first = debt.Single(row =>
            row.BuildOutputRoot == "/build/root" && row.DirectoryName == "g0007");
        Assert.Equal(8192, first.Bytes);
        Assert.Equal(FailedAt, first.FirstFailedAt);
        Assert.Equal(FailedAt.AddMinutes(30), first.LastFailedAt);

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses("ws:1"));
        Assert.Equal(ContinuousTestState.Green, status.State);
        Assert.Equal("passed", status.LastResultStatus);

        store.ClearCtGenerationReapDebt("/build/root", "g0007");
        store.ClearCtGenerationReapDebt("/build/root", "missing");
        Assert.Equal(2, store.ListCtGenerationReapDebt().Count);
        Assert.Equal(ContinuousTestState.Green, Assert.Single(store.ListContinuousTestStatuses("ws:1")).State);
    }

    [Fact]
    public void Generation_disk_and_pressure_round_trip_and_replace()
    {
        using var store = new ContinuousTestStore(DbPath);
        Assert.Null(store.GetCtGenerationPressure());

        store.UpsertCtGenerationDisk("/build/a", 1024, stale: false, FailedAt);
        store.UpsertCtGenerationDisk("/build/a", 2048, stale: true, FailedAt.AddMinutes(5));
        store.UpsertCtGenerationDisk("/build/b", 4096, stale: false, FailedAt);
        store.DeleteCtGenerationDisk("/build/b");
        store.UpsertCtGenerationPressure(1024, rootsTotal: 3, rootsMeasured: 1, FailedAt);
        store.UpsertCtGenerationPressure(2048, rootsTotal: 3, rootsMeasured: 3, FailedAt.AddMinutes(10));

        CtGenerationDiskRecord disk = Assert.Single(store.ListCtGenerationDisk());
        Assert.Equal("/build/a", disk.BuildOutputRoot);
        Assert.Equal(2048, disk.Bytes);
        Assert.True(disk.Stale);
        CtGenerationPressureRecord pressure = store.GetCtGenerationPressure()!;
        Assert.Equal(2048, pressure.BudgetBytes);
        Assert.Equal(3, pressure.RootsTotal);
        Assert.Equal(3, pressure.RootsMeasured);
    }

    [Fact]
    public void Store_records_use_shared_short_hashed_generation_ids()
    {
        ContinuousTestWorkspace workspace = Workspace();
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        using var store = new ContinuousTestStore(DbPath);

        store.PutCtGenerationAllocated(Allocation(paths.GenerationId, workspace.BuildOutputRoot, "hub-1"));

        Assert.True(CtGenerationPaths.IsGenerationId(paths.GenerationId));
        Assert.True(paths.GenerationId.Length <= 16);
        Assert.StartsWith("g", paths.GenerationId, StringComparison.Ordinal);
        Assert.Equal(CtTempPaths.RootDirectoryName, "miller-ct");
        Assert.Contains(
            Path.DirectorySeparatorChar + "miller-ct" + Path.DirectorySeparatorChar,
            paths.TempDirectory,
            StringComparison.Ordinal);
        Assert.Equal(
            paths.GenerationId,
            Assert.Single(store.ListCtGenerations(workspace.BuildOutputRoot)).GenerationId);
        if (OperatingSystem.IsWindows())
            Assert.True(paths.GenerationRoot.Length < 200, paths.GenerationRoot);
    }

    [Fact]
    public void Failed_reap_is_recorded_as_debt_and_does_not_fail_the_generation_row()
    {
        ContinuousTestWorkspace workspace = Workspace();
        CtGenerationPaths paths = CtGenerationPaths.Allocate(workspace);
        paths.EnsureDirectories();
        using var store = new ContinuousTestStore(DbPath);
        store.PutCtGenerationAllocated(Allocation(paths.GenerationId, workspace.BuildOutputRoot, "hub-1"));
        store.MarkCtGenerationComplete(workspace.BuildOutputRoot, paths.GenerationId, Allocated.AddMinutes(1));

        Assert.False(CtGenerationPaths.TryReap(
            paths.GenerationRoot,
            (_, _) => throw new IOException("sharing violation"),
            _ => { }));
        store.UpsertCtGenerationReapDebt(
            workspace.BuildOutputRoot,
            paths.GenerationId,
            bytes: 128,
            FailedAt);

        Assert.Equal(CtGenerationStates.Complete, Assert.Single(store.ListCtGenerations(workspace.BuildOutputRoot)).State);
        Assert.Equal(paths.GenerationId, Assert.Single(store.ListCtGenerationReapDebt()).DirectoryName);
        Assert.True(Directory.Exists(paths.GenerationRoot));
    }

    [Fact]
    public void Missing_db_generation_reads_return_empty()
    {
        using var store = new ContinuousTestStore(DbPath);
        Assert.Empty(store.ListCtGenerations("/build/root"));
        Assert.Empty(store.ListCtGenerationReapDebt());
        Assert.Empty(store.ListCtGenerationDisk());
        Assert.Null(store.GetCtGenerationPressure());
        Assert.False(File.Exists(DbPath));
    }

    private ContinuousTestWorkspace Workspace()
    {
        string workspaceRoot = Path.Combine(_dir, "repo");
        string buildRoot = Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build");
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:1",
            WorkspaceRoot: workspaceRoot,
            ProjectPath: Path.Combine(workspaceRoot, "tests", "Sample.Tests", "Sample.Tests.csproj"),
            BuildOutputRoot: buildRoot);
        _temps.Add(CtTempPaths.ForWorkspace(workspace));
        return workspace;
    }

    private static CtGenerationRecord Allocation(string generationId, string root, string owner) =>
        new(
            GenerationId: generationId,
            BuildOutputRoot: root,
            State: CtGenerationStates.Allocated,
            OwnerToken: owner,
            AllocatedAt: Allocated,
            CompletedAt: null);

    private static void BestEffortDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
