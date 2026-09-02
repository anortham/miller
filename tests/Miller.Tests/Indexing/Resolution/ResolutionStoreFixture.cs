using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Tests.Indexing.Resolution;

internal sealed class ResolutionStoreFixture : IDisposable
{
    private ResolutionStoreFixture(string root, string storePath)
    {
        Root = root;
        StorePath = storePath;
    }

    public string Root { get; }

    public string StorePath { get; }

    public string ViewId { get; } = "view-a";

    public long Generation { get; private set; } = 1;

    public int WriteConnectionOpenCount { get; private set; }

    public static ResolutionStoreFixture Create()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-qtr-facts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string storePath = Path.Combine(root, "store.db");
        ResolutionStoreFixture fixture = new(root, storePath);
        using var connection = fixture.OpenWrite();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
        command.CommandText =
            """
            CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO artifact_metadata VALUES
              ('artifact_id','11111111-1111-4111-8111-111111111111'),
              ('sqlite_schema_version','7'),
              ('extract_contract_version','4'),
              ('hash_algorithm','blake3');
            CREATE TABLE extraction_revisions (revision_id INTEGER);
            INSERT INTO extraction_revisions VALUES (1);
            INSERT INTO store_meta VALUES
              ('family_id','11111111-1111-4111-8111-111111111111'),
              ('store_sqlite_schema_version','2'),
              ('store_format_epoch','1'),
              ('min_reader_version','2.31.0'),
              ('binary_version','2.31.0'),
              ('extraction_identity_epoch','1'),
              ('generation_state','serving');
            INSERT INTO views VALUES
              ('view-a','/tmp/ws',1,'unbound',NULL,NULL,NULL,'2026-08-09T00:00:00Z','2026-08-09T00:00:00Z');
            INSERT INTO manifests VALUES
              ('view-a',1,'manifest-1','request-1','2026-08-09T00:00:00Z');
            """;
        command.ExecuteNonQuery();
        return fixture;
    }

    public StoreVisibility Visibility() =>
        new(
            "11111111-1111-4111-8111-111111111111",
            Root,
            "gen-001",
            StorePath,
            Path.Combine(Root, "coord.db"),
            ViewId,
            "/tmp/ws",
            Generation,
            "manifest-" + Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "unbound",
            null,
            null,
            null,
            Generation,
            "full",
            "2.31.0",
            "store-1",
            "1",
            "2",
            "3");

    public SqliteConnection OpenRead()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StorePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public void ExecuteWrite(string sql)
    {
        using SqliteConnection? ownedConnection = _writeConnection is null ? OpenWrite() : null;
        SqliteConnection connection = _writeConnection ?? ownedConnection!;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = _writeTransaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void WriteTransaction(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_writeTransaction is not null)
            throw new InvalidOperationException("A fixture write transaction is already active.");

        using SqliteConnection connection = OpenWrite();
        using SqliteTransaction transaction = connection.BeginTransaction();
        _writeConnection = connection;
        _writeTransaction = transaction;
        try
        {
            body();
            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); }
            catch (SqliteException) { }
            throw;
        }
        finally
        {
            _writeTransaction = null;
            _writeConnection = null;
        }
    }

    public void AddFile(long versionId, string path, string language = "csharp", string status = "indexed")
    {
        string errorClass = status == "indexed" ? "NULL" : "'parse'";
        string errorJson = status == "indexed" ? "NULL" : "'{}'";
        ExecuteWrite(
            $"""
            INSERT OR REPLACE INTO file_versions
              (version_id,path,content_hash,extraction_epoch,language,content_bytes,line_count,metadata_json,complete_l1,complete_l2,complete_l3)
              VALUES ({versionId},'{Escape(path)}','blake3:{versionId}',1,'{Escape(language)}',10,1,NULL,1,2,3);
            INSERT OR REPLACE INTO manifest_entries
              (view_id,generation,path,language,version_id,status,observed_content_hash,indexed_at,error_class,error_json)
              VALUES ('{ViewId}',{Generation},'{Escape(path)}','{Escape(language)}',{versionId},'{status}','blake3:{versionId}','2026-08-09T00:00:00Z',{errorClass},{errorJson});
            """);
    }

    public void AddFailedPath(string path, string language = "csharp")
    {
        string emptyJson = "{}";
        ExecuteWrite(
            $"""
            INSERT OR REPLACE INTO manifest_entries
              (view_id,generation,path,language,version_id,status,observed_content_hash,indexed_at,error_class,error_json)
              VALUES ('{ViewId}',{Generation},'{Escape(path)}','{Escape(language)}',NULL,'failed','blake3:none','2026-08-09T00:00:00Z','parse','{emptyJson}');
            """);
    }

    public void AddSymbol(
        long versionId,
        string symbolId,
        string name,
        string kind,
        string path,
        string language = "csharp",
        string? parentId = null,
        string? signature = null,
        string? visibility = null,
        string? metadataJson = null)
    {
        string parent = parentId is null ? "NULL" : $"'{Escape(parentId)}'";
        string sig = signature is null ? "NULL" : $"'{Escape(signature)}'";
        string vis = visibility is null ? "NULL" : $"'{Escape(visibility)}'";
        string meta = metadataJson is null ? "NULL" : $"'{Escape(metadataJson)}'";
        ExecuteWrite(
            $"""
            INSERT OR REPLACE INTO symbols (
              version_id,symbol_id,path,language,name,kind,signature,doc_comment,visibility,parent_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,
              body_start_line,body_start_column,body_end_line,body_end_column,body_start_byte,body_end_byte,
              body_hash,semantic_group,confidence,content_type,is_test,test_container,test_lifecycle,metadata_json)
            VALUES (
              {versionId},'{Escape(symbolId)}','{Escape(path)}','{Escape(language)}','{Escape(name)}','{Escape(kind)}',
              {sig},NULL,{vis},{parent},1,1,1,2,0,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,1.0,NULL,0,0,0,{meta});
            """);
    }

    public void AddStructuralFact(
        long versionId,
        string factId,
        string path,
        string patternId,
        string captureName,
        string nodeKind,
        long startByte,
        long endByte,
        string metadataJson,
        string language = "qml")
    {
        ExecuteWrite(
            $"""
            INSERT INTO structural_facts (
              version_id,structural_fact_id,path,language,pattern_id,capture_name,node_kind,containing_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,confidence,metadata_json)
            VALUES (
              {versionId},'{Escape(factId)}','{Escape(path)}','{Escape(language)}','{Escape(patternId)}',
              '{Escape(captureName)}','{Escape(nodeKind)}',NULL,1,1,1,2,{startByte},{endByte},1.0,'{Escape(metadataJson)}');
            """);
    }

    public void AddTypeFact(long versionId, string typeFactId, string symbolId, string resolvedType, bool inferred = false)
    {
        ExecuteWrite(
            $"""
            INSERT OR REPLACE INTO type_facts
              (version_id,type_fact_id,symbol_id,language,resolved_type,generic_params_json,constraints_json,is_inferred,metadata_json)
              VALUES ({versionId},'{Escape(typeFactId)}','{Escape(symbolId)}','csharp','{Escape(resolvedType)}',NULL,NULL,{(inferred ? 1 : 0)},NULL);
            """);
    }

    public void AddIdentifier(
        long versionId,
        string identifierId,
        string name,
        string path,
        string kind = "call",
        string? containingSymbolId = null,
        long startByte = 0,
        long endByte = 3,
        long startLine = 1,
        string? metadataJson = null,
        string language = "csharp",
        double confidence = 1.0,
        string siteProvenance = "target_token",
        bool siteExact = true,
        bool siteSpanless = false,
        long? siteStartColumn = null,
        long? siteEndColumn = null)
    {
        string containing = containingSymbolId is null ? "NULL" : $"'{Escape(containingSymbolId)}'";
        string meta = metadataJson is null ? "NULL" : $"'{Escape(metadataJson)}'";
        ExecuteWrite(
            $"""
            INSERT OR REPLACE INTO identifiers (
              version_id,identifier_id,reference_site_id,path,language,name,kind,containing_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,confidence,code_context,metadata_json)
            VALUES (
              {versionId},'{Escape(identifierId)}','site-{Escape(identifierId)}','{Escape(path)}','{Escape(language)}',
              '{Escape(name)}','{Escape(kind)}',{containing},{startLine},1,{startLine},4,{startByte},{endByte},{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)},NULL,{meta});
            INSERT OR REPLACE INTO reference_sites (
              version_id,reference_site_id,path,language,containing_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,is_exact,provenance)
            VALUES (
              {versionId},'site-{Escape(identifierId)}','{Escape(path)}','{Escape(language)}',{containing},
              {SiteSpan(startLine, startByte, endByte, siteSpanless, siteStartColumn, siteEndColumn)},{(siteExact ? 1 : 0)},'{Escape(siteProvenance)}');
            """);
    }

    public void AddPending(
        long versionId,
        string pendingId,
        string fromSymbolId,
        string name,
        string path,
        string kind = "calls",
        long? startByte = 0,
        long? endByte = 3,
        long startLine = 1,
        string language = "csharp",
        string siteProvenance = "target_token",
        bool siteExact = true,
        bool siteSpanless = false,
        long? siteStartColumn = null,
        long? siteEndColumn = null,
        string? receiver = null,
        string? metadataJson = null)
    {
        string receiverSql = receiver is null ? "NULL" : $"'{Escape(receiver)}'";
        string meta = metadataJson is null ? "NULL" : $"'{Escape(metadataJson)}'";
        string start = startByte is { } sb ? sb.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";
        string end = endByte is { } eb ? eb.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";
        ExecuteWrite(
            $"""
            INSERT OR REPLACE INTO pending_relationships (
              version_id,pending_relationship_id,reference_site_id,from_symbol_id,caller_scope_symbol_id,path,kind,
              target_display_name,target_terminal_name,target_receiver,target_namespace_json,target_import_context,
              start_line,start_column,end_line,end_column,start_byte,end_byte,confidence,metadata_json)
            VALUES (
              {versionId},'{Escape(pendingId)}','site-{Escape(pendingId)}','{Escape(fromSymbolId)}',NULL,'{Escape(path)}',
              '{Escape(kind)}','{Escape(name)}','{Escape(name)}',{receiverSql},'[]',NULL,{startLine},1,{startLine},4,{start},{end},1.0,{meta});
            INSERT OR REPLACE INTO reference_sites (
              version_id,reference_site_id,path,language,containing_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,is_exact,provenance)
            VALUES (
              {versionId},'site-{Escape(pendingId)}','{Escape(path)}','{Escape(language)}','{Escape(fromSymbolId)}',
              {SiteSpan(startLine, startByte, endByte, siteSpanless, siteStartColumn, siteEndColumn)},{(siteExact ? 1 : 0)},'{Escape(siteProvenance)}');
            """);
        _ = language;
    }

    public void AddRelationship(
        long versionId,
        string relationshipId,
        string fromSymbolId,
        string toSymbolId,
        string path,
        string kind = "calls",
        long? startByte = 0,
        long? endByte = 3,
        long startLine = 1)
    {
        string start = startByte is { } sb ? sb.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";
        string end = endByte is { } eb ? eb.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";
        ExecuteWrite(
            $"""
            INSERT OR REPLACE INTO relationships (
              version_id,relationship_id,reference_site_id,from_symbol_id,to_symbol_id,path,kind,
              start_line,start_column,end_line,end_column,start_byte,end_byte,confidence,metadata_json)
            VALUES (
              {versionId},'{Escape(relationshipId)}','site-{Escape(relationshipId)}','{Escape(fromSymbolId)}',
              '{Escape(toSymbolId)}','{Escape(path)}','{Escape(kind)}',{startLine},1,{startLine},4,{start},{end},1.0,NULL);
            INSERT OR REPLACE INTO reference_sites (
              version_id,reference_site_id,path,language,containing_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,is_exact,provenance)
            VALUES (
              {versionId},'site-{Escape(relationshipId)}','{Escape(path)}','csharp','{Escape(fromSymbolId)}',
              {startLine},1,{startLine},4,{start},{end},1,'target_token');
            """);
    }

    public void FlipManifest(long newGeneration, IReadOnlyList<(string Path, long VersionId, string Language, string Status)> entries)
    {
        Generation = newGeneration;
        ExecuteWrite(
            $"""
            INSERT OR REPLACE INTO manifests VALUES ('{ViewId}',{newGeneration},'manifest-{newGeneration}','request-{newGeneration}','2026-08-09T00:00:00Z');
            UPDATE views SET current_generation={newGeneration} WHERE view_id='{ViewId}';
            """);
        foreach ((string path, long versionId, string language, string status) in entries)
        {
            string errorClass = status == "indexed" ? "NULL" : "'parse'";
            string errorJson = status == "indexed" ? "NULL" : "'{}'";
            ExecuteWrite(
                $"""
                INSERT OR REPLACE INTO file_versions
                  (version_id,path,content_hash,extraction_epoch,language,content_bytes,line_count,metadata_json,complete_l1,complete_l2,complete_l3)
                  VALUES ({versionId},'{Escape(path)}','blake3:{versionId}',1,'{Escape(language)}',10,1,NULL,1,2,3);
                INSERT OR REPLACE INTO manifest_entries
                  (view_id,generation,path,language,version_id,status,observed_content_hash,indexed_at,error_class,error_json)
                  VALUES ('{ViewId}',{newGeneration},'{Escape(path)}','{Escape(language)}',{versionId},'{status}','blake3:{versionId}','2026-08-09T00:00:00Z',{errorClass},{errorJson});
                """);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    private SqliteConnection OpenWrite()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StorePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        WriteConnectionOpenCount++;
        return connection;
    }

    private SqliteConnection? _writeConnection;

    private SqliteTransaction? _writeTransaction;

    public void RemoveReferenceSite(string ownerId)
    {
        ExecuteWrite($"DELETE FROM reference_sites WHERE reference_site_id='site-{Escape(ownerId)}';");
    }

    private static string SiteSpan(
        long startLine,
        long? startByte,
        long? endByte,
        bool spanless,
        long? startColumn,
        long? endColumn)
    {
        if (spanless)
            return "NULL,NULL,NULL,NULL,NULL,NULL";
        string start = startByte is { } sb ? sb.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";
        string end = endByte is { } eb ? eb.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL";
        long sc = startColumn ?? 1;
        long ec = endColumn ?? 4;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{startLine},{sc},{startLine},{ec},{start},{end}");
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private const string SchemaSql =
        """
        CREATE TABLE store_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
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
        CREATE TABLE reference_sites (
          version_id INTEGER NOT NULL,
          reference_site_id TEXT NOT NULL,
          path TEXT NOT NULL,
          language TEXT NOT NULL,
          containing_symbol_id TEXT,
          start_line INTEGER,
          start_column INTEGER,
          end_line INTEGER,
          end_column INTEGER,
          start_byte INTEGER,
          end_byte INTEGER,
          is_exact INTEGER NOT NULL,
          provenance TEXT NOT NULL,
          PRIMARY KEY(version_id,reference_site_id)) STRICT;
        CREATE TABLE identifiers (
          version_id INTEGER NOT NULL,
          identifier_id TEXT NOT NULL,
          reference_site_id TEXT NOT NULL,
          path TEXT NOT NULL,
          language TEXT NOT NULL,
          name TEXT NOT NULL,
          kind TEXT NOT NULL,
          containing_symbol_id TEXT,
          start_line INTEGER NOT NULL,
          start_column INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          end_column INTEGER NOT NULL,
          start_byte INTEGER NOT NULL,
          end_byte INTEGER NOT NULL,
          confidence REAL NOT NULL,
          code_context TEXT,
          metadata_json TEXT,
          PRIMARY KEY(version_id,identifier_id)) STRICT;
        CREATE TABLE pending_relationships (
          version_id INTEGER NOT NULL,
          pending_relationship_id TEXT NOT NULL,
          reference_site_id TEXT NOT NULL,
          from_symbol_id TEXT NOT NULL,
          caller_scope_symbol_id TEXT,
          path TEXT NOT NULL,
          kind TEXT NOT NULL,
          target_display_name TEXT NOT NULL,
          target_terminal_name TEXT NOT NULL,
          target_receiver TEXT,
          target_namespace_json TEXT NOT NULL,
          target_import_context TEXT,
          start_line INTEGER NOT NULL,
          start_column INTEGER,
          end_line INTEGER,
          end_column INTEGER,
          start_byte INTEGER,
          end_byte INTEGER,
          confidence REAL NOT NULL,
          metadata_json TEXT,
          PRIMARY KEY(version_id,pending_relationship_id)) STRICT;
        CREATE TABLE relationships (
          version_id INTEGER NOT NULL,
          relationship_id TEXT NOT NULL,
          reference_site_id TEXT NOT NULL,
          from_symbol_id TEXT NOT NULL,
          to_symbol_id TEXT NOT NULL,
          path TEXT NOT NULL,
          kind TEXT NOT NULL,
          start_line INTEGER,
          start_column INTEGER,
          end_line INTEGER,
          end_column INTEGER,
          start_byte INTEGER,
          end_byte INTEGER,
          confidence REAL NOT NULL,
          metadata_json TEXT,
          PRIMARY KEY(version_id,relationship_id)) STRICT;
        CREATE TABLE structural_facts (
          version_id INTEGER NOT NULL,
          structural_fact_id TEXT NOT NULL,
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
          confidence REAL NOT NULL,
          metadata_json TEXT,
          PRIMARY KEY(version_id,structural_fact_id)) STRICT;
        """;
}
