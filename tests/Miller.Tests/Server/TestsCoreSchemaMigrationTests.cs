using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server.Tools;
using Miller.Testing;
using Xunit;

namespace Miller.Tests.Server;

public sealed class TestsCoreSchemaMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _workspaceId;

    public TestsCoreSchemaMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-schema-migration-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(_root);
        _dbPath = CtSchema.DbPathFor(_root);
        _workspaceId = WorkspaceId.FromCanonicalRoot(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Run_migrates_legacy_store_before_reading_and_preserves_rows()
    {
        SeedLegacySchema();
        bool foregroundCalled = false;

        TestsRunResult result = TestsCore.Run(new TestsCoreRequest(
            WorkspaceRoot: _root,
            WorkspaceId: _workspaceId,
            MillerHome: Path.Combine(_dir, "home"),
            Hooks: new TestsCoreHooks(
                Budget: CtExecutionBudget.Disabled(),
                ForegroundRun: request =>
                {
                    foregroundCalled = true;
                    Assert.Single(request.Projects);
                    return new TestsRunOutcome(
                        CtRunExecution.ForegroundOneShot,
                        ContinuousTestVerdict.Unknown,
                        "hook",
                        false);
                })));

        Assert.True(foregroundCalled);
        Assert.Equal(0, result.ExitCode);
        using var store = new ContinuousTestStore(_dbPath);
        Assert.Single(store.ListTestCases(_workspaceId));
        Assert.Single(store.ListContinuousTestProjects(_workspaceId));
        Assert.Equal(CtSchema.SchemaVersion, ReadSchemaVersion());
    }

    [Fact]
    public void Status_refuses_legacy_store_without_mutating_it()
    {
        SeedLegacySchema();
        byte[] before = File.ReadAllBytes(_dbPath);

        ContinuousTestStoreUnreadableException exception = Assert.Throws<ContinuousTestStoreUnreadableException>(
            () => TestsCore.Status(new TestsCoreRequest(
                WorkspaceRoot: _root,
                WorkspaceId: _workspaceId,
                MillerHome: Path.Combine(_dir, "home"))));

        Assert.Contains("project_path", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_dbPath));
        Assert.Equal(1, ReadSchemaVersion());
    }

    private void SeedLegacySchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=OFF;
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO meta(key, value) VALUES ('schema_version', '1');
            PRAGMA user_version=1;

            CREATE TABLE test_suites (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                name TEXT NOT NULL,
                framework TEXT,
                source TEXT NOT NULL,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                UNIQUE (workspace_id, name, source)
            );

            CREATE TABLE test_cases (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                file_path TEXT,
                content_hash TEXT,
                symbol_name TEXT,
                symbol_path TEXT,
                suite_id TEXT,
                name TEXT NOT NULL,
                qualified_name TEXT NOT NULL,
                selector TEXT NOT NULL,
                framework TEXT,
                role TEXT NOT NULL,
                source TEXT NOT NULL,
                confidence REAL NOT NULL,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                provenance_json TEXT NOT NULL DEFAULT '{}',
                UNIQUE (workspace_id, selector, source)
            );

            CREATE TABLE ct_test_projects (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                project_path TEXT NOT NULL,
                framework TEXT,
                command TEXT,
                enabled INTEGER NOT NULL DEFAULT 1,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                exclude_traits TEXT NOT NULL DEFAULT '[]',
                inventory_stale INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE ct_test_states (
                test_case_id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                index_identity TEXT NOT NULL,
                revision INTEGER NOT NULL,
                state TEXT NOT NULL,
                last_run_revision TEXT,
                stale_since_revision TEXT,
                running_run_id TEXT,
                running_revision TEXT,
                last_result_status TEXT,
                last_result_at TEXT,
                failure_summary TEXT,
                flakiness_score REAL NOT NULL DEFAULT 0.0,
                updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );
            CREATE INDEX idx_ct_test_states_workspace_state
                ON ct_test_states(workspace_id, state, test_case_id);

            INSERT INTO test_cases (
                id, workspace_id, name, qualified_name, selector, role, source,
                confidence, metadata_json, provenance_json
            ) VALUES (
                'case:1', $workspace, 'case:1', 'case:1', 'case:1', 'testcase',
                'ct-provider:xunit', 1.0, $metadata, '{}'
            );

            INSERT INTO ct_test_projects (id, workspace_id, project_path, command)
            VALUES ('project:1', $workspace, $project, 'test');
            """;
        string projectPath = Path.Combine(_root, "Sample.Tests.csproj");
        command.Parameters.AddWithValue("$workspace", _workspaceId);
        command.Parameters.AddWithValue("$project", projectPath);
        command.Parameters.AddWithValue(
            "$metadata",
            "{\"ct_project_path\":\"" + projectPath.Replace("\\", "\\\\") + "\"}");
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private int ReadSchemaVersion()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        return int.Parse(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
    }
}
