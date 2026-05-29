using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Tests.Indexing;

/// <summary>
/// Synthesizes a tiny SQLite file matching julie v7.12.2's verified extract schema (schema_version 26,
/// extract_contract_version 1). This is Miller's READ-CONTRACT harness — it is NOT a re-test of julie's
/// extraction (julie owns that). The DDL is transcribed verbatim from julie's <c>src/database/schema.rs</c>
/// (see docs/findings/julie-contract-verified.md §1), so the reader is exercised against the real column
/// set, NULL discipline, and self-FK that a live extract produces.
///
/// Disposable: deletes the temp directory (and -wal/-shm sidecars) on <see cref="Dispose"/>.
/// </summary>
internal sealed class JulieDbFixture : IDisposable
{
    private readonly string _dir;

    /// <summary>Absolute path to the synthesized julie extract <c>.db</c> file.</summary>
    public string DbPath { get; }

    /// <summary>Absolute path to the directory containing the DB (the WAL sidecars live here).</summary>
    public string Directory => _dir;

    /// <summary>
    /// The known rows inserted by <see cref="CreateDefault"/>, in INSERT order. Tests assert the reader's
    /// output against the subset/ordering these imply (the reader's SELECT re-orders by file_path,start_line,id).
    /// </summary>
    public IReadOnlyList<SymbolRow> Rows { get; }

    private JulieDbFixture(string dir, string dbPath, IReadOnlyList<SymbolRow> rows)
    {
        _dir = dir;
        DbPath = dbPath;
        Rows = rows;
    }

    /// <summary>A row as written into the synthesized <c>symbols</c> table.</summary>
    internal sealed record SymbolRow(
        string Id,
        string Name,
        string Kind,
        string Language,
        string FilePath,
        string? Signature,
        int? StartLine,
        string? ParentId);

    /// <summary>
    /// Build a fixture with the given schema/contract version rows and the supplied symbol rows.
    /// <paramref name="schemaVersion"/> is written to <c>schema_version</c>; <paramref name="contractValue"/>
    /// is written to <c>external_extract_metadata['extract_contract_version']</c> as TEXT (julie stores all
    /// metadata values as strings). Passing <c>null</c> for either skips creating that table entirely — used
    /// by the gate's missing-table tests.
    /// </summary>
    public static JulieDbFixture Create(
        long? schemaVersion,
        string? contractValue,
        IReadOnlyList<SymbolRow> rows,
        bool createSchemaVersionTable = true,
        bool createMetadataTable = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-julie-fixture-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "symbols.db");

        var csb = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate };
        using (var conn = new SqliteConnection(csb.ToString()))
        {
            conn.Open();
            // Match julie: WAL + FK enforcement ON. Exercises the WAL sidecar path the reader must tolerate.
            Exec(conn, "PRAGMA journal_mode=WAL;");
            Exec(conn, "PRAGMA foreign_keys=ON;");

            Exec(conn, FilesDdl);
            Exec(conn, SymbolsDdl);
            if (createSchemaVersionTable) Exec(conn, SchemaVersionDdl);
            if (createMetadataTable) Exec(conn, MetadataDdl);

            // files rows (symbols.file_path REFERENCES files(path) — FK is ON, so parents must exist).
            foreach (var path in DistinctPaths(rows))
            {
                using var fcmd = conn.CreateCommand();
                fcmd.CommandText =
                    "INSERT INTO files (path, language, hash, size, last_modified, content, line_count) " +
                    "VALUES ($p, 'csharp', 'blake3hexstub', 100, 0, '', 0);";
                fcmd.Parameters.AddWithValue("$p", path);
                fcmd.ExecuteNonQuery();
            }

            // symbols rows — parents first so self-FK parent_id resolves under FK enforcement.
            foreach (var r in OrderParentsFirst(rows))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO symbols (id, name, kind, language, file_path, signature, start_line, parent_id) " +
                    "VALUES ($id, $name, $kind, $lang, $fp, $sig, $sl, $pid);";
                cmd.Parameters.AddWithValue("$id", r.Id);
                cmd.Parameters.AddWithValue("$name", r.Name);
                cmd.Parameters.AddWithValue("$kind", r.Kind);
                cmd.Parameters.AddWithValue("$lang", r.Language);
                cmd.Parameters.AddWithValue("$fp", r.FilePath);
                cmd.Parameters.AddWithValue("$sig", (object?)r.Signature ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sl", (object?)r.StartLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pid", (object?)r.ParentId ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            if (createSchemaVersionTable && schemaVersion is { } sv)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO schema_version (version, applied_at, description) VALUES ($v, 0, 'test');";
                cmd.Parameters.AddWithValue("$v", sv);
                cmd.ExecuteNonQuery();
            }

            if (createMetadataTable && contractValue is not null)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO external_extract_metadata (key, value, updated_at) " +
                    "VALUES ('extract_contract_version', $val, 0);";
                cmd.Parameters.AddWithValue("$val", contractValue);
                cmd.ExecuteNonQuery();
            }
        }

        // Drop the pool so the file handle is released and the dir can be deleted on Dispose.
        SqliteConnection.ClearAllPools();
        return new JulieDbFixture(dir, dbPath, rows);
    }

    /// <summary>
    /// The canonical fixture: schema 26 / contract '1' with ~12 realistic rows — mixed kinds/languages,
    /// some NULL signatures, at least one NULL start_line, parent/child pairs via parent_id, distinct files.
    /// </summary>
    public static JulieDbFixture CreateDefault() => Create(26, "1", DefaultRows);

    /// <summary>Realistic MD5-hex symbol ids (32 lowercase hex chars), per julie's id scheme (treated as opaque).</summary>
    public static IReadOnlyList<SymbolRow> DefaultRows { get; } = new[]
    {
        // auth/UserService.cs — a class with two child methods (parent/child via parent_id).
        new SymbolRow("a1b2c3d4e5f600112233445566778899", "UserService", "class", "csharp",
            "auth/UserService.cs", "public class UserService", 1, null),
        new SymbolRow("b2c3d4e5f6001122334455667788990a", "GetUser", "method", "csharp",
            "auth/UserService.cs", "public User GetUser(int id)", 5, "a1b2c3d4e5f600112233445566778899"),
        new SymbolRow("c3d4e5f6001122334455667788990a1b", "DeleteUser", "method", "csharp",
            "auth/UserService.cs", null /* NULL signature */, 12, "a1b2c3d4e5f600112233445566778899"),

        // auth/token.ts — a TS function + a const with a NULL start_line (the nullable-INTEGER trap).
        new SymbolRow("d4e5f6001122334455667788990a1b2c", "parseToken", "function", "typescript",
            "auth/token.ts", "function parseToken(raw: string): Token", 3, null),
        new SymbolRow("e5f6001122334455667788990a1b2c3d", "TOKEN_TTL", "constant", "typescript",
            "auth/token.ts", "const TOKEN_TTL = 3600", null /* NULL start_line -> 0 */, null),

        // core/math.rs — a Rust struct + impl method.
        new SymbolRow("f6001122334455667788990a1b2c3d4e", "Vector512", "struct", "rust",
            "core/math.rs", "pub struct Vector512", 8, null),
        new SymbolRow("001122334455667788990a1b2c3d4e5f", "dot", "method", "rust",
            "core/math.rs", "pub fn dot(&self, other: &Vector512) -> f32", 20, "f6001122334455667788990a1b2c3d4e"),

        // util/strings.py — python functions, one with NULL signature.
        new SymbolRow("1122334455667788990a1b2c3d4e5f60", "snake_to_camel", "function", "python",
            "util/strings.py", "def snake_to_camel(s)", 2, null),
        new SymbolRow("22334455667788990a1b2c3d4e5f6011", "EMPTY", "variable", "python",
            "util/strings.py", null /* NULL signature */, 1, null),

        // http/Server.go — go type + two methods.
        new SymbolRow("334455667788990a1b2c3d4e5f601122", "Server", "struct", "go",
            "http/Server.go", "type Server struct", 10, null),
        new SymbolRow("4455667788990a1b2c3d4e5f60112233", "getHTTPResponseCode", "method", "go",
            "http/Server.go", "func (s *Server) getHTTPResponseCode() int", 25, "334455667788990a1b2c3d4e5f601122"),
        new SymbolRow("55667788990a1b2c3d4e5f6011223344", "ServeHTTP", "method", "go",
            "http/Server.go", "func (s *Server) ServeHTTP(w ResponseWriter, r *Request)", 40, "334455667788990a1b2c3d4e5f601122"),
    };

    private static IEnumerable<string> DistinctPaths(IReadOnlyList<SymbolRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
            if (seen.Add(r.FilePath))
                yield return r.FilePath;
    }

    // Parents (parent_id == null) before children so the self-referential FK never dangles at insert time.
    private static IEnumerable<SymbolRow> OrderParentsFirst(IReadOnlyList<SymbolRow> rows)
    {
        foreach (var r in rows) if (r.ParentId is null) yield return r;
        foreach (var r in rows) if (r.ParentId is not null) yield return r;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (System.IO.Directory.Exists(_dir))
                System.IO.Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a held handle on a CI agent must not fail the test.
        }
        _ = CultureInfo.InvariantCulture; // keep the using meaningful if trimmed later
    }

    // --- DDL transcribed verbatim from julie src/database/schema.rs (contract-verified §1) ---

    private const string FilesDdl = """
        CREATE TABLE IF NOT EXISTS files (
            path TEXT PRIMARY KEY,
            language TEXT NOT NULL,
            hash TEXT NOT NULL,
            size INTEGER NOT NULL,
            last_modified INTEGER NOT NULL,
            last_indexed INTEGER DEFAULT 0,
            parse_cache BLOB,
            symbol_count INTEGER DEFAULT 0,
            content TEXT,
            line_count INTEGER DEFAULT 0
        );
        """;

    private const string SymbolsDdl = """
        CREATE TABLE IF NOT EXISTS symbols (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            language TEXT NOT NULL,
            file_path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE,
            signature TEXT,
            start_line INTEGER, start_col INTEGER, end_line INTEGER, end_col INTEGER,
            start_byte INTEGER, end_byte INTEGER,
            doc_comment TEXT,
            visibility TEXT,
            code_context TEXT,
            parent_id TEXT REFERENCES symbols(id),
            metadata TEXT,
            file_hash TEXT,
            last_indexed INTEGER DEFAULT 0,
            semantic_group TEXT,
            confidence REAL DEFAULT 1.0,
            content_type TEXT DEFAULT NULL,
            body_start_line INTEGER, body_start_col INTEGER, body_end_line INTEGER, body_end_col INTEGER,
            body_start_byte INTEGER, body_end_byte INTEGER, body_hash TEXT,
            reference_score REAL NOT NULL DEFAULT 0.0
        );
        """;

    private const string SchemaVersionDdl = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER PRIMARY KEY,
            applied_at INTEGER NOT NULL,
            description TEXT NOT NULL
        );
        """;

    private const string MetadataDdl = """
        CREATE TABLE IF NOT EXISTS external_extract_metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at INTEGER NOT NULL
        );
        """;
}
