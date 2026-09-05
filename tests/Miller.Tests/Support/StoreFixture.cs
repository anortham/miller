using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Indexing.Store;

namespace Miller.Tests.Support;

/// <summary>
/// A real on-disk family store — <c>CURRENT</c>, <c>coord.db</c>, and a <c>gen-001/store.db</c> carrying one view
/// with two manifest generations — written straight to SQLite, so it stays in the fast suite. It is shared
/// because more than one area needs a store that a read session, a freshness probe, or the dashboard can open
/// for real.
/// </summary>
internal sealed class StoreFixture : IDisposable
{
    private readonly StoreReaderRegistrationFixture _reader;

    private StoreFixture(string root, StoreFamilyBinding binding)
    {
        Root = root;
        Binding = binding;
        _reader = new StoreReaderRegistrationFixture(binding);
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
        _reader.Dispose();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    internal ReaderProcessResult ReaderReply(IReadOnlyList<string> args) => _reader.Reply(args);

    private static void CreateCoordinator(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE consumer_cursors (consumer_id TEXT PRIMARY KEY, generation_name TEXT NOT NULL, store_log_sequence INTEGER NOT NULL, updated_at INTEGER NOT NULL) STRICT;";
        command.ExecuteNonQuery();
    }

    private static void CreateStore(string path, string workspace)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
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
            CREATE TABLE source_regions (
              source_region_id TEXT PRIMARY KEY,
              version_id INTEGER NOT NULL,
              path TEXT NOT NULL,
              language TEXT NOT NULL,
              kind TEXT NOT NULL,
              containing_symbol_id TEXT,
              start_line INTEGER NOT NULL,
              start_column INTEGER NOT NULL,
              end_line INTEGER NOT NULL,
              end_column INTEGER NOT NULL,
              start_byte INTEGER NOT NULL,
              end_byte INTEGER NOT NULL,
              metadata_json TEXT) STRICT;
            CREATE TABLE type_facts (
              version_id INTEGER NOT NULL,
              type_fact_id TEXT NOT NULL,
              symbol_id TEXT NOT NULL,
              language TEXT NOT NULL,
              resolved_type TEXT NOT NULL,
              generic_params_json TEXT,
              constraints_json TEXT,
              is_inferred INTEGER NOT NULL,
              metadata_json TEXT,
              PRIMARY KEY(version_id,type_fact_id)) STRICT;
            CREATE TABLE identifiers (
              version_id INTEGER NOT NULL,identifier_id TEXT NOT NULL,reference_site_id TEXT,path TEXT NOT NULL,
              language TEXT NOT NULL,name TEXT NOT NULL,kind TEXT NOT NULL,containing_symbol_id TEXT,
              start_line INTEGER NOT NULL,start_column INTEGER NOT NULL,end_line INTEGER NOT NULL,end_column INTEGER NOT NULL,
              start_byte INTEGER NOT NULL,end_byte INTEGER NOT NULL,confidence REAL,code_context TEXT,metadata_json TEXT,
              PRIMARY KEY(version_id,identifier_id)) STRICT;
            CREATE TABLE pending_relationships (
              version_id INTEGER NOT NULL,pending_relationship_id TEXT NOT NULL,reference_site_id TEXT,
              from_symbol_id TEXT NOT NULL,caller_scope_symbol_id TEXT,path TEXT NOT NULL,kind TEXT NOT NULL,
              target_display_name TEXT NOT NULL,target_terminal_name TEXT NOT NULL,target_receiver TEXT,
              target_namespace_json TEXT,target_import_context TEXT,start_line INTEGER,start_column INTEGER,
              end_line INTEGER,end_column INTEGER,start_byte INTEGER,end_byte INTEGER,confidence REAL,metadata_json TEXT,
              PRIMARY KEY(version_id,pending_relationship_id)) STRICT;
            CREATE TABLE relationships (
              version_id INTEGER NOT NULL,relationship_id TEXT NOT NULL,reference_site_id TEXT,from_symbol_id TEXT NOT NULL,
              to_symbol_id TEXT NOT NULL,path TEXT NOT NULL,kind TEXT NOT NULL,start_line INTEGER,start_column INTEGER,
              end_line INTEGER,end_column INTEGER,start_byte INTEGER,end_byte INTEGER,confidence REAL,metadata_json TEXT,
              PRIMARY KEY(version_id,relationship_id)) STRICT;
            CREATE TABLE reference_sites (
              version_id INTEGER NOT NULL,reference_site_id TEXT NOT NULL,path TEXT NOT NULL,language TEXT NOT NULL,
              containing_symbol_id TEXT,start_line INTEGER,start_column INTEGER,end_line INTEGER,end_column INTEGER,
              start_byte INTEGER,end_byte INTEGER,is_exact INTEGER NOT NULL,provenance TEXT NOT NULL,
              PRIMARY KEY(version_id,reference_site_id)) STRICT;
            CREATE TABLE parse_diagnostics (
              diagnostic_id TEXT PRIMARY KEY,
              version_id INTEGER NOT NULL,
              path TEXT NOT NULL,
              language TEXT NOT NULL,
              kind TEXT NOT NULL,
              message TEXT,
              start_line INTEGER NOT NULL,
              start_column INTEGER NOT NULL,
              end_line INTEGER NOT NULL,
              end_column INTEGER NOT NULL,
              start_byte INTEGER NOT NULL,
              end_byte INTEGER NOT NULL,
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
            INSERT INTO source_regions VALUES
              ('region-same',2,'same.cs','csharp','comment',NULL,1,1,1,2,0,1,NULL);
            INSERT INTO store_log VALUES
              (1,'request-prior','manifest_flipped','view-a',1,NULL,NULL,0,'{}','2026-08-08T00:00:00Z'),
              (2,'request-a','manifest_flipped','view-a',2,NULL,NULL,1,'{}','2026-08-09T00:00:01Z');
            """;
        command.Parameters.AddWithValue("$root", workspace);
        command.ExecuteNonQuery();
    }
}
