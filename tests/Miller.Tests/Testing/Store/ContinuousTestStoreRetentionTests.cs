using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store;

public sealed class ContinuousTestStoreRetentionTests : IDisposable
{
    private const string Workspace = "ws:retention";
    private const string OtherWorkspace = "ws:other";
    private const string Identity = "gen:retention";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "miller-ct-retention-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public ContinuousTestStoreRetentionTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void Prune_keeps_the_exact_30_day_boundary_and_deletes_older_rows()
    {
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(Case("case:boundary"));
        DateTimeOffset now = At("2026-08-24T12:00:00Z");
        CompleteEmptyRun(store, "run:boundary", 1, now.AddDays(-30));
        CompleteEmptyRun(store, "run:expired", 2, now.AddDays(-30).AddTicks(-1));

        ContinuousTestHistoryPruneResult result = store.PruneContinuousTestHistory(Workspace, now);

        Assert.Equal(2, result.ConsideredRuns);
        Assert.Equal(1, result.ProtectedRuns);
        Assert.Equal(1, result.DeletedRuns);
        Assert.Equal(["run:boundary"], store.ListTestRuns(Workspace).Select(row => row.Id));
        Assert.Empty(store.ListTestResults(Workspace));
    }

    [Fact]
    public void Prune_keeps_the_newest_50_normalized_outcomes_for_each_test()
    {
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(Case("case:history"));
        DateTimeOffset now = At("2026-08-24T12:00:00Z");
        for (int index = 0; index < 51; index++)
            CompleteRun(store, $"run:{index:D2}", "case:history", index, now.AddDays(-60).AddMinutes(index));

        ContinuousTestHistoryPruneResult result = store.PruneContinuousTestHistory(Workspace, now);

        Assert.Equal(51, result.ConsideredResults);
        Assert.Equal(1, result.DeletedResults);
        Assert.Equal(50, store.ListTestResults(Workspace).Count);
        Assert.DoesNotContain(store.ListTestRuns(Workspace), row => row.Id == "run:00");
        Assert.Contains(store.ListTestRuns(Workspace), row => row.Id == "run:50");
    }

    [Fact]
    public void Prune_preserves_active_runs_and_runs_required_by_retained_results()
    {
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(Case("case:active"));
        store.PutTestCase(Case("case:retained"));
        DateTimeOffset now = At("2026-08-24T12:00:00Z");
        store.StartContinuousTestRun(new ContinuousTestRun(
            "run:active", Workspace, "running", "1", Identity, 1, StartedAt: now.AddDays(-90)), ["case:active"]);
        PutArtifact(store, "artifact:active", now.AddDays(-90), "run:active", Path.Combine(_directory, "disabled.csproj"));
        store.LinkContinuousTestRunArtifact(Workspace, "run:active", "artifact:active");
        CompleteRun(store, "run:retained-result", "case:retained", 2, now.AddDays(-60));

        ContinuousTestHistoryPruneResult result = store.PruneContinuousTestHistory(Workspace, now);

        Assert.Equal(2, result.ProtectedRuns);
        Assert.Equal(2, store.ListTestRuns(Workspace).Count);
        Assert.Contains(store.ListTestRuns(Workspace), row => row.Id == "run:active");
        Assert.Contains(store.ListTestRuns(Workspace), row => row.Id == "run:retained-result");
        Assert.Contains(store.ListRunArtifacts(Workspace), row => row.Id == "artifact:active");
        Assert.Equal(
            ContinuousTestState.Running,
            store.ListContinuousTestStatuses(Workspace).Single(row => row.TestCaseId == "case:active").State);
    }

    [Fact]
    public void Prune_preserves_newest_enabled_project_artifact_and_its_named_run()
    {
        using var store = new ContinuousTestStore(_dbPath);
        string project = Path.Combine(_directory, "App.Tests.csproj");
        store.PutContinuousTestProject(new ContinuousTestProject("project:enabled", Workspace, project));
        store.PutTestCase(Case("case:project"));
        DateTimeOffset now = At("2026-08-24T12:00:00Z");
        CompleteEmptyRun(store, "run:old-artifact", 1, now.AddDays(-90));
        CompleteEmptyRun(store, "run:newest-artifact", 2, now.AddDays(-89));
        PutArtifact(store, "artifact:old", now.AddDays(-90), "run:old-artifact", project);
        PutArtifact(store, "artifact:newest", now.AddDays(-89), "run:newest-artifact", project);

        ContinuousTestHistoryPruneResult result = store.PruneContinuousTestHistory(Workspace, now);

        Assert.Equal(1, result.DeletedArtifacts);
        Assert.Equal(["artifact:newest"], store.ListRunArtifacts(Workspace).Select(row => row.Id));
        Assert.Equal(["run:newest-artifact"], store.ListTestRuns(Workspace).Select(row => row.Id));
    }

    [Fact]
    public void Prune_preserves_and_reports_legacy_unlinked_artifacts()
    {
        using var store = new ContinuousTestStore(_dbPath);
        DateTimeOffset now = At("2026-08-24T12:00:00Z");
        PutArtifact(store, "artifact:legacy", now.AddDays(-90), payload: null);

        ContinuousTestHistoryPruneResult result = store.PruneContinuousTestHistory(Workspace, now);

        Assert.Equal(1, result.LegacyUnlinkedArtifacts);
        Assert.Equal(["artifact:legacy"], store.ListRunArtifacts(Workspace).Select(row => row.Id));
    }

    [Fact]
    public void Prune_is_workspace_scoped_and_leaves_states_and_watermarks_unchanged()
    {
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(Case("case:state"));
        store.PutTestCase(Case("case:other", OtherWorkspace));
        CompleteEmptyRun(store, "run:state", 1, At("2026-01-01T00:00:00Z"));
        CompleteEmptyRun(store, "run:other", 1, At("2026-01-01T00:00:00Z"), OtherWorkspace);
        store.MarkContinuousTestsStale(Workspace, ["case:state"], new CtFreshnessKey(Identity, 8));
        Execute("""
            INSERT INTO ct_revision_cursors(workspace_id, index_identity, revision)
            VALUES ('ws:retention', 'gen:retention', 8);
            """);
        string state = Scalar("SELECT state FROM ct_test_states WHERE test_case_id = 'case:state';");
        string watermark = Scalar("SELECT revision FROM ct_revision_cursors WHERE workspace_id = 'ws:retention';");

        ContinuousTestHistoryPruneResult result = store.PruneContinuousTestHistory(Workspace, At("2026-08-24T12:00:00Z"));

        Assert.Equal(1, result.DeletedRuns);
        Assert.Equal(state, Scalar("SELECT state FROM ct_test_states WHERE test_case_id = 'case:state';"));
        Assert.Equal(watermark, Scalar("SELECT revision FROM ct_revision_cursors WHERE workspace_id = 'ws:retention';"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM test_runs WHERE workspace_id = 'ws:other';"));
    }

    [Fact]
    public void Prune_is_idempotent_and_nested_transaction_failure_rolls_back()
    {
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(Case("case:idempotent"));
        CompleteEmptyRun(store, "run:old", 1, At("2026-01-01T00:00:00Z"));
        DateTimeOffset now = At("2026-08-24T12:00:00Z");

        ContinuousTestHistoryPruneResult first = store.PruneContinuousTestHistory(Workspace, now);
        ContinuousTestHistoryPruneResult second = store.PruneContinuousTestHistory(Workspace, now);
        Assert.True(first.DeletedRuns > 0);
        Assert.Equal(0, second.DeletedRuns);
        Assert.Throws<InvalidOperationException>(() => store.Transaction(() =>
        {
            store.PutTestCase(Case("case:rollback"));
            store.PruneContinuousTestHistory(Workspace, now);
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM test_cases WHERE id = 'case:rollback';"));
    }

    [Fact]
    public void Prune_reports_actual_remaining_counts_when_a_running_state_names_no_run()
    {
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(Case("case:counter"));
        store.PutTestCase(Case("case:orphan"));
        CompleteRun(store, "run:counter", "case:counter", 1, At("2026-01-01T00:00:00Z"), resultStatus: "unknown");
        PutArtifact(store, "artifact:counter", At("2026-01-01T00:00:00Z"), "run:counter", "project:counter");
        Execute("""
            PRAGMA foreign_keys=OFF;
            INSERT INTO ct_test_states(
                test_case_id, workspace_id, index_identity, revision, state, running_run_id)
            VALUES ('case:orphan', 'ws:retention', 'gen:retention', 1, 'running', 'run:missing');
            PRAGMA foreign_keys=ON;
            """);

        ContinuousTestHistoryPruneResult result =
            store.PruneContinuousTestHistory(Workspace, At("2026-08-24T12:00:00Z"));

        Assert.Equal(result.ConsideredRuns, result.DeletedRuns + result.ProtectedRuns);
        Assert.Equal(result.ConsideredResults, result.DeletedResults + result.ProtectedResults);
        Assert.Equal(result.ConsideredArtifacts, result.DeletedArtifacts + result.ProtectedArtifacts);
        Assert.Equal(0, result.ProtectedRuns);
        Assert.Equal(0, result.ProtectedResults);
        Assert.Equal(0, result.ProtectedArtifacts);
    }

    private static ContinuousTestCase Case(string id, string workspace = Workspace) =>
        new(id, workspace, id, id, id, Framework: "xunit");

    private static void CompleteRun(
        ContinuousTestStore store,
        string runId,
        string testCaseId,
        long revision,
        DateTimeOffset endedAt,
        string workspace = Workspace,
        string resultStatus = "passed")
    {
        store.StartContinuousTestRun(new ContinuousTestRun(
            runId, workspace, "running", revision.ToString(CultureInfo.InvariantCulture), Identity, revision), [testCaseId]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            workspace,
            runId,
            revision.ToString(CultureInfo.InvariantCulture),
            revision.ToString(CultureInfo.InvariantCulture),
            Identity,
            revision,
            "passed",
            endedAt,
            [new ContinuousTestResult(
                $"result:{runId}",
                workspace,
                testCaseId,
                runId,
                resultStatus,
                revision.ToString(CultureInfo.InvariantCulture),
                Identity,
                revision)]));
    }

    private static void CompleteEmptyRun(
        ContinuousTestStore store,
        string runId,
        long revision,
        DateTimeOffset endedAt,
        string workspace = Workspace)
    {
        store.StartContinuousTestRun(new ContinuousTestRun(
            runId, workspace, "running", revision.ToString(CultureInfo.InvariantCulture), Identity, revision), []);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            workspace,
            runId,
            revision.ToString(CultureInfo.InvariantCulture),
            revision.ToString(CultureInfo.InvariantCulture),
            Identity,
            revision,
            "passed",
            endedAt));
    }

    private static void PutArtifact(
        ContinuousTestStore store,
        string id,
        DateTimeOffset createdAt,
        string? runId = null,
        string? projectPath = null,
        IReadOnlyDictionary<string, object?>? payload = null) =>
        store.PutRunArtifact(new ContinuousTestRunArtifact(
            id,
            Workspace,
            "test_results",
            CreatedAt: createdAt,
            Payload: payload ?? new Dictionary<string, object?>
            {
                ["run_id"] = runId,
                ["project_path"] = projectPath,
            }));

    private string Scalar(string sql)
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
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private int Count(string sql) => int.Parse(Scalar(sql), CultureInfo.InvariantCulture);

    private void Execute(string sql)
    {
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

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
