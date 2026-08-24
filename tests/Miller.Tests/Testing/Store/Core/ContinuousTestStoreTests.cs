using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store.Core;

public sealed class ContinuousTestStoreTests : IDisposable
{
    private const string Workspace = "ws:1";
    private const string Identity = "gen-1";

    private readonly string _dir;
    private readonly string _dbPath;
    private readonly ITestOutputHelper _output;

    public ContinuousTestStoreTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-store-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Put_and_list_round_trip_a_test_case_with_path_hash_and_name_path_keys()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var metadata = new Dictionary<string, object?> { ["ct_project_path"] = "/tmp/proj.csproj" };
        var provenance = new Dictionary<string, object?> { ["extractor"] = "dotnet" };
        store.PutTestCase(new ContinuousTestCase(
            Id: "test:1",
            WorkspaceId: Workspace,
            Name: "FactOne",
            QualifiedName: "Suite.FactOne",
            Selector: "Suite.FactOne",
            FilePath: "tests/Suite.cs",
            ContentHash: "blake3:abc",
            SymbolName: "FactOne",
            SymbolPath: "tests/Suite.cs",
            Framework: "xunit",
            Role: ContinuousTestRole.ParameterizedTest,
            Source: "ct-provider:dotnet",
            Confidence: 0.75,
            Metadata: metadata,
            Provenance: provenance));

        ContinuousTestCase row = Assert.Single(store.ListTestCases(Workspace));
        Assert.Equal("test:1", row.Id);
        Assert.Equal("tests/Suite.cs", row.FilePath);
        Assert.Equal("blake3:abc", row.ContentHash);
        Assert.Equal("FactOne", row.SymbolName);
        Assert.Equal("tests/Suite.cs", row.SymbolPath);
        Assert.Equal(ContinuousTestRole.ParameterizedTest, row.Role);
        Assert.Equal("ct-provider:dotnet", row.Source);
        Assert.Equal(0.75, row.Confidence);
        Assert.Equal("/tmp/proj.csproj", row.Metadata["ct_project_path"]);
        Assert.Equal("dotnet", row.Provenance["extractor"]);
        Assert.Empty(store.ListTestCases("ws:other"));
    }

    [Fact]
    public void PutTestCase_upserts_by_id()
    {
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(Case("test:1", name: "Old"));
        store.PutTestCase(Case("test:1", name: "New"));

        ContinuousTestCase row = Assert.Single(store.ListTestCases(Workspace));
        Assert.Equal("New", row.Name);
        Assert.Equal("test:1.selector", row.Selector);
    }

    [Fact]
    public void DeleteTestCase_removes_the_case_and_cascades_status_and_results()
    {
        using var store = CreateStoreWithTests("test:1");
        CompleteRun(store, "run:1", "test:1", revision: 1, status: "failed");

        Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(1, store.DeleteTestCase(Workspace, "test:1"));

        Assert.Empty(store.ListTestCases(Workspace));
        Assert.Empty(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(0, store.ScoreContinuousTestFlakiness(Workspace, "test:1").Samples);
        Assert.Equal(0, store.DeleteTestCase(Workspace, "test:1"));
        Assert.Equal(
            0L,
            Convert.ToInt64(Scalar("SELECT COUNT(*) FROM test_results;"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Transaction_rolls_back_on_failure_and_commits_nested_success()
    {
        using var store = new ContinuousTestStore(_dbPath);

        Assert.Throws<InvalidOperationException>(() => store.Transaction(() =>
        {
            store.PutTestCase(Case("test:1"));
            throw new InvalidOperationException("boom");
        }));
        Assert.Empty(store.ListTestCases(Workspace));

        store.Transaction(() =>
        {
            store.PutTestCase(Case("test:a"));
            store.Transaction(() => store.PutTestCase(Case("test:b")));
        });
        Assert.Equal(2, store.ListTestCases(Workspace).Count);
    }

    [Fact]
    public void Mark_continuous_tests_stale_marks_candidates_at_the_composite_key()
    {
        using var store = CreateStoreWithTests("test:1", "test:2");

        store.MarkContinuousTestsStale(Workspace, ["test:1", "test:2"], Key(2));

        IReadOnlyList<ContinuousTestStatus> statuses = store.ListContinuousTestStatuses(Workspace);
        Assert.Collection(
            statuses,
            first =>
            {
                Assert.Equal("test:1", first.TestCaseId);
                Assert.Equal(ContinuousTestState.Stale, first.State);
                Assert.Equal("2", first.StaleSinceRevision);
                Assert.Equal(Identity, first.IndexIdentity);
                Assert.Equal(2, first.Revision);
                Assert.Null(first.ProvenFreshKey);
            },
            second =>
            {
                Assert.Equal("test:2", second.TestCaseId);
                Assert.Equal(ContinuousTestState.Stale, second.State);
                Assert.Equal("2", second.StaleSinceRevision);
            });

        store.MarkContinuousTestsStale(Workspace, ["test:2"], Key(3));
        Assert.Equal("3", store.ListContinuousTestStatuses(Workspace).Single(row => row.TestCaseId == "test:2")
            .StaleSinceRevision);

        store.MarkContinuousTestsStale(Workspace, [], Key(4));
        Assert.Equal(2, store.ListContinuousTestStatuses(Workspace).Count);
    }

    [Fact]
    public void Mark_stale_on_live_running_case_preserves_markers_and_records_earliest_revision()
    {
        using var store = CreateStoreWithTests("test:1");
        store.StartContinuousTestRun(Running("run:1", 1), ["test:1"]);

        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(2));

        ContinuousTestStatus live = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Running, live.State);
        Assert.Equal("run:1", live.RunningRunId);
        Assert.Equal("1", live.RunningRevision);
        Assert.Equal("2", live.StaleSinceRevision);
        Assert.Equal(Identity, live.IndexIdentity);
        Assert.Equal(1, live.Revision);

        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(3));
        ContinuousTestStatus stillLive = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Running, stillLive.State);
        Assert.Equal("run:1", stillLive.RunningRunId);
        Assert.Equal("1", stillLive.RunningRevision);
        Assert.Equal("2", stillLive.StaleSinceRevision);
    }

    [Fact]
    public void Mark_stale_heals_dead_running_marker_to_stale()
    {
        using var store = CreateStoreWithTests("test:1");
        store.StartContinuousTestRun(Running("run:1", 1), ["test:1"]);
        Exec("UPDATE test_runs SET status = 'interrupted' WHERE id = 'run:1';");

        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(2));

        ContinuousTestStatus healed = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Stale, healed.State);
        Assert.Equal("2", healed.StaleSinceRevision);
        Assert.Equal(Identity, healed.IndexIdentity);
        Assert.Equal(2, healed.Revision);
        Assert.Null(healed.RunningRunId);
        Assert.Null(healed.RunningRevision);
    }

    [Fact]
    public void Mark_stale_while_live_then_same_revision_completion_commits_fresh()
    {
        using var store = CreateStoreWithTests("test:1");
        store.StartContinuousTestRun(Running("run:1", 1), ["test:1"]);
        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(1));

        store.CompleteContinuousTestRun(Completion("run:1", 1, 1, Result("result:1", "test:1", "run:1", 1, "passed")));

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Green, status.State);
        Assert.Equal("1", status.LastRunRevision);
        Assert.Null(status.StaleSinceRevision);
        Assert.Null(status.RunningRunId);
        Assert.Equal(new CtFreshnessKey(Identity, 1), status.ProvenFreshKey);
    }

    [Fact]
    public void Mark_stale_while_live_then_moved_revision_completion_preserves_stale()
    {
        using var store = CreateStoreWithTests("test:1");
        store.StartContinuousTestRun(Running("run:1", 1), ["test:1"]);
        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(2));

        store.CompleteContinuousTestRun(Completion(
            "run:1",
            selected: 1,
            current: 2,
            Result("result:1", "test:1", "run:1", 1, "failed")));

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Stale, status.State);
        Assert.Equal("2", status.StaleSinceRevision);
        Assert.Null(status.RunningRunId);
        Assert.Equal("failed", status.LastResultStatus);
        Assert.Null(status.ProvenFreshKey);
    }

    [Fact]
    public void Completing_selected_run_does_not_clear_unselected_stale_tests()
    {
        using var store = CreateStoreWithTests("test:selected", "test:unselected-a", "test:unselected-b");
        store.MarkContinuousTestsStale(
            Workspace,
            ["test:selected", "test:unselected-a", "test:unselected-b"],
            Key(42));
        store.StartContinuousTestRun(
            Running("run:1", 42) with { Command = "dotnet test", Framework = "xunit" },
            ["test:selected"]);

        store.CompleteContinuousTestRun(Completion(
            "run:1",
            42,
            42,
            Result("result:1", "test:selected", "run:1", 42, "passed")));

        Dictionary<string, ContinuousTestStatus> statuses =
            store.ListContinuousTestStatuses(Workspace).ToDictionary(row => row.TestCaseId);
        Assert.Equal(ContinuousTestState.Green, statuses["test:selected"].State);
        Assert.Equal("42", statuses["test:selected"].LastRunRevision);
        Assert.Equal(new CtFreshnessKey(Identity, 42), statuses["test:selected"].ProvenFreshKey);
        Assert.Equal(ContinuousTestState.Stale, statuses["test:unselected-a"].State);
        Assert.Equal("42", statuses["test:unselected-a"].StaleSinceRevision);
        Assert.Equal(ContinuousTestState.Stale, statuses["test:unselected-b"].State);
        Assert.Equal(
            "dotnet test",
            Convert.ToString(Scalar("SELECT command FROM test_runs WHERE id = 'run:1';"), CultureInfo.InvariantCulture));
        Assert.Equal(
            Identity,
            Convert.ToString(
                Scalar("SELECT index_identity FROM test_runs WHERE id = 'run:1';"),
                CultureInfo.InvariantCulture));
        Assert.Equal(
            42L,
            Convert.ToInt64(Scalar("SELECT revision FROM test_runs WHERE id = 'run:1';"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Completing_run_marks_selected_cases_missing_from_results_stale()
    {
        using var store = CreateStoreWithTests("test:1", "test:2");
        store.MarkContinuousTestsStale(Workspace, ["test:1", "test:2"], Key(2));
        store.StartContinuousTestRun(Running("run:1", 2), ["test:1", "test:2"]);

        store.CompleteContinuousTestRun(Completion(
            "run:1",
            2,
            2,
            Result("result:1", "test:1", "run:1", 2, "passed")));

        Dictionary<string, ContinuousTestStatus> statuses =
            store.ListContinuousTestStatuses(Workspace).ToDictionary(row => row.TestCaseId);
        Assert.Equal(ContinuousTestState.Green, statuses["test:1"].State);
        Assert.Equal(ContinuousTestState.Stale, statuses["test:2"].State);
        Assert.Equal("2", statuses["test:2"].StaleSinceRevision);
        Assert.Null(statuses["test:2"].RunningRunId);
        Assert.Null(statuses["test:2"].LastRunRevision);
    }

    [Fact]
    public void Completing_stale_revision_records_history_without_clearing_freshness()
    {
        using var store = CreateStoreWithTests("test:1");
        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(1));
        store.StartContinuousTestRun(Running("run:1", 1), ["test:1"]);
        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(2));

        store.CompleteContinuousTestRun(Completion(
            "run:1",
            selected: 1,
            current: 2,
            Result("result:1", "test:1", "run:1", 1, "failed", failureSummary: "expected true\nfull stack trace")));

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Stale, status.State);
        Assert.Equal("2", status.StaleSinceRevision);
        Assert.Null(status.LastRunRevision);
        Assert.Equal("failed", status.LastResultStatus);
        Assert.Equal("expected true", status.FailureSummary);
        Assert.Equal(
            "1",
            Convert.ToString(
                Scalar("SELECT result_revision FROM test_results WHERE id = 'result:1';"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Completing_current_revision_red_stores_failure_summary()
    {
        using var store = CreateStoreWithTests("test:1");
        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(3));
        store.StartContinuousTestRun(Running("run:1", 3), ["test:1"]);

        store.CompleteContinuousTestRun(Completion(
            "run:1",
            3,
            3,
            Result("result:1", "test:1", "run:1", 3, "failed", failureSummary: "assert failed\nstack")));

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Red, status.State);
        Assert.Equal("3", status.LastRunRevision);
        Assert.Null(status.StaleSinceRevision);
        Assert.Equal("assert failed", status.FailureSummary);
        Assert.Equal(new CtFreshnessKey(Identity, 3), status.ProvenFreshKey);
    }

    [Fact]
    public void Completing_run_coalesces_duplicate_results_for_selected_case()
    {
        using var store = CreateStoreWithTests("test:1");
        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(4));
        store.StartContinuousTestRun(Running("run:1", 4), ["test:1"]);

        store.CompleteContinuousTestRun(Completion(
            "run:1",
            4,
            4,
            Result("result:1", "test:1", "run:1", 4, "passed"),
            Result("result:2", "test:1", "run:1", 4, "passed", durationSeconds: 0.25)));

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Green, status.State);
        Assert.Equal(
            1L,
            Convert.ToInt64(Scalar("SELECT COUNT(*) FROM test_results;"), CultureInfo.InvariantCulture));
        Assert.Equal(
            "result:2",
            Convert.ToString(Scalar("SELECT id FROM test_results;"), CultureInfo.InvariantCulture));
        Assert.Equal(
            0.25,
            Convert.ToDouble(Scalar("SELECT duration_seconds FROM test_results;"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Completing_runs_recomputes_flakiness_score_from_history()
    {
        using var store = CreateStoreWithTests("test:1");
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-14T15:00:00Z", CultureInfo.InvariantCulture);

        CompleteRun(store, "run:1", "test:1", 1, "passed", now.AddMinutes(1));
        CompleteRun(store, "run:2", "test:1", 2, "failed", now.AddMinutes(2));
        CompleteRun(store, "run:3", "test:1", 3, "passed", now.AddMinutes(3));
        CompleteRun(store, "run:4", "test:1", 4, "failed", now.AddMinutes(4));

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Red, status.State);
        Assert.Equal(0.5, status.FlakinessScore);
        ContinuousTestFlakinessScore score = store.ScoreContinuousTestFlakiness(Workspace, "test:1");
        Assert.Equal(0.5, score.FailureRate);
        Assert.Equal(ContinuousTestFlakinessState.Flaky, score.State);
    }

    [Fact]
    public void Skipped_only_history_keeps_flakiness_score_zero()
    {
        using var store = CreateStoreWithTests("test:1");
        DateTimeOffset now = DateTimeOffset.Parse("2026-06-14T15:00:00Z", CultureInfo.InvariantCulture);
        CompleteRun(store, "run:1", "test:1", 1, "skipped", now.AddMinutes(1));
        CompleteRun(store, "run:2", "test:1", 2, "skipped", now.AddMinutes(2));

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(ContinuousTestState.Skipped, status.State);
        Assert.Equal(0.0, status.FlakinessScore);
    }

    [Fact]
    public void Link_continuous_test_run_artifact_sets_artifact_id_once()
    {
        using var store = CreateStoreWithTests("test:1");
        store.StartContinuousTestRun(Running("run:1", 1), ["test:1"]);
        store.PutRunArtifact(new ContinuousTestRunArtifact(
            Id: "artifact:1",
            WorkspaceId: Workspace,
            Kind: "test_results",
            Path: "TestResults/run-1.junit.xml"));

        store.LinkContinuousTestRunArtifact(Workspace, "run:1", "artifact:1");
        Assert.Equal(
            "artifact:1",
            Convert.ToString(
                Scalar("SELECT artifact_id FROM test_runs WHERE id = 'run:1';"),
                CultureInfo.InvariantCulture));

        store.PutRunArtifact(new ContinuousTestRunArtifact(
            Id: "artifact:2",
            WorkspaceId: Workspace,
            Kind: "test_results",
            Path: "TestResults/run-2.junit.xml"));
        store.LinkContinuousTestRunArtifact(Workspace, "run:1", "artifact:2");
        Assert.Equal(
            "artifact:1",
            Convert.ToString(
                Scalar("SELECT artifact_id FROM test_runs WHERE id = 'run:1';"),
                CultureInfo.InvariantCulture));
        Assert.Equal(
            "TestResults/run-1.junit.xml",
            Convert.ToString(
                Scalar("SELECT path FROM run_artifacts WHERE id = 'artifact:1';"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Mark_continuous_tests_stale_invalidates_the_fresh_watermark_for_that_identity()
    {
        using var store = CreateStoreWithTests("test:1", "test:2");
        CompleteRun(store, "run:1", "test:1", 1, "passed");
        CompleteRun(store, "run:2", "test:2", 1, "passed");
        Exec("""
            INSERT INTO ct_case_fresh_watermarks(test_case_id, workspace_id, index_identity, revision)
            VALUES ('test:1', 'ws:1', 'gen-1', 7),
                   ('test:2', 'ws:1', 'gen-1', 7),
                   ('test:1', 'ws:1', 'gen-old', 99);
            """);

        store.MarkContinuousTestsStale(Workspace, ["test:1"], Key(8));

        Assert.Equal(
            0L,
            Convert.ToInt64(
                Scalar("SELECT COUNT(*) FROM ct_case_fresh_watermarks WHERE test_case_id = 'test:1';"),
                CultureInfo.InvariantCulture));
        Assert.Equal(
            7L,
            Convert.ToInt64(
                Scalar("SELECT revision FROM ct_case_fresh_watermarks WHERE test_case_id = 'test:2';"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Aggregate_continuous_test_statuses_matches_detailed_projection_for_selected_and_missing_cursors()
    {
        using var store = CreateAggregateFixture();
        CtFreshnessKey selected = Key(58);
        IReadOnlyList<ContinuousTestStatus> statuses = store.ListContinuousTestStatuses(Workspace);
        IReadOnlyDictionary<string, CtFreshnessKey> watermarks =
            store.ListContinuousTestFreshWatermarks(Workspace, Identity);

        ContinuousTestProjectedStatus detailed =
            ContinuousTestStatusProjection.Project(selected, statuses, watermarks);
        ContinuousTestStatusAggregate aggregate = store.AggregateContinuousTestStatuses(Workspace, selected);
        ContinuousTestProjectedStatus projected = ContinuousTestStatusProjection.Project(selected, aggregate);

        Assert.Equal(detailed, projected);
        Assert.Equal(9, aggregate.Total);
        Assert.Equal(2, aggregate.Pending);
        Assert.Equal(3, aggregate.Stale);
        Assert.Equal(1, aggregate.FreshRed);

        ContinuousTestStatusAggregate missingCursor =
            store.AggregateContinuousTestStatuses(Workspace, selectedKey: null);
        ContinuousTestProjectedStatus missingProjection =
            ContinuousTestStatusProjection.Project(liveKey: null, statuses);
        Assert.Equal(missingProjection, ContinuousTestStatusProjection.Project(null, missingCursor));
        Assert.Equal(1, missingCursor.Stale);
    }

    [Fact]
    public void Aggregate_selected_key_keeps_an_exact_key_stale_row_stale()
    {
        using var store = CreateStoreWithTests("test:stale-exact");
        Exec("""
            INSERT INTO ct_test_states(test_case_id, workspace_id, index_identity, revision, state)
            VALUES ('test:stale-exact', 'ws:1', 'gen-1', 58, 'stale');
            """);
        CtFreshnessKey selected = Key(58);
        IReadOnlyList<ContinuousTestStatus> statuses = store.ListContinuousTestStatuses(Workspace);
        ContinuousTestProjectedStatus detailed =
            ContinuousTestStatusProjection.Project(selected, statuses);
        ContinuousTestStatusAggregate aggregate = store.AggregateContinuousTestStatuses(Workspace, selected);
        ContinuousTestProjectedStatus projected =
            ContinuousTestStatusProjection.Project(selected, aggregate, watchHealthy: true);

        Assert.Equal(detailed, projected);
        Assert.Equal(1, aggregate.Stale);
        Assert.Equal(ContinuousTestVerdict.Partial, projected.Verdict);
    }

    [Fact]
    public void Aggregate_continuous_test_statuses_query_uses_existing_indexes_without_temp_sort()
    {
        using var store = CreateAggregateFixture();
        string[] selectedPlan = ExplainAggregateQuery(Key(58));
        string[] noCursorPlan = ExplainAggregateQuery(selected: null);
        _output.WriteLine($"selected={string.Join(" | ", selectedPlan)}");
        _output.WriteLine($"no_cursor={string.Join(" | ", noCursorPlan)}");

        Assert.Contains(selectedPlan, line => line.Contains("idx_ct_test_states_", StringComparison.Ordinal));
        Assert.Contains(noCursorPlan, line => line.Contains("idx_ct_test_states_workspace_state", StringComparison.Ordinal));
        Assert.DoesNotContain(
            selectedPlan.Concat(noCursorPlan),
            line => line.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Aggregate_continuous_test_statuses_allocates_materially_less_than_detailed_rows()
    {
        using var store = CreateSyntheticAggregateFixture(256);
        for (int i = 0; i < 4; i++)
        {
            _ = store.ListContinuousTestStatuses(Workspace);
            _ = store.AggregateContinuousTestStatuses(Workspace, Key(58));
        }

        long detailedAllocations = Allocations(
            () => store.ListContinuousTestStatuses(Workspace));
        long aggregateAllocations = Allocations(
            () => store.AggregateContinuousTestStatuses(Workspace, Key(58)));
        _output.WriteLine($"detailed_allocations={detailedAllocations} aggregate_allocations={aggregateAllocations}");

        Assert.True(
            aggregateAllocations * 5 < detailedAllocations * 4,
            $"aggregate allocations {aggregateAllocations} were not materially below detailed {detailedAllocations}");
    }

    [Fact]
    public void Store_never_exposes_a_sqlite_connection()
    {
        string[] names = typeof(ContinuousTestStore)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();
        Assert.DoesNotContain("Conn", names);
        Assert.DoesNotContain("Connection", names);
        Assert.Contains(names, name => name.Contains("Coverage", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("Generation", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("Watermark", StringComparison.Ordinal));
    }

    /// <summary>
    /// One xUnit <c>-method</c> selector runs every data row of a theory, so many cases legitimately
    /// share one selector. <c>test_cases</c> used to declare <c>UNIQUE (workspace_id, selector, source)</c>,
    /// so the second row raised SQLite error 19. Discovery stores the whole inventory in ONE transaction,
    /// so that one collision rolled back every case, not just the colliding one.
    /// </summary>
    [Fact]
    public void One_transaction_stores_every_case_of_a_theory_that_shares_one_selector()
    {
        const string selector = "-method Suite.Theory";
        using var store = new ContinuousTestStore(_dbPath);

        store.Transaction(() =>
        {
            store.PutTestCase(Case("test:row-1") with { Selector = selector });
            store.PutTestCase(Case("test:row-2") with { Selector = selector });
            store.PutTestCase(Case("test:row-3") with { Selector = selector });
            store.PutTestCase(Case("test:solo"));
        });

        IReadOnlyList<ContinuousTestCase> rows = store.ListTestCases(Workspace);
        Assert.Equal(4, rows.Count);
        Assert.Equal(
            ["test:row-1", "test:row-2", "test:row-3", "test:solo"],
            rows.Select(row => row.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(3, rows.Count(row => string.Equals(row.Selector, selector, StringComparison.Ordinal)));
    }

    private ContinuousTestStore CreateStoreWithTests(params string[] testCaseIds)
    {
        var store = new ContinuousTestStore(_dbPath);
        foreach (string testCaseId in testCaseIds)
            store.PutTestCase(Case(testCaseId));
        return store;
    }

    private ContinuousTestStore CreateAggregateFixture()
    {
        var store = CreateStoreWithTests(
            "test:exact-green",
            "test:watermark-green",
            "test:wrong-watermark-green",
            "test:exact-red",
            "test:watermark-red",
            "test:exact-skipped",
            "test:stale",
            "test:running",
            "test:unknown");
        Exec("""
            INSERT INTO ct_test_states(test_case_id, workspace_id, index_identity, revision, state)
            VALUES ('test:exact-green', 'ws:1', 'gen-1', 58, 'green'),
                   ('test:watermark-green', 'ws:1', 'gen-1', 41, 'green'),
                   ('test:wrong-watermark-green', 'ws:1', 'gen-1', 41, 'green'),
                   ('test:exact-red', 'ws:1', 'gen-1', 58, 'red'),
                   ('test:watermark-red', 'ws:1', 'gen-1', 41, 'red'),
                   ('test:exact-skipped', 'ws:1', 'gen-1', 58, 'skipped'),
                   ('test:stale', 'ws:1', 'gen-1', 41, 'stale'),
                   ('test:running', 'ws:1', 'gen-1', 58, 'running'),
                   ('test:unknown', 'ws:1', 'gen-1', 58, 'unknown');
            INSERT INTO ct_case_fresh_watermarks(test_case_id, workspace_id, index_identity, revision)
            VALUES ('test:watermark-green', 'ws:1', 'gen-1', 59),
                   ('test:wrong-watermark-green', 'ws:1', 'gen-2', 99),
                   ('test:watermark-red', 'ws:1', 'gen-1', 59);
            """);
        return store;
    }

    private ContinuousTestStore CreateSyntheticAggregateFixture(int count)
    {
        string[] ids = Enumerable.Range(0, count)
            .Select(index => $"test:synthetic-{index:D3}")
            .ToArray();
        ContinuousTestStore store = CreateStoreWithTests(ids);
        string rows = string.Join(
            ",\n",
            ids.Select((id, index) =>
                $"('{id}', 'ws:1', 'gen-1', 58, '{(index % 3 == 0 ? "red" : "green")}')"));
        Exec($"INSERT INTO ct_test_states(test_case_id, workspace_id, index_identity, revision, state) VALUES {rows};");
        return store;
    }

    private string[] ExplainAggregateQuery(CtFreshnessKey? selected)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN\n"
            + (selected is null
                ? ContinuousTestStore.AggregateContinuousTestStatusesNoCursorSql
                : ContinuousTestStore.AggregateContinuousTestStatusesSelectedSql);
        command.Parameters.AddWithValue("$workspace", Workspace);
        if (selected is { } key)
        {
            command.Parameters.AddWithValue("$identity", key.IndexIdentity);
            command.Parameters.AddWithValue("$revision", key.Revision);
        }
        using SqliteDataReader reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
            plan.Add(reader.GetString(3));
        return plan.ToArray();
    }

    private static long Allocations(Action body)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++)
            body();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void CompleteRun(
        ContinuousTestStore store,
        string runId,
        string testCaseId,
        long revision,
        string status,
        DateTimeOffset? endedAt = null)
    {
        store.StartContinuousTestRun(Running(runId, revision), [testCaseId]);
        store.CompleteContinuousTestRun(Completion(
            runId,
            revision,
            revision,
            endedAt,
            Result(runId + ":" + testCaseId, testCaseId, runId, revision, status)));
    }

    private static ContinuousTestCase Case(string id, string? name = null) =>
        new(
            Id: id,
            WorkspaceId: Workspace,
            Name: name ?? id,
            QualifiedName: name ?? id,
            Selector: id + ".selector",
            FilePath: "tests/Suite.cs",
            ContentHash: "blake3:abc",
            SymbolName: id,
            SymbolPath: "tests/Suite.cs",
            Framework: "xunit");

    private static CtFreshnessKey Key(long revision) => new(Identity, revision);

    /// <summary>
    /// A terminal run always has an end time. The xUnit parser assigns one only from the
    /// <c>test-assembly-finished</c> event, and a stall kill truncates the child's output before it, so a
    /// killed run stored <c>status=failed</c> beside <c>ended_at=NULL</c> — a row that read as a run still
    /// going, long after the process tree was gone. Observed live in .miller/ct.db on
    /// <c>ct-run:a37c9ec6df1d442785b671caeadfa026</c>.
    /// </summary>
    [Fact]
    public void Completing_a_run_with_no_provider_end_time_still_stamps_ended_at()
    {
        using var store = CreateStoreWithTests("test:1");
        store.StartContinuousTestRun(Running("run:1", 1), ["test:1"]);
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        store.CompleteContinuousTestRun(
            Completion("run:1", 1, 1, Result("result:1", "test:1", "run:1", 1, "failed")));

        ContinuousTestRun row = Assert.Single(store.ListTestRuns(Workspace));
        Assert.Equal("failed", row.Status);
        Assert.NotNull(row.EndedAt);
        Assert.True(row.EndedAt >= before, $"stamped {row.EndedAt:O}, which predates the call at {before:O}");
    }

    [Fact]
    public void A_provider_end_time_wins_over_the_stamped_fallback()
    {
        using var store = CreateStoreWithTests("test:1");
        store.StartContinuousTestRun(Running("run:1", 1), ["test:1"]);
        var reported = new DateTimeOffset(2026, 6, 14, 1, 0, 2, TimeSpan.Zero);

        store.CompleteContinuousTestRun(
            Completion("run:1", 1, 1, reported, Result("result:1", "test:1", "run:1", 1, "passed")));

        Assert.Equal(reported, Assert.Single(store.ListTestRuns(Workspace)).EndedAt);
    }

    private static ContinuousTestRun Running(string id, long revision) =>
        new(
            Id: id,
            WorkspaceId: Workspace,
            Status: "running",
            SelectedRevision: Rev(revision),
            IndexIdentity: Identity,
            Revision: revision);

    private static ContinuousTestRunCompletion Completion(
        string runId,
        long selected,
        long current,
        params ContinuousTestResult[] results) =>
        Completion(runId, selected, current, endedAt: null, results);

    private static ContinuousTestRunCompletion Completion(
        string runId,
        long selected,
        long current,
        DateTimeOffset? endedAt,
        params ContinuousTestResult[] results) =>
        new(
            WorkspaceId: Workspace,
            TestRunId: runId,
            SelectedRevision: Rev(selected),
            CurrentRevision: Rev(current),
            IndexIdentity: Identity,
            Revision: current,
            Status: results.Any(result => result.Status is "failed" or "error") ? "failed" : "passed",
            EndedAt: endedAt,
            Results: results);

    private static ContinuousTestResult Result(
        string id,
        string testCaseId,
        string runId,
        long revision,
        string status,
        string? failureSummary = null,
        double? durationSeconds = null) =>
        new(
            Id: id,
            WorkspaceId: Workspace,
            TestCaseId: testCaseId,
            TestRunId: runId,
            Status: status,
            ResultRevision: Rev(revision),
            IndexIdentity: Identity,
            Revision: revision,
            DurationSeconds: durationSeconds,
            FailureSummary: failureSummary);

    private static string Rev(long revision) => revision.ToString(CultureInfo.InvariantCulture);

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private void Exec(string sql)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
