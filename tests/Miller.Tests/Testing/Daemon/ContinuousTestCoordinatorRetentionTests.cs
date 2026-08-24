using Microsoft.Data.Sqlite;
using Miller.Testing;
using Miller.Tests.Testing.Daemon.Engine;
using Xunit;

namespace Miller.Tests.Testing.Daemon;

public sealed class ContinuousTestCoordinatorRetentionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "miller-ct-coordinator-retention-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public ContinuousTestCoordinatorRetentionTests()
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
    public async Task Run_maintenance_invokes_history_retention()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_directory);
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        store.PutContinuousTestProject(new ContinuousTestProject(
            "project:app",
            workspace.WorkspaceId,
            workspace.ProjectPath,
            Enabled: false));
        store.PutRunArtifact(new ContinuousTestRunArtifact(
            "artifact:old",
            workspace.WorkspaceId,
            "test_results",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-90),
            Payload: new Dictionary<string, object?>
            {
                ["run_id"] = "run:orphan",
                ["project_path"] = workspace.ProjectPath,
            }));

        var provider = new FakeContinuousTestProvider
        {
            RunResult = new ProviderRunResult(
                "run:live",
                "passed",
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults:
                [
                    new ProviderCaseResult("result:app", "test:app", "passed", "2", EngineTestSupport.Identity),
                ]),
        };
        var coordinator = new ContinuousTestCoordinator(
            provider,
            store,
            runIdFactory: static () => "run:live");

        await coordinator.RunSelectedAsync(new ContinuousTestCoordinatorRunRequest(
            workspace,
            "2",
            "2",
            EngineTestSupport.Identity,
            ["test:app"]), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(store.ListRunArtifacts(workspace.WorkspaceId), row => row.Id == "artifact:old");
    }

    [Fact]
    public async Task Retention_failure_does_not_terminalize_a_successful_run()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_directory);
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        PutOldArtifact(store, workspace);
        AddRetentionDeleteFailureTrigger();
        var diagnostics = new List<string>();
        var provider = SuccessfulProvider();
        var coordinator = new ContinuousTestCoordinator(
            provider,
            store,
            runIdFactory: static () => "run:live",
            onDiagnostic: diagnostics.Add);

        ContinuousTestCoordinatorRunResult result = await coordinator.RunSelectedAsync(
            Request(workspace),
            TestContext.Current.CancellationToken);

        Assert.Equal("passed", result.ProviderResult.Status);
        Assert.Equal("passed", store.ListTestRuns(workspace.WorkspaceId).Single(row => row.Id == "run:live").Status);
        Assert.Contains(store.ListRunArtifacts(workspace.WorkspaceId), row => row.Id == "artifact:old");
        Assert.Contains(diagnostics, row => row.StartsWith("ct_history_prune_failed type=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retention_failure_does_not_replace_the_original_provider_failure()
    {
        ContinuousTestWorkspace workspace = EngineTestSupport.Workspace(_directory);
        using var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(EngineTestSupport.Case("test:app", workspace.ProjectPath));
        PutOldArtifact(store, workspace);
        AddRetentionDeleteFailureTrigger();
        var diagnostics = new List<string>();
        var provider = SuccessfulProvider();
        provider.RunException = new InvalidOperationException("provider original failure");
        var coordinator = new ContinuousTestCoordinator(
            provider,
            store,
            runIdFactory: static () => "run:failed",
            onDiagnostic: diagnostics.Add);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunSelectedAsync(Request(workspace, "run:failed"), TestContext.Current.CancellationToken));

        Assert.Equal("provider original failure", exception.Message);
        Assert.Contains(store.ListRunArtifacts(workspace.WorkspaceId), row => row.Id == "artifact:old");
        Assert.Contains(diagnostics, row => row.StartsWith("ct_history_prune_failed type=", StringComparison.Ordinal));
    }

    private static ContinuousTestCoordinatorRunRequest Request(
        ContinuousTestWorkspace workspace,
        string runId = "run:live") =>
        new(
            workspace,
            "2",
            "2",
            EngineTestSupport.Identity,
            ["test:app"],
            RunId: runId);

    private static FakeContinuousTestProvider SuccessfulProvider() =>
        new()
        {
            RunResult = new ProviderRunResult(
                "run:live",
                "passed",
                EndedAt: DateTimeOffset.UtcNow,
                CaseResults:
                [
                    new ProviderCaseResult("result:app", "test:app", "passed", "2", EngineTestSupport.Identity),
                ]),
        };

    private static void PutOldArtifact(ContinuousTestStore store, ContinuousTestWorkspace workspace) =>
        store.PutRunArtifact(new ContinuousTestRunArtifact(
            "artifact:old",
            workspace.WorkspaceId,
            "test_results",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-90),
            Payload: new Dictionary<string, object?>
            {
                ["run_id"] = "run:orphan",
                ["project_path"] = workspace.ProjectPath,
            }));

    private void AddRetentionDeleteFailureTrigger()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_ct_retention_delete
            BEFORE DELETE ON run_artifacts
            WHEN OLD.id = 'artifact:old'
            BEGIN
                SELECT RAISE(ABORT, 'retention failure');
            END;
            """;
        command.ExecuteNonQuery();
    }
}
