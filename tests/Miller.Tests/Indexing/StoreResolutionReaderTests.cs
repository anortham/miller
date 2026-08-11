using Microsoft.Data.Sqlite;
using Miller.Core.Freshness;
using Miller.Core.References;
using Miller.Indexing;
using Miller.Indexing.Reads;
using System.Text.Json;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreResolutionReaderTests
{
    private const string TargetId = "10000000000000000000000000000001";
    private const string CollisionTargetId = "10000000000000000000000000000002";
    private const string OtherCollisionTargetId = "10000000000000000000000000000003";
    private const string CollisionCallerId = "10000000000000000000000000000004";

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

    [Fact]
    public void ReferenceEvidenceReaderScopesCollidingLocalIdsByVersion()
    {
        using var session = new CollisionStoreReadSession();
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10);

        ReferenceEvidenceSet inbound = ReferenceEvidenceReader.Read(
            session,
            CollisionTargetId,
            bounds);
        OutgoingReferenceEvidenceSet outgoing = ReferenceEvidenceReader.ReadOutgoing(
            session,
            CollisionCallerId,
            bounds);
        ReferenceEvidenceBundle bundle = ReferenceEvidenceReader.ReadForSymbol(
            session,
            CollisionCallerId,
            new ReferenceEvidenceQuery(bounds),
            new ReferenceEvidenceQuery(bounds),
            bounds,
            [ReferenceKind.Call]);

        ReferenceEvidence inboundRow = Assert.Single(inbound.Exact);
        Assert.Equal("src/Visible.cs", inboundRow.FilePath);
        Assert.Empty(inbound.Fallback);
        OutgoingReferenceEvidence outgoingRow = Assert.Single(outgoing.Exact);
        Assert.Equal(CollisionTargetId, outgoingRow.TargetSymbolId);
        Assert.Equal("src/Visible.cs", outgoingRow.FilePath);
        Assert.Empty(outgoing.Fallback);
        Assert.Single(bundle.Outgoing.Exact);
        Assert.Single(bundle.OutgoingKinds[ReferenceKind.Call].Exact);
    }

    [Fact]
    public void FamilyStoreResolutionQueriesUseCompositeIndexes()
    {
        using var session = new CollisionStoreReadSession();

        string[] plan = session.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                EXPLAIN QUERY PLAN
                SELECT b.target_symbol_id
                FROM main.identifiers AS i
                JOIN resolution_base.identifier_resolutions AS b
                  ON b.version_id=i.version_id AND b.identifier_id=i.identifier_id
                WHERE i.containing_symbol_id=$containing
                UNION ALL
                SELECT b.target_symbol_id
                FROM main.pending_relationships AS p
                JOIN resolution_base.pending_resolutions AS b
                  ON b.version_id=p.version_id
                 AND b.pending_relationship_id=p.pending_relationship_id
                WHERE COALESCE(p.caller_scope_symbol_id,p.from_symbol_id)=$containing;
                """;
            command.Parameters.AddWithValue("$containing", CollisionCallerId);
            using var reader = command.ExecuteReader();
            var details = new List<string>();
            while (reader.Read())
                details.Add(reader.GetString(3));
            return details.ToArray();
        });

        Assert.Contains(plan, detail => detail.Contains("SEARCH b", StringComparison.Ordinal)
            && detail.Contains("version_id", StringComparison.Ordinal)
            && detail.Contains("identifier_id", StringComparison.Ordinal));
        Assert.Contains(plan, detail => detail.Contains("SEARCH b", StringComparison.Ordinal)
            && detail.Contains("version_id", StringComparison.Ordinal)
            && detail.Contains("pending_relationship_id", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => detail.StartsWith("SCAN b", StringComparison.Ordinal));
    }

    [Fact]
    public void FamilyStoreReferenceEvidenceMatchesEquivalentLegacyProjection()
    {
        using var familyStore = new CollisionStoreReadSession();
        using var legacy = new CollisionStoreReadSession(familyStore: false);

        Assert.Equal(ReadReferenceGraph(legacy), ReadReferenceGraph(familyStore));
    }

    private static string ReadReferenceGraph(IWorkspaceReadSession session)
    {
        var bounds = new ReferenceEvidenceBounds(ExactLimit: 10, FallbackLimit: 10);
        ReferenceEvidenceQuery query = new(bounds);

        return JsonSerializer.Serialize(new
        {
            Inbound = ReferenceEvidenceReader.Read(session, CollisionTargetId, bounds),
            Outgoing = ReferenceEvidenceReader.ReadOutgoing(session, CollisionCallerId, bounds),
            TargetBundle = ReferenceEvidenceReader.ReadForSymbol(
                session,
                CollisionTargetId,
                query,
                query,
                bounds,
                [ReferenceKind.Call]),
            CallerBundle = ReferenceEvidenceReader.ReadForSymbol(
                session,
                CollisionCallerId,
                query,
                query,
                bounds,
                [ReferenceKind.Call]),
        });
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

    private sealed class CollisionStoreReadSession : IWorkspaceReadSession
    {
        private readonly SqliteConnection _connection;

        public CollisionStoreReadSession(bool familyStore = true)
        {
            _connection = new SqliteConnection("Data Source=:memory:;Pooling=False;Foreign Keys=False");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = $$"""
                ATTACH DATABASE ':memory:' AS resolution_base;
                CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY,value TEXT NOT NULL);
                INSERT INTO artifact_metadata VALUES
                  ('artifact_id','family-a'),('sqlite_schema_version','6'),
                  ('extract_contract_version','4'),('hash_algorithm','blake3');
                CREATE TABLE extraction_revisions (revision_id INTEGER);
                INSERT INTO extraction_revisions VALUES (7);
                CREATE TABLE symbols (version_id INTEGER,symbol_id TEXT,path TEXT,language TEXT,name TEXT,kind TEXT,
                  PRIMARY KEY(version_id,symbol_id));
                CREATE TABLE reference_sites (version_id INTEGER,reference_site_id TEXT,path TEXT,language TEXT,
                  containing_symbol_id TEXT,start_line INTEGER,start_column INTEGER,end_line INTEGER,end_column INTEGER,
                  start_byte INTEGER,end_byte INTEGER,is_exact INTEGER,provenance TEXT,
                  PRIMARY KEY(version_id,reference_site_id));
                CREATE TABLE identifiers (version_id INTEGER,identifier_id TEXT,reference_site_id TEXT,path TEXT,
                  language TEXT,name TEXT,kind TEXT,containing_symbol_id TEXT,start_line INTEGER,start_column INTEGER,
                  end_line INTEGER,end_column INTEGER,start_byte INTEGER,end_byte INTEGER,confidence REAL,
                  PRIMARY KEY(version_id,identifier_id));
                CREATE INDEX identifiers_containing ON identifiers(containing_symbol_id,version_id);
                CREATE TABLE relationships (version_id INTEGER,relationship_id TEXT,reference_site_id TEXT,
                  from_symbol_id TEXT,to_symbol_id TEXT,path TEXT,kind TEXT,confidence REAL,
                  PRIMARY KEY(version_id,relationship_id));
                CREATE TABLE pending_relationships (version_id INTEGER,pending_relationship_id TEXT,
                  reference_site_id TEXT,from_symbol_id TEXT,caller_scope_symbol_id TEXT,path TEXT,kind TEXT,
                  target_display_name TEXT,target_terminal_name TEXT,confidence REAL,
                  PRIMARY KEY(version_id,pending_relationship_id));
                CREATE INDEX pending_containing ON pending_relationships(caller_scope_symbol_id,from_symbol_id,version_id);
                CREATE TABLE resolution_identifier_deltas (view_id TEXT,delta_generation INTEGER,version_id INTEGER,
                  identifier_id TEXT,target_version_id INTEGER,target_symbol_id TEXT,tier INTEGER,confidence REAL,
                  method TEXT,outcome TEXT,candidates INTEGER,
                  PRIMARY KEY(view_id,delta_generation,version_id,identifier_id));
                CREATE INDEX resolution_identifier_delta_target ON resolution_identifier_deltas(
                  target_version_id,target_symbol_id,view_id,delta_generation);
                CREATE TABLE resolution_pending_deltas (view_id TEXT,delta_generation INTEGER,version_id INTEGER,
                  pending_relationship_id TEXT,operation TEXT,target_version_id INTEGER,target_symbol_id TEXT,
                  tier INTEGER,confidence REAL,method TEXT,
                  PRIMARY KEY(view_id,delta_generation,version_id,pending_relationship_id));
                CREATE INDEX resolution_pending_delta_target ON resolution_pending_deltas(
                  target_version_id,target_symbol_id,view_id,delta_generation);
                CREATE TABLE structural_facts (id INTEGER);
                CREATE TABLE language_capability_gaps (id INTEGER);
                CREATE TABLE resolution_base.identifier_resolutions (version_id INTEGER,identifier_id TEXT,
                  target_version_id INTEGER,target_symbol_id TEXT,tier INTEGER,confidence REAL,method TEXT,
                  outcome TEXT,candidates INTEGER,PRIMARY KEY(version_id,identifier_id));
                CREATE INDEX resolution_base.identifier_target ON identifier_resolutions(
                  target_version_id,target_symbol_id,version_id,identifier_id);
                CREATE TABLE resolution_base.pending_resolutions (version_id INTEGER,pending_relationship_id TEXT,
                  target_version_id INTEGER,target_symbol_id TEXT,tier INTEGER,confidence REAL,method TEXT,
                  PRIMARY KEY(version_id,pending_relationship_id));
                CREATE INDEX resolution_base.pending_target ON pending_resolutions(
                  target_version_id,target_symbol_id,version_id,pending_relationship_id);
                INSERT INTO symbols VALUES
                  (1,'{{CollisionTargetId}}','src/VisibleTarget.cs','csharp','Target','method'),
                  (1,'{{CollisionCallerId}}','src/Visible.cs','csharp','Caller','method'),
                  (2,'{{OtherCollisionTargetId}}','src/OtherTarget.cs','csharp','OtherTarget','method'),
                  (2,'10000000000000000000000000000005','src/Other.cs','csharp','OtherCaller','method');
                INSERT INTO reference_sites VALUES
                  (1,'site-1','src/Visible.cs','csharp','{{CollisionCallerId}}',10,2,10,8,100,106,1,'target_token'),
                  (2,'site-1','src/Other.cs','csharp','10000000000000000000000000000005',20,2,20,8,200,206,1,'target_token');
                INSERT INTO identifiers VALUES
                  (1,'identifier-1','site-1','src/Visible.cs','csharp','Target','call','{{CollisionCallerId}}',10,2,10,8,100,106,0.9),
                  (2,'identifier-1','site-1','src/Other.cs','csharp','OtherTarget','call','10000000000000000000000000000005',20,2,20,8,200,206,0.9);
                INSERT INTO resolution_base.identifier_resolutions VALUES
                  (1,'identifier-1',1,'{{CollisionTargetId}}',1,0.9,'exact','resolved',1),
                  (2,'identifier-1',2,'{{OtherCollisionTargetId}}',1,0.9,'exact','resolved',1);
                CREATE TEMP TABLE _miller_visible_entries (version_id INTEGER PRIMARY KEY);
                INSERT INTO _miller_visible_entries VALUES (1),(2);
                CREATE TEMP TABLE _miller_session (generation INTEGER,view_id TEXT,resolution_delta_generation INTEGER);
                INSERT INTO _miller_session VALUES (7,'view-a',0);
                CREATE TEMP VIEW symbols AS
                  SELECT s.* FROM main.symbols AS s
                  JOIN _miller_visible_entries AS v USING(version_id);
                CREATE TEMP VIEW reference_sites AS
                  SELECT r.reference_site_id,r.version_id AS file_id,r.path,r.language,r.containing_symbol_id,
                         r.start_line,r.start_column,r.end_line,r.end_column,r.start_byte,r.end_byte,r.is_exact,r.provenance
                  FROM main.reference_sites AS r
                  JOIN _miller_visible_entries AS v USING(version_id);
                CREATE TEMP VIEW identifiers AS
                  SELECT i.identifier_id,i.reference_site_id,i.version_id AS file_id,i.path,i.language,i.name,i.kind,
                         i.containing_symbol_id,i.start_line,i.start_column,i.end_line,i.end_column,i.start_byte,i.end_byte,
                         i.confidence,NULL AS code_context,NULL AS metadata_json
                  FROM main.identifiers AS i
                  JOIN _miller_visible_entries AS v USING(version_id);
                CREATE TEMP VIEW relationships AS SELECT r.relationship_id,r.reference_site_id,r.from_symbol_id,r.to_symbol_id,
                  r.version_id AS file_id,r.path,r.kind,NULL AS start_line,NULL AS start_column,NULL AS end_line,
                  NULL AS end_column,NULL AS start_byte,NULL AS end_byte,r.confidence,NULL AS metadata_json
                  FROM main.relationships AS r
                  JOIN _miller_visible_entries AS v USING(version_id);
                CREATE TEMP VIEW pending_relationships AS SELECT p.pending_relationship_id,p.reference_site_id,
                  p.from_symbol_id,p.caller_scope_symbol_id,p.version_id AS file_id,p.path,p.kind,p.target_display_name,
                  p.target_terminal_name,NULL AS target_receiver,'[]' AS target_namespace_json,
                  NULL AS target_import_context,NULL AS start_line,NULL AS start_column,NULL AS end_line,
                  NULL AS end_column,NULL AS start_byte,NULL AS end_byte,p.confidence,NULL AS metadata_json
                  FROM main.pending_relationships AS p
                  JOIN _miller_visible_entries AS v USING(version_id);
                CREATE TEMP VIEW identifier_resolutions AS
                  SELECT r.identifier_id,r.target_symbol_id,r.tier,r.confidence,r.method,r.outcome,r.candidates,
                         7 AS resolved_at_revision
                  FROM resolution_base.identifier_resolutions AS r
                  JOIN _miller_visible_entries AS v USING(version_id);
                CREATE TEMP VIEW pending_resolutions AS
                  SELECT r.pending_relationship_id,r.target_symbol_id,r.tier,r.confidence,r.method,
                         7 AS resolved_at_revision
                  FROM resolution_base.pending_resolutions AS r
                  JOIN _miller_visible_entries AS v USING(version_id);
                """;
            command.ExecuteNonQuery();
            if (!familyStore)
            {
                command.CommandText = """
                    DELETE FROM _miller_visible_entries WHERE version_id <> 1;
                    DROP TABLE temp._miller_session;
                    """;
                command.ExecuteNonQuery();
            }
            Snapshot = new WorkspaceReadSnapshot(
                "/workspace",
                "workspace-a",
                "family-a",
                "view-a",
                new WorkspaceFreshnessToken("family-a", 7, StoreInstanceId: "family-a:gen-001"),
                IndexLevels.FullMetadataValue,
                familyStore ? WorkspaceReadMode.FamilyStore : WorkspaceReadMode.LegacyArtifact);
        }

        public WorkspaceReadSnapshot Snapshot { get; }

        public TResult Read<TResult>(Func<SqliteConnection, TResult> query) => query(_connection);

        public void Dispose() => _connection.Dispose();
    }
}
