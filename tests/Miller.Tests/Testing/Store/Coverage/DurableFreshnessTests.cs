using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store.Coverage;

public sealed class DurableFreshnessTests : IDisposable
{
    private const string Workspace = "ws:1";
    private const string Identity = "gen-1";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-durable-freshness-").FullName;

    private string DbPath => Path.Combine(_dir, CtSchema.DbFileName);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Committed_freshness_round_trips_the_composite_key()
    {
        using var store = CreateStoreWithProviderCase("a:1", ProjectA());
        CommitGreen(store, "a:1", Identity, 12);

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.True(ContinuousTestDurableFreshness.IsCommittedFreshAt(status, new CtFreshnessKey(Identity, 12)));
        Assert.False(ContinuousTestDurableFreshness.IsCommittedFreshAt(status, new CtFreshnessKey(Identity, 13)));
        Assert.False(ContinuousTestDurableFreshness.IsCommittedFreshAt(status, new CtFreshnessKey("gen-2", 12)));
    }

    [Fact]
    public void Advance_fresh_watermark_marks_committed_provider_cases_at_the_composite_key()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "a:1", ProjectA());
        SeedProviderCase(store, "a:2", ProjectA());
        SeedProviderCase(store, "b:1", ProjectB());
        CommitGreen(store, "a:1", Identity, 267);
        CommitGreen(store, "a:2", Identity, 267);
        CommitGreen(store, "b:1", Identity, 267);

        store.AdvanceContinuousTestFreshWatermark(Workspace, ProjectA(), Key(267), Key(273));

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.Equal(new CtFreshnessKey(Identity, 273), watermarks["a:1"]);
        Assert.Equal(new CtFreshnessKey(Identity, 273), watermarks["a:2"]);
        Assert.False(watermarks.ContainsKey("b:1"));
    }

    [Fact]
    public void Advance_fresh_watermark_skips_cases_not_fresh_at_the_from_key()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "committed-at-from", ProjectA());
        SeedProviderCase(store, "committed-below-from", ProjectA());
        SeedProviderCase(store, "running", ProjectA());
        SeedProviderCase(store, "never-run", ProjectA());
        CommitGreen(store, "committed-at-from", Identity, 267);
        CommitGreen(store, "committed-below-from", Identity, 200);
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: "run:live",
                WorkspaceId: Workspace,
                Status: "running",
                SelectedRevision: "267",
                IndexIdentity: Identity,
                Revision: 267),
            ["running"]);

        store.AdvanceContinuousTestFreshWatermark(Workspace, ProjectA(), Key(267), Key(273));

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.Equal(273, watermarks["committed-at-from"].Revision);
        Assert.False(watermarks.ContainsKey("committed-below-from"));
        Assert.False(watermarks.ContainsKey("running"));
        Assert.False(watermarks.ContainsKey("never-run"));
    }

    [Fact]
    public void Advance_fresh_watermark_never_lowers_and_chains_forward()
    {
        using var store = CreateStoreWithProviderCase("test:1", ProjectA());
        CommitGreen(store, "test:1", Identity, 9);
        store.AdvanceContinuousTestFreshWatermark(Workspace, ProjectA(), Key(9), Key(273));
        store.AdvanceContinuousTestFreshWatermark(Workspace, ProjectA(), Key(9), Key(100));
        Assert.Equal(273, store.ListContinuousTestFreshWatermarks(Workspace, Identity)["test:1"].Revision);

        store.AdvanceContinuousTestFreshWatermark(Workspace, ProjectA(), Key(273), Key(281));
        Assert.Equal(281, store.ListContinuousTestFreshWatermarks(Workspace, Identity)["test:1"].Revision);
        Assert.True(ContinuousTestDurableFreshness.IsWatermarkFreshAt(
            new CtFreshnessKey(Identity, 281),
            new CtFreshnessKey(Identity, 281)));
        Assert.False(ContinuousTestDurableFreshness.IsWatermarkFreshAt(
            new CtFreshnessKey(Identity, 9),
            new CtFreshnessKey(Identity, 10)));
    }

    [Fact]
    public void Changed_index_identity_invalidates_stored_freshness()
    {
        using var store = CreateStoreWithProviderCase("test:1", ProjectA());
        CommitGreen(store, "test:1", Identity, 12);
        store.AdvanceContinuousTestFreshWatermark(Workspace, ProjectA(), Key(12), Key(20));

        IReadOnlyDictionary<string, CtFreshnessKey> oldIdentity =
            store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        IReadOnlyDictionary<string, CtFreshnessKey> newIdentity =
            store.ListContinuousTestFreshWatermarks(Workspace, "gen-2");
        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));

        Assert.Equal(new CtFreshnessKey(Identity, 20), oldIdentity["test:1"]);
        Assert.Empty(newIdentity);
        Assert.False(ContinuousTestDurableFreshness.IsCommittedFreshAt(status, new CtFreshnessKey("gen-2", 12)));
        Assert.False(ContinuousTestDurableFreshness.IsWatermarkFreshAt(
            oldIdentity["test:1"],
            new CtFreshnessKey("gen-2", 20)));

        store.AdvanceContinuousTestFreshWatermark(
            Workspace,
            ProjectA(),
            new CtFreshnessKey("gen-2", 12),
            new CtFreshnessKey("gen-2", 20));
        Assert.Empty(store.ListContinuousTestFreshWatermarks(Workspace, "gen-2"));
        Assert.Equal(20, store.ListContinuousTestFreshWatermarks(Workspace, Identity)["test:1"].Revision);
    }

    [Fact]
    public void Mark_stale_invalidates_every_watermark_for_the_case()
    {
        using var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, "test:1", ProjectA());
        SeedProviderCase(store, "test:2", ProjectA());
        CommitGreen(store, "test:1", Identity, 267);
        CommitGreen(store, "test:2", Identity, 267);
        store.AdvanceContinuousTestFreshWatermark(Workspace, ProjectA(), Key(267), Key(273));

        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(274));

        IReadOnlyDictionary<string, CtFreshnessKey> watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.False(watermarks.ContainsKey("test:1"));
        Assert.Equal(273, watermarks["test:2"].Revision);
    }

    [Fact]
    public void Complete_delta_normalizes_paths_and_rejects_unavailable_changes()
    {
        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: Workspace,
            WorkspaceRoot: _dir,
            ProjectPath: Path.Combine(_dir, "proj.csproj"),
            BuildOutputRoot: Path.Combine(_dir, "out"));
        var complete = new ContinuousTestDaemonChange(
            Workspace: workspace,
            CurrentRevision: "12",
            IndexIdentity: Identity,
            ChangedPaths: [@"src\Foo.cs", "/src/Foo.cs", "src/Bar.cs"],
            DeltaCompleteness: ContinuousTestDeltaCompleteness.Complete,
            DeltaFromRevision: 11,
            DeltaToRevision: 12);
        var unavailable = new ContinuousTestDaemonChange(
            Workspace: workspace,
            CurrentRevision: "12",
            IndexIdentity: Identity,
            ChangedPaths: ["src/Foo.cs"]);

        Assert.True(ContinuousTestDurableFreshness.TryGetCompleteDelta(
            complete,
            out long from,
            out long to,
            out IReadOnlyList<string> paths));
        Assert.Equal(11, from);
        Assert.Equal(12, to);
        Assert.Equal(["src/Bar.cs", "src/Foo.cs"], paths);
        Assert.False(ContinuousTestDurableFreshness.TryGetCompleteDelta(unavailable, out _, out _, out _));
    }

    [Fact]
    public void Discovery_failure_is_active_only_for_the_matching_project()
    {
        var cases = new List<ContinuousTestCase>
        {
            new(
                Id: "fail:1",
                WorkspaceId: Workspace,
                Name: "fail",
                QualifiedName: "fail",
                Selector: "fail",
                Source: "ct-project-status",
                Metadata: new Dictionary<string, object?>
                {
                    ["kind"] = "ct-project-discovery-failure",
                    ["ct_project_path"] = ProjectA(),
                }),
        };

        Assert.True(ContinuousTestDurableFreshness.HasActiveDiscoveryFailure(cases, ProjectA()));
        Assert.False(ContinuousTestDurableFreshness.HasActiveDiscoveryFailure(cases, ProjectB()));
    }

    [Fact]
    public void Missing_db_watermark_reads_return_empty()
    {
        using var store = new ContinuousTestStore(DbPath);
        Assert.Empty(store.ListContinuousTestFreshWatermarks(Workspace));
        Assert.False(File.Exists(DbPath));
    }

    private ContinuousTestStore CreateStoreWithProviderCase(string id, string projectPath)
    {
        var store = new ContinuousTestStore(DbPath);
        SeedProviderCase(store, id, projectPath);
        return store;
    }

    private static void SeedProviderCase(ContinuousTestStore store, string id, string projectPath) =>
        store.PutTestCase(new ContinuousTestCase(
            Id: id,
            WorkspaceId: Workspace,
            Name: id.Replace(":", "_", StringComparison.Ordinal),
            QualifiedName: $"Tests.{id.Replace(":", "_", StringComparison.Ordinal)}",
            Selector: $"{id}.selector",
            Source: "ct-provider:dotnet",
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = projectPath }));

    private static void CommitGreen(ContinuousTestStore store, string testCaseId, string identity, long revision)
    {
        string runId = "run:" + testCaseId + ":" + revision;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: Workspace,
                Status: "running",
                SelectedRevision: revision.ToString(),
                IndexIdentity: identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: Workspace,
            TestRunId: runId,
            SelectedRevision: revision.ToString(),
            CurrentRevision: revision.ToString(),
            IndexIdentity: identity,
            Revision: revision,
            Status: "passed",
            Results:
            [
                new ContinuousTestResult(
                    Id: "res:" + testCaseId + ":" + revision,
                    WorkspaceId: Workspace,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: "passed",
                    ResultRevision: revision.ToString(),
                    IndexIdentity: identity,
                    Revision: revision),
            ]));
    }

    private string ProjectA() => Path.GetFullPath(Path.Combine(_dir, "repo", "A.Tests", "A.Tests.csproj"));

    private string ProjectB() => Path.GetFullPath(Path.Combine(_dir, "repo", "B.Tests", "B.Tests.csproj"));

    private static CtFreshnessKey Key(long revision) => new(Identity, revision);
}
