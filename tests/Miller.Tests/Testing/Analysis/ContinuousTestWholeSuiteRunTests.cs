using Miller.Testing;
using Miller.Tests.Testing.Daemon.Engine;
using Miller.Tests.Testing.Selection;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

/// <summary>
/// A run that covers every known test case is handed to the provider as an EMPTY selection, so the provider
/// runs the whole assembly once under its seeded trait exclusions.
///
/// <para>Both forms express the same run; only the cost differs. A per-case selection becomes one
/// <c>-method</c> pair per id, and Miller's own ~6,000 cases then exceed the command-line limit and split into
/// roughly 50 processes, each paying host startup and discovery again — 6+ minutes for a subset that
/// <c>dotnet test</c> runs in 25 seconds.</para>
///
/// <para>The dangerous half is bookkeeping, not speed. The run must still RECORD that it covered every case,
/// or freshness at the composite <c>(index_identity, revision)</c> key goes quietly wrong and a complete run
/// looks like it selected nothing. So these tests assert what the provider is told AND what the store
/// records.</para>
/// </summary>
public sealed class ContinuousTestWholeSuiteRunTests : IDisposable
{
    private const string WorkspaceId = "ws:whole-suite";
    private const string Identity = "gen-1";

    private readonly string _root =
        Directory.CreateTempSubdirectory("miller-ct-whole-suite-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_root + "-build", recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_whole_suite_run_hands_the_provider_an_empty_selection()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b", "test:c");
        var provider = new RecordingProvider();
        var coordinator = new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1");

        await coordinator.RunSelectedAsync(
            RunRequest(workspace, ["test:a", "test:b", "test:c"], wholeSuite: true),
            TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(provider.Requests).TestCaseIds);
    }

    /// <summary>
    /// The whole-suite form must not cost the run its record of what it covered. The store still has to show
    /// all three cases selected at this key, because "green requires complete results at the selected
    /// composite key" is judged from exactly those rows.
    /// </summary>
    [Fact]
    public async Task A_whole_suite_run_still_records_every_case_it_covered()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b", "test:c");
        var coordinator = new ContinuousTestCoordinator(
            new RecordingProvider(), store, runIdFactory: static () => "run:1");

        await coordinator.RunSelectedAsync(
            RunRequest(workspace, ["test:a", "test:b", "test:c"], wholeSuite: true),
            TestContext.Current.CancellationToken);

        IReadOnlyList<ContinuousTestStatus> statuses = store.ListContinuousTestStatuses(WorkspaceId);
        Assert.Equal(
            new[] { "test:a", "test:b", "test:c" },
            statuses.Select(row => row.TestCaseId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The default is unchanged. A partial selection is still passed through case by case — running a whole
    /// assembly for three tests out of six thousand is the same mistake in the other direction.
    /// </summary>
    [Fact]
    public async Task A_partial_run_still_hands_the_provider_its_case_list()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b", "test:c");
        var provider = new RecordingProvider();
        var coordinator = new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1");

        await coordinator.RunSelectedAsync(
            RunRequest(workspace, ["test:b"], wholeSuite: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(["test:b"], Assert.Single(provider.Requests).TestCaseIds);
    }

    /// <summary>
    /// Contract clause (e): an IMPACT-DERIVED selection that happens to cover every known case
    /// still travels as its explicit id list. Only a workspace-scope request (a real generation
    /// change, an explicit run) may collapse to the whole-suite form — an impacted set that merely
    /// equals the inventory is a coincidence, not an instruction to run the world.
    /// </summary>
    [Fact]
    public async Task An_impact_derived_selection_covering_every_known_case_still_runs_as_an_id_list()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        // The engine selector resolves exactly one impacted test, `test:app`, so seeding only that case makes
        // the selection cover the whole known inventory.
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        var provider = new RecordingProvider();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        queue.Enqueue(EngineTestSupport.Change(workspace, debounce: TimeSpan.Zero));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(["test:app"], Assert.Single(provider.Requests).TestCaseIds);
    }

    /// <summary>
    /// The whole-suite fast path survives where it is legitimate: an explicit workspace-scope run
    /// whose stale set really is the whole inventory. The queue SETS the flag here; the tests above
    /// prove the coordinator honours it.
    /// </summary>
    [Fact]
    public async Task An_explicit_workspace_scope_run_with_everything_stale_is_whole_suite()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:a", "test:b", "test:c");
        var provider = new RecordingProvider
        {
            DiscoverCases = ProviderCases("test:a", "test:b", "test:c"),
        };
        var queue = new ContinuousTestDaemonQueue(
            store,
            SelectorFor(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(provider.Requests).TestCaseIds);
    }

    /// <summary>
    /// Explicit run contract: the run executes exactly the CURRENT stale set. A case committed
    /// fresh at the live key is neither re-marked stale nor re-run, and because the stale set is a
    /// strict subset of the inventory the run travels as an id list, never as a whole suite.
    /// </summary>
    [Fact]
    public async Task An_explicit_run_executes_only_the_stale_set()
    {
        ContinuousTestWorkspace workspace = Workspace();
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        SeedTestCases(store, workspace, "test:fresh", "test:stale");
        CommitGreen(store, "test:fresh", Identity, 2);
        var provider = new RecordingProvider
        {
            DiscoverCases = ProviderCases("test:fresh", "test:stale"),
        };
        var queue = new ContinuousTestDaemonQueue(
            store,
            SelectorFor(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        ContinuousTestDaemonEnqueueResult enqueued = queue.EnqueueExplicit(new ContinuousTestDaemonChange(
            workspace,
            "2",
            Identity,
            WorkspaceScope: true,
            ObservedAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        // The enqueue itself must not destroy the committed-fresh row.
        Assert.DoesNotContain("test:fresh", enqueued.Selection.StaleTestCaseIds);
        Assert.Equal(["test:stale"], Assert.Single(provider.Requests).TestCaseIds);
        ContinuousTestStatus fresh = Assert.Single(
            store.ListContinuousTestStatuses(WorkspaceId),
            row => row.TestCaseId == "test:fresh");
        Assert.Equal(ContinuousTestState.Green, fresh.State);
    }

    /// <summary>
    /// The converse, and the reason the rule is "covers everything" rather than "is a workspace-scope run".
    /// A selection of one case out of two must still travel as that one case; running a whole assembly for it
    /// would be slower than the chunking this change exists to avoid.
    /// </summary>
    [Fact]
    public async Task The_queue_leaves_a_partial_run_alone()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_root);
        using var store = new ContinuousTestStore(CtSchema.DbPathFor(_root));
        // A SECOND known case, in a DIFFERENT test file so the selector does not resolve it. The selection is
        // then a strict subset of the inventory, and the run must travel as its own case list.
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        store.PutTestCase(EngineTestSupport.Case("test:other", workspace.ProjectPath, "tests/OtherTests.cs"));
        var provider = new RecordingProvider();
        var queue = new ContinuousTestDaemonQueue(
            store,
            EngineTestSupport.Selector(store),
            new ContinuousTestCoordinator(provider, store, runIdFactory: static () => "run:1"));

        ContinuousTestDaemonEnqueueResult enqueued =
            queue.Enqueue(EngineTestSupport.Change(workspace, debounce: TimeSpan.Zero));
        await queue.DrainReadyAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        // Guard the premise: this test says nothing unless the selection really is a strict subset. A second
        // case in the SAME test file is selected too, and the run is then correctly a whole-suite one.
        Assert.Equal(["test:app"], enqueued.Selection.SelectedTestCaseIds);

        // The drain also runs the backfill lane for the case that has never run. Neither run covers the whole
        // inventory, so NEITHER may be inverted - an empty selection here would run the whole assembly for a
        // single test.
        Assert.NotEmpty(provider.Requests);
        Assert.All(provider.Requests, request => Assert.NotEmpty(request.TestCaseIds));
        Assert.Contains(provider.Requests, request => request.TestCaseIds.SequenceEqual(["test:app"]));
    }

    private static ContinuousTestCoordinatorRunRequest RunRequest(
        ContinuousTestWorkspace workspace,
        string[] testCaseIds,
        bool wholeSuite) =>
        new(
            Workspace: workspace,
            SelectedRevision: "2",
            CurrentRevision: "2",
            IndexIdentity: Identity,
            TestCaseIds: testCaseIds,
            WholeSuite: wholeSuite);

    private ContinuousTestWorkspace Workspace()
    {
        string project = Path.Combine(_root, "src", "App.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        // The build output root must live OUTSIDE the workspace root; the queue validates it.
        return new ContinuousTestWorkspace(
            WorkspaceId,
            _root,
            project,
            Path.Combine(_root + "-build", "whole-suite"));
    }

    private static ContinuousTestImpactSelector SelectorFor(ContinuousTestStore store) =>
        new(store, new FakeMillerFactSource());

    private static IReadOnlyList<ProviderTestCase> ProviderCases(params string[] ids) =>
        ids
            .Select(id => new ProviderTestCase(
                Id: id,
                DisplayName: id,
                FullyQualifiedName: $"App.Tests.{id}",
                Selector: $"App.Tests.{id}",
                Framework: "xunit",
                SourcePath: "tests/AppTests.cs"))
            .ToArray();

    /// <summary>Commits one green result at <c>(identity, revision)</c> through the real
    /// run-completion path, so the case is committed-fresh at that key.</summary>
    private static void CommitGreen(
        ContinuousTestStore store,
        string testCaseId,
        string identity,
        long revision)
    {
        string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string runId = "seed-run:" + testCaseId;
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: runId,
                WorkspaceId: WorkspaceId,
                Status: "running",
                SelectedRevision: revisionText,
                IndexIdentity: identity,
                Revision: revision),
            [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: WorkspaceId,
            TestRunId: runId,
            SelectedRevision: revisionText,
            CurrentRevision: revisionText,
            IndexIdentity: identity,
            Revision: revision,
            Status: "passed",
            Results:
            [
                new ContinuousTestResult(
                    Id: runId + ":result",
                    WorkspaceId: WorkspaceId,
                    TestCaseId: testCaseId,
                    TestRunId: runId,
                    Status: "passed",
                    ResultRevision: revisionText,
                    IndexIdentity: identity,
                    Revision: revision),
            ]));
    }

    private static void SeedTestCases(
        ContinuousTestStore store,
        ContinuousTestWorkspace workspace,
        params string[] ids)
    {
        foreach (string id in ids)
        {
            store.PutTestCase(new ContinuousTestCase(
                Id: id,
                WorkspaceId: WorkspaceId,
                Name: id,
                QualifiedName: $"App.Tests.{id}",
                Selector: $"App.Tests.{id}",
                FilePath: "tests/AppTests.cs",
                Framework: "xunit",
                Role: ContinuousTestRole.TestCase,
                Source: "ct-provider:dotnet",
                Confidence: 1.0,
                Metadata: new Dictionary<string, object?> { ["ct_project_path"] = workspace.ProjectPath }));
        }
    }

    private sealed class RecordingProvider : IContinuousTestProvider
    {
        public List<ContinuousTestProviderRunRequest> Requests { get; } = [];

        public IReadOnlyList<ProviderTestCase> DiscoverCases { get; init; } = [];

        public Task<IReadOnlyList<ProviderTestCase>> DiscoverAsync(
            ContinuousTestWorkspace workspace,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DiscoverCases);

        public Task<ProviderRunResult> RunAsync(
            ContinuousTestProviderRunRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProviderRunResult(request.RunId ?? "run:1", "passed"));
        }
    }
}
