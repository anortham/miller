using Microsoft.Data.Sqlite;
using Miller.Core.Freshness;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreResolutionReaderTests
{
    private const string TargetId = "10000000000000000000000000000001";

    [Fact]
    public void ReferenceEvidenceReaderAcceptsFamilyStoreResolutionViews()
    {
        using var fixture = Fixture();
        DropResolutionTables(fixture.DbPath);
        using var session = new TempViewReadSession(fixture.DbPath, fixture.WorkspaceRoot);
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10);

        ReferenceEvidenceBundle result = ReferenceEvidenceReader.ReadForSymbol(
            session,
            TargetId,
            new ReferenceEvidenceQuery(bounds),
            new ReferenceEvidenceQuery(bounds),
            bounds,
            [ReferenceKind.Call]);

        Assert.Empty(result.Inbound.Exact);
        Assert.Empty(result.Outgoing.Exact);
    }

    [Fact]
    public void DeadCodeCandidateReaderAcceptsFamilyStoreResolutionViews()
    {
        using var fixture = Fixture();
        DropResolutionTables(fixture.DbPath);
        using var session = new TempViewReadSession(fixture.DbPath, fixture.WorkspaceRoot);

        DeadCodeCandidateReport report = DeadCodeCandidateReader.Read(session, fixture.WorkspaceRoot);

        Assert.NotNull(report);
    }

    private static JulieDbFixture Fixture() => JulieDbFixture.Create(
        JulieDbFixture.PinnedSchema,
        JulieDbFixture.PinnedContract,
        [new(TargetId, "Target", "class", "csharp", "src/Target.cs", "class Target", 1, null)]);

    private static void DropResolutionTables(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE identifier_resolutions;
            DROP TABLE pending_resolutions;
            DROP TABLE pending_relationships;
            DROP TABLE reference_sites;
            """;
        command.ExecuteNonQuery();
    }

    private sealed class TempViewReadSession : IWorkspaceReadSession
    {
        private readonly SqliteConnection _connection;

        public TempViewReadSession(string dbPath, string workspaceRoot)
        {
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                ForeignKeys = false,
            }.ToString());
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                CREATE TEMP VIEW reference_sites AS
                SELECT CAST(NULL AS TEXT) AS reference_site_id, CAST(NULL AS TEXT) AS file_id,
                       CAST(NULL AS TEXT) AS path, CAST(NULL AS TEXT) AS language,
                       CAST(NULL AS TEXT) AS containing_symbol_id, CAST(NULL AS INTEGER) AS start_line,
                       CAST(NULL AS INTEGER) AS start_column, CAST(NULL AS INTEGER) AS end_line,
                       CAST(NULL AS INTEGER) AS end_column, CAST(NULL AS INTEGER) AS start_byte,
                       CAST(NULL AS INTEGER) AS end_byte, CAST(NULL AS INTEGER) AS is_exact,
                       CAST(NULL AS TEXT) AS provenance
                WHERE 0;
                CREATE TEMP VIEW identifier_resolutions AS
                SELECT CAST(NULL AS TEXT) AS identifier_id, CAST(NULL AS TEXT) AS target_symbol_id,
                       CAST(NULL AS INTEGER) AS tier, CAST(NULL AS REAL) AS confidence,
                       CAST(NULL AS TEXT) AS method, CAST(NULL AS TEXT) AS outcome,
                       CAST(NULL AS INTEGER) AS candidates, CAST(NULL AS INTEGER) AS resolved_at_revision
                WHERE 0;
                CREATE TEMP VIEW pending_relationships AS
                SELECT CAST(NULL AS TEXT) AS pending_relationship_id, CAST(NULL AS TEXT) AS reference_site_id,
                       CAST(NULL AS TEXT) AS from_symbol_id, CAST(NULL AS TEXT) AS caller_scope_symbol_id,
                       CAST(NULL AS TEXT) AS file_id, CAST(NULL AS TEXT) AS path,
                       CAST(NULL AS TEXT) AS kind, CAST(NULL AS TEXT) AS target_display_name,
                       CAST(NULL AS TEXT) AS target_terminal_name, CAST(NULL AS TEXT) AS target_receiver,
                       CAST(NULL AS TEXT) AS target_namespace_json, CAST(NULL AS TEXT) AS target_import_context,
                       CAST(NULL AS INTEGER) AS start_line, CAST(NULL AS INTEGER) AS start_column,
                       CAST(NULL AS INTEGER) AS end_line, CAST(NULL AS INTEGER) AS end_column,
                       CAST(NULL AS INTEGER) AS start_byte, CAST(NULL AS INTEGER) AS end_byte,
                       CAST(NULL AS REAL) AS confidence, CAST(NULL AS TEXT) AS metadata_json
                WHERE 0;
                CREATE TEMP VIEW pending_resolutions AS
                SELECT CAST(NULL AS TEXT) AS pending_relationship_id, CAST(NULL AS TEXT) AS target_symbol_id,
                       CAST(NULL AS INTEGER) AS tier, CAST(NULL AS REAL) AS confidence,
                       CAST(NULL AS TEXT) AS method, CAST(NULL AS INTEGER) AS resolved_at_revision
                WHERE 0;
                """;
            command.ExecuteNonQuery();
            Snapshot = new WorkspaceReadSnapshot(
                workspaceRoot,
                "workspace-a",
                "family-a",
                "view-a",
                new WorkspaceFreshnessToken("family-a", 1, StoreInstanceId: "family-a:gen-001"),
                IndexLevels.FullMetadataValue,
                WorkspaceReadMode.FamilyStore);
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => query(_connection);

        public void Dispose() => _connection.Dispose();
    }
}
