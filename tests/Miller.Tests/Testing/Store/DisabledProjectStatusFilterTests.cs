using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store;

public sealed class DisabledProjectStatusFilterTests : IDisposable
{
    private const string Workspace = "ws:1";
    private const string Identity = "gen-1";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "miller-ct-disabled-filter-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public DisabledProjectStatusFilterTests()
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
    public void A_disabled_projects_red_case_is_absent_from_statuses_and_both_aggregates()
    {
        using ContinuousTestStore store = CreateStoreWithRedCase(out string project);

        store.PutContinuousTestProject(new ContinuousTestProject("project:a", Workspace, project, Enabled: false));

        Assert.Empty(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal(0, store.AggregateContinuousTestStatuses(Workspace, selectedKey: null).Total);
        ContinuousTestStatusAggregate selected = store.AggregateContinuousTestStatuses(Workspace, Key(1));
        Assert.Equal(0, selected.Total);
        Assert.Equal(0, selected.FreshRed);
        Assert.Equal(1, Convert.ToInt32(Scalar("SELECT COUNT(*) FROM ct_test_states;"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Reenabling_the_project_restores_the_same_rows()
    {
        using ContinuousTestStore store = CreateStoreWithRedCase(out string project);
        store.PutContinuousTestProject(new ContinuousTestProject("project:a", Workspace, project, Enabled: false));

        Assert.Equal(1, store.SetContinuousTestProjectEnabled(Workspace, project, true));

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal("test:red", status.TestCaseId);
        Assert.Equal(ContinuousTestState.Red, status.State);
        Assert.Equal(1, store.AggregateContinuousTestStatuses(Workspace, selectedKey: null).Total);
        ContinuousTestStatusAggregate selected = store.AggregateContinuousTestStatuses(Workspace, Key(1));
        Assert.Equal(1, selected.Total);
        Assert.Equal(1, selected.FreshRed);
    }

    [Fact]
    public void A_case_with_no_project_row_still_counts()
    {
        using ContinuousTestStore store = CreateStoreWithRedCase(out _);

        ContinuousTestStatus status = Assert.Single(store.ListContinuousTestStatuses(Workspace));
        Assert.Equal("test:red", status.TestCaseId);
        Assert.Equal(1, store.AggregateContinuousTestStatuses(Workspace, selectedKey: null).Total);
        Assert.Equal(1, store.AggregateContinuousTestStatuses(Workspace, Key(1)).Total);
    }

    private ContinuousTestStore CreateStoreWithRedCase(out string project)
    {
        project = Path.Combine(_directory, "A.csproj");
        var store = new ContinuousTestStore(_dbPath);
        store.PutTestCase(new ContinuousTestCase(
            Id: "test:red",
            WorkspaceId: Workspace,
            Name: "RedFact",
            QualifiedName: "Suite.RedFact",
            Selector: "Suite.RedFact",
            FilePath: "tests/Suite.cs",
            ContentHash: "blake3:abc",
            SymbolName: "RedFact",
            SymbolPath: "tests/Suite.cs",
            Framework: "xunit",
            Source: "ct-provider:dotnet",
            Metadata: new Dictionary<string, object?> { ["ct_project_path"] = project }));
        store.StartContinuousTestRun(
            new ContinuousTestRun(
                Id: "run:1",
                WorkspaceId: Workspace,
                Status: "running",
                SelectedRevision: "1",
                IndexIdentity: Identity,
                Revision: 1),
            ["test:red"]);
        store.CompleteContinuousTestRun(new ContinuousTestRunCompletion(
            WorkspaceId: Workspace,
            TestRunId: "run:1",
            SelectedRevision: "1",
            CurrentRevision: "1",
            IndexIdentity: Identity,
            Revision: 1,
            Status: "failed",
            EndedAt: null,
            Results:
            [
                new ContinuousTestResult(
                    Id: "result:1",
                    WorkspaceId: Workspace,
                    TestCaseId: "test:red",
                    TestRunId: "run:1",
                    Status: "failed",
                    ResultRevision: "1",
                    IndexIdentity: Identity,
                    Revision: 1),
            ]));
        return store;
    }

    private static CtFreshnessKey Key(long revision) => new(Identity, revision);

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
}
