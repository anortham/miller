using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class ContinuousTestStoreApplierTests : IDisposable
{
    private const string Workspace = "ws:1";
    private const string Identity = "gen-1";

    private readonly string _dir;
    private readonly string _dbPath;

    public ContinuousTestStoreApplierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-applier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Store_applier_inserts_discovered_cases_and_commits_fresh_results()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var applier = new ContinuousTestStoreApplier(store);
        var discovered = new ProviderTestCase(
            Id: "test:1",
            DisplayName: "Passes",
            FullyQualifiedName: "Sample.Tests.Passes",
            Selector: "Sample.Tests.Passes",
            Framework: "xunit",
            SourcePath: "tests/SampleTests.cs");

        applier.ApplyDiscovery(Workspace, [discovered]);
        Assert.Equal("Sample.Tests.Passes", store.GetTestCase(Workspace, "test:1")!.QualifiedName);

        applier.StartRun(new ContinuousTestProviderRunStart(
            WorkspaceId: Workspace,
            RunId: "run:1",
            SelectedRevision: "1",
            IndexIdentity: Identity,
            Revision: 1,
            SelectedTestCaseIds: ["test:1"],
            Command: "dotnet test",
            Framework: "xunit",
            StartedAt: DateTimeOffset.Parse("2026-06-14T01:00:00Z")));
        applier.CompleteRun(
            WorkspaceId: Workspace,
            SelectedRevision: "1",
            CurrentRevision: "1",
            IndexIdentity: Identity,
            Revision: 1,
            Result: new ProviderRunResult(
                RunId: "run:1",
                Status: "passed",
                StartedAt: null,
                EndedAt: DateTimeOffset.Parse("2026-06-14T01:00:02Z"),
                CaseResults:
                [
                    new ProviderCaseResult(
                        Id: "result:1",
                        TestCaseId: "test:1",
                        Status: "passed",
                        ResultRevision: "1",
                        IndexIdentity: Identity,
                        DurationSeconds: 0.2),
                ]));

        var status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Green, status.State);
        Assert.Equal("1", status.LastRunRevision);
        Assert.Null(status.StaleSinceRevision);
        Assert.Equal("passed", status.LastResultStatus);
    }

    [Fact]
    public void Apply_discovery_stores_all_cases_or_none()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var applier = new ContinuousTestStoreApplier(store);
        var good = new ProviderTestCase(
            Id: "test:good",
            DisplayName: "Passes",
            FullyQualifiedName: "Sample.Tests.Passes",
            Selector: "Sample.Tests.Passes",
            Framework: "xunit");
        var bad = new ProviderTestCase(
            Id: "test:bad",
            DisplayName: "Breaks",
            FullyQualifiedName: "Sample.Tests.Breaks",
            Selector: "Sample.Tests.Breaks",
            Framework: "xunit",
            Metadata: new Dictionary<string, object?> { ["boom"] = ThrowingSequence() });

        Assert.ThrowsAny<Exception>(() => applier.ApplyDiscovery(Workspace, [good, bad]));

        Assert.Empty(store.ListTestCases(Workspace));
    }

    private static IEnumerable<object?> ThrowingSequence()
    {
        yield return "first";
        throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Store_applier_records_project_path_on_discovered_cases()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var applier = new ContinuousTestStoreApplier(store);
        var projectPath = Path.Combine(_dir, "repo", "tests", "Sample.Tests", "Sample.Tests.csproj");

        applier.ApplyDiscovery(
            Workspace,
            [
                new ProviderTestCase(
                    Id: "test:1",
                    DisplayName: "Passes",
                    FullyQualifiedName: "Sample.Tests.Passes",
                    Selector: "Sample.Tests.Passes",
                    Framework: "xunit"),
            ],
            projectPath);

        Assert.Equal(projectPath, store.GetTestCase(Workspace, "test:1")!.Metadata["ct_project_path"]);
    }

    [Fact]
    public void Store_applier_prunes_old_provider_cases_for_project_on_discovery_refresh()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var applier = new ContinuousTestStoreApplier(store);
        var projectPath = Path.Combine(_dir, "repo", "tests", "Sample.Tests", "Sample.Tests.csproj");
        applier.ApplyDiscovery(
            Workspace,
            [
                new ProviderTestCase(
                    Id: "old:xunit-id",
                    DisplayName: "Passes",
                    FullyQualifiedName: "Sample.Tests.Passes",
                    Selector: "-id old:xunit-id",
                    Framework: "xunit"),
            ],
            projectPath);

        applier.ApplyDiscovery(
            Workspace,
            [
                new ProviderTestCase(
                    Id: "new:xunit-id",
                    DisplayName: "Passes",
                    FullyQualifiedName: "Sample.Tests.Passes",
                    Selector: "-id new:xunit-id",
                    Framework: "xunit"),
            ],
            projectPath);

        Assert.Equal(["new:xunit-id"], store.ListTestCases(Workspace).Select(row => row.Id).ToArray());
    }

    [Fact]
    public void Store_applier_uses_stable_result_ids_when_provider_result_ids_repeat()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var applier = new ContinuousTestStoreApplier(store);
        applier.ApplyDiscovery(
            Workspace,
            [
                new ProviderTestCase(
                    Id: "test:1",
                    DisplayName: "First",
                    FullyQualifiedName: "Sample.Tests.First",
                    Selector: "Sample.Tests.First"),
                new ProviderTestCase(
                    Id: "test:2",
                    DisplayName: "Second",
                    FullyQualifiedName: "Sample.Tests.Second",
                    Selector: "Sample.Tests.Second"),
            ]);
        applier.StartRun(new ContinuousTestProviderRunStart(
            WorkspaceId: Workspace,
            RunId: "run:1",
            SelectedRevision: "1",
            IndexIdentity: Identity,
            Revision: 1,
            SelectedTestCaseIds: ["test:1", "test:2"]));

        applier.CompleteRun(
            WorkspaceId: Workspace,
            SelectedRevision: "1",
            CurrentRevision: "1",
            IndexIdentity: Identity,
            Revision: 1,
            Result: new ProviderRunResult(
                RunId: "run:1",
                Status: "passed",
                StartedAt: null,
                EndedAt: DateTimeOffset.Parse("2026-06-14T01:00:02Z"),
                CaseResults:
                [
                    new ProviderCaseResult(
                        Id: "provider-result:duplicate",
                        TestCaseId: "test:1",
                        Status: "passed",
                        ResultRevision: "1",
                        IndexIdentity: Identity),
                    new ProviderCaseResult(
                        Id: "provider-result:duplicate",
                        TestCaseId: "test:2",
                        Status: "passed",
                        ResultRevision: "1",
                        IndexIdentity: Identity),
                ]));

        var results = store.ListTestResults(Workspace).OrderBy(row => row.TestCaseId, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal(2, results.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ["provider-result:duplicate", "provider-result:duplicate"],
            results.Select(row => (string)row.Metadata["provider_result_id"]!).ToArray());
    }

    [Fact]
    public void Store_applier_preserves_stale_state_when_revision_moves_during_run()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var applier = new ContinuousTestStoreApplier(store);
        applier.ApplyDiscovery(
            Workspace,
            [
                new ProviderTestCase(
                    Id: "test:1",
                    DisplayName: "Fails",
                    FullyQualifiedName: "Sample.Tests.Fails",
                    Selector: "Sample.Tests.Fails"),
            ]);
        applier.StartRun(new ContinuousTestProviderRunStart(
            WorkspaceId: Workspace,
            RunId: "run:1",
            SelectedRevision: "1",
            IndexIdentity: Identity,
            Revision: 1,
            SelectedTestCaseIds: ["test:1"]));
        store.MarkContinuousTestsStale(Workspace, ["test:1"], new CtFreshnessKey(Identity, 2));

        applier.CompleteRun(
            WorkspaceId: Workspace,
            SelectedRevision: "1",
            CurrentRevision: "2",
            IndexIdentity: Identity,
            Revision: 2,
            Result: new ProviderRunResult(
                RunId: "run:1",
                Status: "failed",
                StartedAt: null,
                EndedAt: DateTimeOffset.Parse("2026-06-14T01:00:02Z"),
                CaseResults:
                [
                    new ProviderCaseResult(
                        Id: "result:1",
                        TestCaseId: "test:1",
                        Status: "failed",
                        ResultRevision: "1",
                        IndexIdentity: Identity,
                        FailureSummary: "assert failed\nstack"),
                ]));

        var status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Stale, status.State);
        Assert.Equal("2", status.StaleSinceRevision);
        Assert.Null(status.LastRunRevision);
        Assert.Equal("failed", status.LastResultStatus);
        Assert.Equal("assert failed", status.FailureSummary);
    }

    [Fact]
    public void Store_applier_prunes_old_provider_cases_on_empty_discovery()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var applier = new ContinuousTestStoreApplier(store);
        var projectPath = Path.Combine(_dir, "repo", "tests", "Sample.Tests", "Sample.Tests.csproj");
        applier.ApplyDiscovery(
            Workspace,
            [
                new ProviderTestCase(
                    Id: "old:xunit-id",
                    DisplayName: "Passes",
                    FullyQualifiedName: "Sample.Tests.Passes",
                    Selector: "-id old:xunit-id",
                    Framework: "xunit"),
            ],
            projectPath);

        applier.ApplyDiscovery(Workspace, [], projectPath);

        Assert.Empty(store.ListTestCases(Workspace));
    }

    [Fact]
    public void Store_applier_does_not_prune_when_project_path_is_null()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var applier = new ContinuousTestStoreApplier(store);
        applier.ApplyDiscovery(
            Workspace,
            [
                new ProviderTestCase(
                    Id: "old:xunit-id",
                    DisplayName: "Passes",
                    FullyQualifiedName: "Sample.Tests.Passes",
                    Selector: "-id old:xunit-id",
                    Framework: "xunit"),
            ]);

        applier.ApplyDiscovery(Workspace, [], projectPath: null);

        Assert.Equal(["old:xunit-id"], store.ListTestCases(Workspace).Select(row => row.Id).ToArray());
    }

    [Fact]
    public void Complete_run_updates_only_the_selected_case()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var applier = new ContinuousTestStoreApplier(store);
        var project = ProjectPath("Sample.Tests");
        SeedProviderCase(store, "tc:selected", project);
        SeedProviderCase(store, "tc:unselected-a", project);
        SeedProviderCase(store, "tc:unselected-b", project);
        CommitGreen(store, applier, "tc:selected", 41);
        CommitGreen(store, applier, "tc:unselected-a", 41);
        CommitGreen(store, applier, "tc:unselected-b", 41);
        applier.StartRun(new ContinuousTestProviderRunStart(
            WorkspaceId: Workspace,
            RunId: "run:1",
            SelectedRevision: "42",
            IndexIdentity: Identity,
            Revision: 42,
            SelectedTestCaseIds: ["tc:selected"]));

        applier.CompleteRun(
            WorkspaceId: Workspace,
            SelectedRevision: "42",
            CurrentRevision: "42",
            IndexIdentity: Identity,
            Revision: 42,
            Result: PassResult("run:1", "tc:selected", "42"));

        var statuses = store.ListContinuousTestStatuses(Workspace).ToDictionary(row => row.TestCaseId);
        Assert.Equal(ContinuousTestState.Green, statuses["tc:selected"].State);
        Assert.Equal("42", statuses["tc:selected"].LastRunRevision);
        Assert.Equal("41", statuses["tc:unselected-a"].LastRunRevision);
        Assert.Equal("41", statuses["tc:unselected-b"].LastRunRevision);
        Assert.False(ContinuousTestDurableFreshness.IsCommittedFreshAt(
            statuses["tc:unselected-a"],
            new CtFreshnessKey(Identity, 42)));
        Assert.False(ContinuousTestDurableFreshness.IsCommittedFreshAt(
            statuses["tc:unselected-b"],
            new CtFreshnessKey(Identity, 42)));
        var watermarks = store.ListContinuousTestFreshWatermarks(Workspace, Identity);
        Assert.DoesNotContain("tc:unselected-a", watermarks.Keys);
        Assert.DoesNotContain("tc:unselected-b", watermarks.Keys);
    }

    private string ProjectPath(string name) =>
        Path.GetFullPath(Path.Combine(_dir, "repo", "tests", name, name + ".csproj"));

    private static void SeedProviderCase(ContinuousTestStore store, string id, string projectPath) =>
        store.PutTestCase(new ContinuousTestCase(
            Id: id,
            WorkspaceId: Workspace,
            Name: id.Replace(":", "_", StringComparison.Ordinal),
            QualifiedName: $"Tests.{id.Replace(":", "_", StringComparison.Ordinal)}",
            Selector: $"{id}.selector",
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = projectPath }));

    private static void CommitGreen(ContinuousTestStore store, ContinuousTestStoreApplier applier, string testCaseId, long revision)
    {
        string runId = $"run:green:{testCaseId}:{revision}";
        applier.StartRun(new ContinuousTestProviderRunStart(
            WorkspaceId: Workspace,
            RunId: runId,
            SelectedRevision: revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IndexIdentity: Identity,
            Revision: revision,
            SelectedTestCaseIds: [testCaseId]));
        applier.CompleteRun(
            WorkspaceId: Workspace,
            SelectedRevision: revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CurrentRevision: revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IndexIdentity: Identity,
            Revision: revision,
            Result: PassResult(runId, testCaseId, revision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static ProviderRunResult PassResult(string runId, string testCaseId, string revision) =>
        new(
            RunId: runId,
            Status: "passed",
            StartedAt: null,
            EndedAt: DateTimeOffset.Parse("2026-07-03T01:00:02Z"),
            CaseResults:
            [
                new ProviderCaseResult(
                    Id: $"result:{runId}:{testCaseId}",
                    TestCaseId: testCaseId,
                    Status: "passed",
                    ResultRevision: revision,
                    IndexIdentity: Identity),
            ]);
}

/// <summary>
/// The coordinator's maintenance tail reports two degradations that a run SURVIVES rather than fails on: a
/// build generation directory the reap could not remove, and generation disk over its budget. Both leave the
/// coordinator only through the lifecycle sink, so an unwired sink makes a generation directory held by a
/// surviving test host look exactly like a clean workspace. These tests drive the real maintenance tail and
/// assert on what the sink receives, so removing either report - or dropping the sink at the constructor -
/// turns them red.
/// </summary>
public sealed class ContinuousTestCoordinatorLifecycleLogTests : IDisposable
{
    private const string WorkspaceId = "ws:lifecycle";
    private const string Identity = "gen-1";
    private const string OwnerToken = "owner:lifecycle";

    // 'g' plus twelve lowercase hex characters is what CtGenerationPaths.IsGenerationId accepts.
    private const string GenerationId = "gabcdef012345";

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-lifecycle-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_reap_that_fails_is_reported_through_the_constructor_sink()
    {
        var reported = new List<string>();
        ContinuousTestWorkspace workspace = Workspace("project-a");
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        SeedReapEligibleGeneration(store, workspace);

        var coordinator = new ContinuousTestCoordinator(
            new StubContinuousTestProvider(),
            store,
            runIdFactory: static () => "run:1",
            options: new ContinuousTestCoordinatorOptions
            {
                OwnerToken = OwnerToken,
                // A surviving test host still holds the generation directory, so the rename fails.
                ReapGenerationDirectory = static _ => false,
            },
            onDiagnostic: reported.Add);

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.Equal(
            $"generation_reap_failed root={workspace.BuildOutputRoot} gen={GenerationId}",
            Assert.Single(reported));
    }

    [Fact]
    public async Task A_reap_that_succeeds_reports_nothing()
    {
        var reported = new List<string>();
        ContinuousTestWorkspace workspace = Workspace("project-b");
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        SeedReapEligibleGeneration(store, workspace);

        var coordinator = new ContinuousTestCoordinator(
            new StubContinuousTestProvider(),
            store,
            runIdFactory: static () => "run:1",
            options: new ContinuousTestCoordinatorOptions
            {
                OwnerToken = OwnerToken,
                ReapGenerationDirectory = static _ => true,
            },
            onDiagnostic: reported.Add);

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        // The report names a failure. A sink that receives it on the success path would cry wolf on every run.
        Assert.Empty(reported);
    }

    [Fact]
    public async Task Generation_disk_over_budget_is_reported_through_the_options_sink()
    {
        var reported = new List<string>();
        ContinuousTestWorkspace workspace = Workspace("project-c");
        Directory.CreateDirectory(Path.Combine(workspace.BuildOutputRoot, GenerationId));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);

        var coordinator = new ContinuousTestCoordinator(
            new StubContinuousTestProvider(),
            store,
            runIdFactory: static () => "run:1",
            options: new ContinuousTestCoordinatorOptions
            {
                OwnerToken = OwnerToken,
                GenerationDiskBudgetBytes = 1024,
                MeasureDirectoryBytes = static _ => 4096,
                LifecycleLog = reported.Add,
            });

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.Equal("generation_disk_over_budget bytes=4096 budget=1024", Assert.Single(reported));
    }

    [Fact]
    public async Task Generation_disk_inside_the_budget_reports_nothing()
    {
        var reported = new List<string>();
        ContinuousTestWorkspace workspace = Workspace("project-d");
        Directory.CreateDirectory(Path.Combine(workspace.BuildOutputRoot, GenerationId));
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);

        var coordinator = new ContinuousTestCoordinator(
            new StubContinuousTestProvider(),
            store,
            runIdFactory: static () => "run:1",
            options: new ContinuousTestCoordinatorOptions
            {
                OwnerToken = OwnerToken,
                GenerationDiskBudgetBytes = 8192,
                MeasureDirectoryBytes = static _ => 4096,
                LifecycleLog = reported.Add,
            });

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.Empty(reported);
    }

    [Fact]
    public async Task The_constructor_sink_beats_the_options_sink()
    {
        var fromConstructor = new List<string>();
        var fromOptions = new List<string>();
        ContinuousTestWorkspace workspace = Workspace("project-e");
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        SeedReapEligibleGeneration(store, workspace);

        var coordinator = new ContinuousTestCoordinator(
            new StubContinuousTestProvider(),
            store,
            runIdFactory: static () => "run:1",
            options: new ContinuousTestCoordinatorOptions
            {
                OwnerToken = OwnerToken,
                ReapGenerationDirectory = static _ => false,
                LifecycleLog = fromOptions.Add,
            },
            onDiagnostic: fromConstructor.Add);

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        Assert.Single(fromConstructor);
        Assert.Empty(fromOptions);
    }

    [Fact]
    public async Task A_coordinator_without_a_sink_still_runs_the_maintenance_tail()
    {
        ContinuousTestWorkspace workspace = Workspace("project-f");
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCase(store, workspace);
        SeedReapEligibleGeneration(store, workspace);

        // The production shape before the sink was wired: no sink at either seam. The reap still fails and the
        // debt is still recorded, so the run does not break - the only thing missing is the report, which is
        // what made a held generation directory invisible.
        var coordinator = new ContinuousTestCoordinator(
            new StubContinuousTestProvider(),
            store,
            runIdFactory: static () => "run:1",
            options: new ContinuousTestCoordinatorOptions
            {
                OwnerToken = OwnerToken,
                ReapGenerationDirectory = static _ => false,
            });

        await coordinator.RunSelectedAsync(RunRequest(workspace), TestContext.Current.CancellationToken);

        CtGenerationReapDebtRecord debt = Assert.Single(store.ListCtGenerationReapDebt());
        Assert.Equal(GenerationId, debt.DirectoryName);
        Assert.Equal(workspace.BuildOutputRoot, debt.BuildOutputRoot);
    }

    private ContinuousTestWorkspace Workspace(string buildOutputName)
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new ContinuousTestWorkspace(
            WorkspaceId,
            _root,
            project,
            Path.Combine(_root, "ct-build", buildOutputName));
    }

    private static ContinuousTestCoordinatorRunRequest RunRequest(ContinuousTestWorkspace workspace) =>
        new(
            Workspace: workspace,
            SelectedRevision: "2",
            CurrentRevision: "2",
            IndexIdentity: Identity,
            TestCaseIds: ["test:app"]);

    private static void SeedTestCase(ContinuousTestStore store, ContinuousTestWorkspace workspace) =>
        store.PutTestCase(new ContinuousTestCase(
            Id: "test:app",
            WorkspaceId: WorkspaceId,
            Name: "AppTests",
            QualifiedName: "App.Tests.AppTests",
            Selector: "App.Tests.AppTests",
            FilePath: "tests/AppTests.cs",
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = workspace.ProjectPath }));

    /// <summary>
    /// One generation the maintenance tail must reap: not the active one, and not the newest complete one, so
    /// neither retention rule keeps it.
    /// </summary>
    private static void SeedReapEligibleGeneration(ContinuousTestStore store, ContinuousTestWorkspace workspace)
    {
        store.PutCtGenerationAllocated(new CtGenerationRecord(
            GenerationId: GenerationId,
            BuildOutputRoot: workspace.BuildOutputRoot,
            State: CtGenerationStates.Allocated,
            OwnerToken: OwnerToken,
            AllocatedAt: DateTimeOffset.UtcNow,
            CompletedAt: null));
        Assert.True(store.MarkCtGenerationReapEligible(workspace.BuildOutputRoot, GenerationId, OwnerToken));
    }

    /// <summary>
    /// Verdicts DERIVE from the selection. A stub that claims "passed" while reporting a verdict for
    /// nothing is the shape the coordinator now refuses to record, because a provider that executes no
    /// process looks exactly like it.
    /// </summary>
    private sealed class StubContinuousTestProvider : IContinuousTestProvider
    {
        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderTestCase>>([]);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            string runId = request.RunId ?? "run:1";
            return Task.FromResult(new ProviderRunResult(
                runId,
                "passed",
                CaseResults: request.TestCaseIds
                    .Select(testCaseId => new ProviderCaseResult(
                        Id: $"{runId}:{testCaseId}",
                        TestCaseId: testCaseId,
                        Status: "passed",
                        ResultRevision: request.SelectedRevision,
                        IndexIdentity: request.IndexIdentity))
                    .ToArray()));
        }
    }
}
