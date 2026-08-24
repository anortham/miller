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
}
