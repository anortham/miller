using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class FamilyStoreReadSessionTests
{
    [Fact]
    public void CurrentManifestFiltersRetainedVersionsBeforeReaderQueries()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding, "workspace-a");

        (long Files, long Symbols, string Name) actual = session.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT (SELECT COUNT(*) FROM files), (SELECT COUNT(*) FROM symbols), " +
                "(SELECT name FROM symbols ORDER BY symbol_id LIMIT 1)";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));
        });

        Assert.Equal((1, 1, "Visible"), actual);
        Assert.Equal(WorkspaceReadMode.FamilyStore, session.Snapshot.Mode);
        Assert.Equal("view-a", session.Snapshot.ViewId);
        Assert.Equal("manifest-current", session.Snapshot.Freshness.ManifestHash);
        Assert.Equal(2, session.Snapshot.Freshness.StoreLogSequence);
        Assert.Equal(IndexLevels.FullMetadataValue, session.Snapshot.IndexLevel);
        Assert.Equal("11111111-1111-4111-8111-111111111111:gen-001", session.Snapshot.Freshness.StoreInstanceId);
        Assert.Equal("view-a", session.Snapshot.Freshness.ViewId);
        Assert.Equal("gen-001", session.Snapshot.Freshness.GenerationName);
        Assert.Equal(2, session.Snapshot.Freshness.ManifestGeneration);
        Assert.Equal(IndexLevels.FullMetadataValue, session.Snapshot.Freshness.IndexLevel);
        Assert.NotNull(session.Snapshot.Freshness.LevelStampL1);
        Assert.NotNull(session.Snapshot.Freshness.LevelStampL2);
        Assert.NotNull(session.Snapshot.Freshness.LevelStampL3);
        Assert.NotEqual(
            session.Snapshot.IndexIdentity,
            (session.Snapshot with
            {
                Freshness = session.Snapshot.Freshness with { IndexLevel = IndexLevels.SymbolsMetadataValue },
                IndexLevel = IndexLevels.SymbolsMetadataValue,
            }).IndexIdentity);
    }

    [Fact]
    public void StoreLogCursorIgnoresSiblingViewEventsButTracksSharedVersionChanges()
    {
        using StoreFixture fixture = StoreFixture.Create();

        Assert.Equal(2, ReadStoreLogSequence(fixture));
        AppendStoreLog(fixture, 3, "view-b", versionId: null);
        Assert.Equal(2, ReadStoreLogSequence(fixture));
        AppendStoreLog(fixture, 4, "view-b", versionId: 2);
        Assert.Equal(4, ReadStoreLogSequence(fixture));
        AppendStoreLog(fixture, 5, viewId: null, versionId: null);
        Assert.Equal(5, ReadStoreLogSequence(fixture));
    }

    [Fact]
    public void FamilyMismatchRefusesBeforeOpeningAReadSession()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreFamilyBinding wrong = fixture.Binding with { FamilyId = Guid.Parse("22222222-2222-4222-8222-222222222222") };

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
            FamilyStoreReadSession.Open(wrong));

        Assert.Equal(FamilyStoreReadFailure.FamilyMismatch, error.Failure);
    }

    [Fact]
    public void SessionConnectionIsQueryOnlyAfterProjectionSetup()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        SqliteException error = Assert.Throws<SqliteException>(() => session.Read(connection =>
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO store_log VALUES (3,'bad','bad',NULL,NULL,NULL,NULL,0,'{}','2026-08-09T00:00:02Z')";
            return command.ExecuteNonQuery();
        }));

        Assert.Equal(8, error.SqliteErrorCode);
    }

    [Fact]
    public void PatternFactsReaderUsesTheManifestScopedStoreProjection()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        IReadOnlyList<PatternListRow> rows = new PatternFactsReader().List(
            session,
            patternId: null,
            language: null,
            pathGlob: null,
            metadataFilters: null);

        PatternListRow row = Assert.Single(rows);
        Assert.Equal("visible.pattern.v1", row.PatternId);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public void RevisionDeltaReaderComparesThePriorAndCurrentStoreManifests()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);

        RevisionDeltaResult delta = RevisionDeltaReader.Read(
            session,
            fromRevision: 1,
            fromArtifactId: fixture.Binding.FamilyId.ToString("D"));

        Assert.Equal(RevisionDeltaStatus.Complete, delta.Status);
        Assert.Equal(["same.cs"], delta.ChangedPaths);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(delta.DeletedPaths));
    }

    [Fact]
    public void EnabledWorkspaceFactoryUsesTheValidatedPointerInsteadOfTheLegacyArtifact()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);

        using WorkspaceReadHandle session = WorkspaceReadSessionFactory.Open(
            Path.Combine(fixture.Root, "missing-legacy.db"),
            fixture.Binding.WorkspaceRoot,
            "workspace-a",
            storeEnabled: true);

        Assert.Equal(WorkspaceReadMode.FamilyStore, session.Snapshot.Mode);
        Assert.Equal("view-a", session.Snapshot.ViewId);
    }

    [Fact]
    public void DisabledWorkspaceFactoryRefusesLegacyReadsWhileStorePointerRemains()
    {
        using StoreFixture fixture = StoreFixture.Create();
        StoreWorkspacePointer.Write(fixture.Binding.WorkspaceRoot, fixture.Binding);

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
            WorkspaceReadSessionFactory.Open(
                Path.Combine(fixture.Root, "legacy.db"),
                fixture.Binding.WorkspaceRoot,
                "workspace-a",
                storeEnabled: false));

        Assert.Equal(FamilyStoreReadFailure.BindingNotReady, error.Failure);
    }

    [Fact]
    public void ServingGenerationSymlinkOutsideTheFamilyRootIsRejected()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string generation = Path.Combine(fixture.Binding.StoreRoot, "gen-001");
        string outside = Path.Combine(fixture.Root, "outside-generation");
        Directory.Move(generation, outside);
        Directory.CreateSymbolicLink(generation, outside);

        FamilyStoreReadException error = Assert.Throws<FamilyStoreReadException>(() =>
            FamilyStoreReadSession.Open(fixture.Binding));

        Assert.Equal(FamilyStoreReadFailure.Corrupt, error.Failure);
    }

    private static long ReadStoreLogSequence(StoreFixture fixture)
    {
        using FamilyStoreReadSession session = FamilyStoreReadSession.Open(fixture.Binding);
        return Assert.IsType<long>(session.Snapshot.Freshness.StoreLogSequence);
    }

    private static void AppendStoreLog(StoreFixture fixture, long sequence, string? viewId, long? versionId)
    {
        string databasePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO store_log VALUES ($sequence,$request,'version_level_completed',$view,NULL,$version,2,0,'{}','2026-08-09T00:00:03Z')";
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$request", $"request-{sequence}");
        command.Parameters.AddWithValue("$view", (object?)viewId ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", (object?)versionId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private sealed class StoreFixture : IDisposable
    {
        private StoreFixture(string root, StoreFamilyBinding binding)
        {
            Root = root;
            Binding = binding;
        }

        public string Root { get; }

        public StoreFamilyBinding Binding { get; }

        public static StoreFixture Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "miller-family-read-" + Guid.NewGuid().ToString("N"));
            string workspace = Path.Combine(root, "workspace");
            string store = Path.Combine(root, "store");
            string generation = Path.Combine(store, "gen-001");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(Path.Combine(generation, "bases"));
            Directory.CreateDirectory(Path.Combine(store, "spool"));
            Directory.CreateDirectory(Path.Combine(store, "scratch"));
            File.WriteAllText(Path.Combine(store, "CURRENT"), "gen-001\n");
            CreateCoordinator(Path.Combine(store, "coord.db"));
            workspace = PathCanonicalizer.CanonicalizeRoot(workspace);
            store = PathCanonicalizer.CanonicalizeRoot(store);
            CreateStore(Path.Combine(generation, "store.db"), workspace);
            var binding = new StoreFamilyBinding(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                store,
                "view-a",
                workspace,
                StoreBindingState.Ready);
            return new StoreFixture(root, binding);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static void CreateCoordinator(string path)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE consumer_cursors (consumer_id TEXT PRIMARY KEY, generation_name TEXT NOT NULL, store_log_sequence INTEGER NOT NULL, updated_at INTEGER NOT NULL) STRICT;";
            command.ExecuteNonQuery();
        }

        private static void CreateStore(string path, string workspace)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE store_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
                INSERT INTO store_meta VALUES
                  ('family_id','11111111-1111-4111-8111-111111111111'),
                  ('store_sqlite_schema_version','2'),
                  ('store_format_epoch','1'),
                  ('min_reader_version','2.31.0'),
                  ('binary_version','2.31.0'),
                  ('extraction_identity_epoch','1'),
                  ('generation_state','serving');
                CREATE TABLE views (
                  view_id TEXT PRIMARY KEY,
                  root TEXT NOT NULL,
                  current_generation INTEGER,
                  resolution_state TEXT NOT NULL,
                  resolution_base_id TEXT,
                  resolution_delta_generation INTEGER,
                  resolution_exact_at INTEGER,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL) STRICT;
                CREATE TABLE manifests (
                  view_id TEXT NOT NULL,
                  generation INTEGER NOT NULL,
                  manifest_hash TEXT NOT NULL,
                  request_id TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  PRIMARY KEY(view_id,generation)) STRICT;
                CREATE TABLE file_versions (
                  version_id INTEGER PRIMARY KEY,
                  path TEXT NOT NULL,
                  content_hash TEXT NOT NULL,
                  extraction_epoch INTEGER NOT NULL,
                  language TEXT NOT NULL,
                  content_bytes INTEGER NOT NULL,
                  line_count INTEGER,
                  metadata_json TEXT,
                  complete_l1 INTEGER,
                  complete_l2 INTEGER,
                  complete_l3 INTEGER) STRICT;
                CREATE TABLE manifest_entries (
                  view_id TEXT NOT NULL,
                  generation INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  version_id INTEGER,
                  status TEXT NOT NULL,
                  observed_content_hash TEXT,
                  indexed_at TEXT NOT NULL,
                  error_class TEXT,
                  error_json TEXT,
                  PRIMARY KEY(view_id,generation,path)) STRICT;
                CREATE TABLE symbols (
                  version_id INTEGER NOT NULL,
                  symbol_id TEXT NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  name TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  signature TEXT,
                  doc_comment TEXT,
                  visibility TEXT,
                  parent_symbol_id TEXT,
                  start_line INTEGER NOT NULL,
                  start_column INTEGER NOT NULL,
                  end_line INTEGER NOT NULL,
                  end_column INTEGER NOT NULL,
                  start_byte INTEGER NOT NULL,
                  end_byte INTEGER NOT NULL,
                  body_start_line INTEGER,
                  body_start_column INTEGER,
                  body_end_line INTEGER,
                  body_end_column INTEGER,
                  body_start_byte INTEGER,
                  body_end_byte INTEGER,
                  body_hash TEXT,
                  semantic_group TEXT,
                  confidence REAL,
                  content_type TEXT,
                  is_test INTEGER NOT NULL,
                  test_container INTEGER NOT NULL,
                  test_lifecycle INTEGER NOT NULL,
                  metadata_json TEXT,
                  PRIMARY KEY(version_id,symbol_id)) STRICT;
                CREATE TABLE store_log (
                  sequence INTEGER PRIMARY KEY,
                  request_id TEXT NOT NULL,
                  event_kind TEXT NOT NULL,
                  view_id TEXT,
                  generation INTEGER,
                  version_id INTEGER,
                  level INTEGER,
                  terminal INTEGER NOT NULL,
                  payload_json TEXT NOT NULL,
                  created_at TEXT NOT NULL) STRICT;
                CREATE TABLE structural_facts (
                  structural_fact_id INTEGER PRIMARY KEY,
                  version_id INTEGER NOT NULL,
                  path TEXT NOT NULL,
                  language TEXT NOT NULL,
                  pattern_id TEXT NOT NULL,
                  capture_name TEXT NOT NULL,
                  node_kind TEXT NOT NULL,
                  containing_symbol_id TEXT,
                  start_line INTEGER NOT NULL,
                  start_column INTEGER NOT NULL,
                  end_line INTEGER NOT NULL,
                  end_column INTEGER NOT NULL,
                  start_byte INTEGER NOT NULL,
                  end_byte INTEGER NOT NULL,
                  confidence REAL,
                  metadata_json TEXT) STRICT;
                """;
            command.ExecuteNonQuery();
            command.CommandText =
                """
                INSERT INTO views VALUES ('view-a',$root,2,'unbound',NULL,NULL,NULL,'2026-08-09T00:00:00Z','2026-08-09T00:00:00Z');
                INSERT INTO manifests VALUES
                  ('view-a',1,'manifest-prior','request-prior','2026-08-08T00:00:00Z'),
                  ('view-a',2,'manifest-current','request-a','2026-08-09T00:00:00Z');
                INSERT INTO file_versions VALUES
                  (1,'same.cs','blake3:hidden',1,'csharp',10,1,NULL,1,2,3),
                  (2,'same.cs','blake3:visible',1,'csharp',11,1,NULL,1,2,3);
                INSERT INTO manifest_entries VALUES
                  ('view-a',1,'same.cs','csharp',1,'indexed','blake3:hidden','2026-08-08T00:00:00Z',NULL,NULL),
                  ('view-a',2,'same.cs','csharp',2,'indexed','blake3:visible','2026-08-09T00:00:00Z',NULL,NULL);
                INSERT INTO symbols VALUES
                  (1,'symbol','same.cs','csharp','Hidden','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL),
                  (2,'symbol','same.cs','csharp','Visible','class',NULL,NULL,NULL,NULL,1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,NULL);
                INSERT INTO structural_facts VALUES
                  (1,1,'same.cs','csharp','hidden.pattern.v1','node','class',NULL,1,1,1,2,0,1,1.0,NULL),
                  (2,2,'same.cs','csharp','visible.pattern.v1','node','class',NULL,1,1,1,2,0,1,1.0,NULL);
                INSERT INTO store_log VALUES
                  (1,'request-prior','manifest_flipped','view-a',1,NULL,NULL,0,'{}','2026-08-08T00:00:00Z'),
                  (2,'request-a','store_import_completed','view-a',2,NULL,NULL,1,'{}','2026-08-09T00:00:01Z');
                """;
            command.Parameters.AddWithValue("$root", workspace);
            command.ExecuteNonQuery();
        }
    }
}
