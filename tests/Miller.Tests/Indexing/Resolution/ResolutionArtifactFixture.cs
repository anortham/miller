using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Tests.Indexing.Resolution;

internal sealed class ResolutionArtifactFixture : IDisposable
{
    private ResolutionArtifactFixture(string root, string dbPath)
    {
        Root = root;
        DbPath = dbPath;
    }

    public string Root { get; }

    public string DbPath { get; }

    public static ResolutionArtifactFixture Create()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-qtr-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string dbPath = Path.Combine(root, "symbols.db");
        using var connection = OpenWrite(dbPath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE files (
              file_id TEXT PRIMARY KEY,
              path TEXT NOT NULL UNIQUE,
              language TEXT NOT NULL,
              content_hash TEXT NOT NULL,
              content_bytes INTEGER NOT NULL,
              line_count INTEGER,
              indexed_at TEXT NOT NULL,
              last_revision_id INTEGER NOT NULL,
              status TEXT NOT NULL,
              metadata_json TEXT);
            CREATE TABLE symbols (
              symbol_id TEXT PRIMARY KEY,
              file_id TEXT NOT NULL,
              path TEXT NOT NULL,
              language TEXT NOT NULL,
              name TEXT NOT NULL,
              kind TEXT NOT NULL,
              signature TEXT,
              doc_comment TEXT,
              visibility TEXT,
              parent_symbol_id TEXT,
              start_line INTEGER, start_column INTEGER, end_line INTEGER, end_column INTEGER,
              start_byte INTEGER, end_byte INTEGER,
              body_start_line INTEGER, body_start_column INTEGER, body_end_line INTEGER, body_end_column INTEGER,
              body_start_byte INTEGER, body_end_byte INTEGER, body_hash TEXT,
              semantic_group TEXT, confidence REAL, content_type TEXT,
              is_test INTEGER NOT NULL DEFAULT 0, test_container INTEGER NOT NULL DEFAULT 0,
              test_lifecycle INTEGER NOT NULL DEFAULT 0, metadata_json TEXT);
            CREATE TABLE type_facts (
              type_fact_id TEXT PRIMARY KEY,
              symbol_id TEXT NOT NULL,
              language TEXT NOT NULL,
              resolved_type TEXT NOT NULL,
              generic_params_json TEXT,
              constraints_json TEXT,
              is_inferred INTEGER NOT NULL,
              metadata_json TEXT);
            CREATE TABLE reference_sites (
              reference_site_id TEXT PRIMARY KEY,
              file_id TEXT NOT NULL,
              path TEXT NOT NULL,
              language TEXT NOT NULL,
              containing_symbol_id TEXT,
              start_line INTEGER, start_column INTEGER, end_line INTEGER, end_column INTEGER,
              start_byte INTEGER, end_byte INTEGER,
              is_exact INTEGER NOT NULL,
              provenance TEXT NOT NULL);
            CREATE TABLE identifiers (
              identifier_id TEXT PRIMARY KEY,
              reference_site_id TEXT NOT NULL,
              file_id TEXT NOT NULL,
              path TEXT NOT NULL,
              language TEXT NOT NULL,
              name TEXT NOT NULL,
              kind TEXT NOT NULL,
              containing_symbol_id TEXT,
              start_line INTEGER, start_column INTEGER, end_line INTEGER, end_column INTEGER,
              start_byte INTEGER, end_byte INTEGER,
              confidence REAL NOT NULL DEFAULT 1.0,
              code_context TEXT,
              metadata_json TEXT);
            CREATE TABLE pending_relationships (
              pending_relationship_id TEXT PRIMARY KEY,
              reference_site_id TEXT NOT NULL,
              from_symbol_id TEXT NOT NULL,
              caller_scope_symbol_id TEXT,
              file_id TEXT NOT NULL,
              path TEXT NOT NULL,
              kind TEXT NOT NULL,
              target_display_name TEXT NOT NULL,
              target_terminal_name TEXT NOT NULL,
              target_receiver TEXT,
              target_namespace_json TEXT NOT NULL,
              target_import_context TEXT,
              start_line INTEGER NOT NULL,
              start_column INTEGER, end_line INTEGER, end_column INTEGER,
              start_byte INTEGER, end_byte INTEGER,
              confidence REAL NOT NULL,
              metadata_json TEXT);
            CREATE TABLE relationships (
              relationship_id TEXT PRIMARY KEY,
              reference_site_id TEXT NOT NULL,
              from_symbol_id TEXT NOT NULL,
              to_symbol_id TEXT NOT NULL,
              file_id TEXT NOT NULL,
              path TEXT NOT NULL,
              kind TEXT NOT NULL,
              start_line INTEGER, start_column INTEGER, end_line INTEGER, end_column INTEGER,
              start_byte INTEGER, end_byte INTEGER,
              confidence REAL NOT NULL,
              metadata_json TEXT);
            CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO artifact_metadata VALUES
              ('artifact_id','art-1'),
              ('sqlite_schema_version','6'),
              ('extract_contract_version','4'),
              ('hash_algorithm','blake3'),
              ('index_level','full');
            CREATE TABLE extraction_revisions (revision_id INTEGER);
            INSERT INTO extraction_revisions VALUES (1);
            CREATE TABLE structural_facts (id INTEGER);
            CREATE TABLE language_capability_gaps (id INTEGER);
            """;
        command.ExecuteNonQuery();
        return new ResolutionArtifactFixture(root, dbPath);
    }

    public SqliteConnection OpenRead()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public void AddFile(string fileId, string path, string language = "csharp", string status = "indexed")
    {
        ExecuteWrite(
            $"""
            INSERT INTO files (file_id,path,language,content_hash,content_bytes,line_count,indexed_at,last_revision_id,status,metadata_json)
            VALUES ('{Escape(fileId)}','{Escape(path)}','{Escape(language)}','blake3:{Escape(fileId)}',10,1,'2026-08-09T00:00:00Z',1,'{status}',NULL);
            """);
    }

    public void AddSymbol(
        string fileId,
        string symbolId,
        string name,
        string kind,
        string path,
        string language = "csharp",
        string? parentId = null,
        string? metadataJson = null)
    {
        string parent = parentId is null ? "NULL" : $"'{Escape(parentId)}'";
        string meta = metadataJson is null ? "NULL" : $"'{Escape(metadataJson)}'";
        ExecuteWrite(
            $"""
            INSERT INTO symbols (
              symbol_id,file_id,path,language,name,kind,signature,doc_comment,visibility,parent_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,
              is_test,test_container,test_lifecycle,metadata_json)
            VALUES (
              '{Escape(symbolId)}','{Escape(fileId)}','{Escape(path)}','{Escape(language)}','{Escape(name)}','{Escape(kind)}',
              NULL,NULL,NULL,{parent},1,1,1,2,0,1,0,0,0,{meta});
            """);
    }

    public void AddTypeFact(string typeFactId, string symbolId, string resolvedType, bool inferred = false)
    {
        ExecuteWrite(
            $"""
            INSERT INTO type_facts (type_fact_id,symbol_id,language,resolved_type,is_inferred)
            VALUES ('{Escape(typeFactId)}','{Escape(symbolId)}','csharp','{Escape(resolvedType)}',{(inferred ? 1 : 0)});
            """);
    }

    public void AddIdentifier(
        string fileId,
        string identifierId,
        string name,
        string path,
        string kind = "call",
        string? containingSymbolId = null,
        long startByte = 0,
        long endByte = 3,
        long startLine = 1,
        string? metadataJson = null)
    {
        string containing = containingSymbolId is null ? "NULL" : $"'{Escape(containingSymbolId)}'";
        string meta = metadataJson is null ? "NULL" : $"'{Escape(metadataJson)}'";
        ExecuteWrite(
            $"""
            INSERT INTO identifiers (
              identifier_id,reference_site_id,file_id,path,language,name,kind,containing_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,confidence,metadata_json)
            VALUES (
              '{Escape(identifierId)}','site-{Escape(identifierId)}','{Escape(fileId)}','{Escape(path)}','csharp',
              '{Escape(name)}','{Escape(kind)}',{containing},{startLine},1,{startLine},4,{startByte},{endByte},1.0,{meta});
            INSERT OR REPLACE INTO reference_sites (
              reference_site_id,file_id,path,language,containing_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,is_exact,provenance)
            VALUES (
              'site-{Escape(identifierId)}','{Escape(fileId)}','{Escape(path)}','csharp',{containing},
              {startLine},1,{startLine},4,{startByte},{endByte},1,'target_token');
            """);
    }

    public void AddPending(
        string fileId,
        string pendingId,
        string fromSymbolId,
        string name,
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
            INSERT OR REPLACE INTO pending_relationships (
              pending_relationship_id,reference_site_id,from_symbol_id,caller_scope_symbol_id,file_id,path,kind,
              target_display_name,target_terminal_name,target_receiver,target_namespace_json,target_import_context,
              start_line,start_column,end_line,end_column,start_byte,end_byte,confidence,metadata_json)
            VALUES (
              '{Escape(pendingId)}','site-{Escape(pendingId)}','{Escape(fromSymbolId)}',NULL,'{Escape(fileId)}','{Escape(path)}',
              '{Escape(kind)}','{Escape(name)}','{Escape(name)}',NULL,'[]',NULL,{startLine},1,{startLine},4,{start},{end},1.0,NULL);
            INSERT OR REPLACE INTO reference_sites (
              reference_site_id,file_id,path,language,containing_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,is_exact,provenance)
            VALUES (
              'site-{Escape(pendingId)}','{Escape(fileId)}','{Escape(path)}','csharp','{Escape(fromSymbolId)}',
              {startLine},1,{startLine},4,{start},{end},1,'target_token');
            """);
    }

    public void AddRelationship(
        string fileId,
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
              relationship_id,reference_site_id,from_symbol_id,to_symbol_id,file_id,path,kind,
              start_line,start_column,end_line,end_column,start_byte,end_byte,confidence,metadata_json)
            VALUES (
              '{Escape(relationshipId)}','site-{Escape(relationshipId)}','{Escape(fromSymbolId)}',
              '{Escape(toSymbolId)}','{Escape(fileId)}','{Escape(path)}','{Escape(kind)}',
              {startLine},1,{startLine},4,{start},{end},1.0,NULL);
            INSERT OR REPLACE INTO reference_sites (
              reference_site_id,file_id,path,language,containing_symbol_id,
              start_line,start_column,end_line,end_column,start_byte,end_byte,is_exact,provenance)
            VALUES (
              'site-{Escape(relationshipId)}','{Escape(fileId)}','{Escape(path)}','csharp','{Escape(fromSymbolId)}',
              {startLine},1,{startLine},4,{start},{end},1,'target_token');
            """);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    private void ExecuteWrite(string sql)
    {
        using var connection = OpenWrite(DbPath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenWrite(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
