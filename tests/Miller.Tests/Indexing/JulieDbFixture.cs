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

    /// <summary>
    /// A row as written into the synthesized <c>symbols</c> table. The first eight fields are the M1 read
    /// projection; the remaining detail/body columns (M2 <c>ReadDetail</c>/<c>ReadBody</c>) are optional
    /// init-properties that default to NULL, so every existing positional construction stays valid and the
    /// 90 M1 tests are unaffected.
    /// </summary>
    internal sealed record SymbolRow(
        string Id,
        string Name,
        string Kind,
        string Language,
        string FilePath,
        string? Signature,
        int? StartLine,
        string? ParentId)
    {
        public string? DocComment { get; init; }
        public string? Visibility { get; init; }
        public string? CodeContext { get; init; }
        public int? BodyStartByte { get; init; }
        public int? BodyEndByte { get; init; }
        public int? BodyStartLine { get; init; }
        public int? BodyEndLine { get; init; }

        /// <summary>
        /// Raw <c>symbols.metadata</c> JSON (julie's per-language extractor output). NULL by default so
        /// existing rows are unaffected. Seed e.g. <c>{"is_test":true}</c> to exercise the cross-language
        /// <c>is_test</c> read path (M2 decision-4).
        /// </summary>
        public string? Metadata { get; init; }
    }

    /// <summary>A row as written into the synthesized <c>identifiers</c> table (M2 <c>ReadReferences</c>).</summary>
    internal sealed record IdentifierRow(
        string Id,
        string Name,
        string Kind,             // 'call' | 'variable_ref' | 'type_usage' | 'member_access'
        string Language,
        string FilePath,
        int StartLine,
        string? ContainingSymbolId); // POPULATED (enclosing symbol). target_symbol_id is ALWAYS NULL.

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
        bool createMetadataTable = true,
        IReadOnlyList<IdentifierRow>? identifiers = null,
        IReadOnlyDictionary<string, string>? fileContent = null,
        string? workspaceId = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "miller-julie-fixture-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "symbols.db");

        // Pooling=false on the write connection: it is disposed at the end of this using block, releasing the
        // file handle immediately WITHOUT a process-global SqliteConnection.ClearAllPools() (which races a
        // concurrently running test's live connection — xUnit parallelizes collections).
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        };
        using (var conn = new SqliteConnection(csb.ToString()))
        {
            conn.Open();
            // Match julie: WAL + FK enforcement ON. Exercises the WAL sidecar path the reader must tolerate.
            Exec(conn, "PRAGMA journal_mode=WAL;");
            Exec(conn, "PRAGMA foreign_keys=ON;");

            Exec(conn, FilesDdl);
            Exec(conn, SymbolsDdl);
            Exec(conn, IdentifiersDdl);
            if (createSchemaVersionTable) Exec(conn, SchemaVersionDdl);
            if (createMetadataTable) Exec(conn, MetadataDdl);

            // files rows (symbols.file_path REFERENCES files(path) — FK is ON, so parents must exist).
            // identifiers also FK to files(path), so union both sources of paths.
            foreach (var path in DistinctPaths(rows, identifiers))
            {
                string content = fileContent is not null && fileContent.TryGetValue(path, out var c) ? c : "";
                using var fcmd = conn.CreateCommand();
                fcmd.CommandText =
                    "INSERT INTO files (path, language, hash, size, last_modified, content, line_count) " +
                    "VALUES ($p, 'csharp', 'blake3hexstub', 100, 0, $content, 0);";
                fcmd.Parameters.AddWithValue("$p", path);
                fcmd.Parameters.AddWithValue("$content", content);
                fcmd.ExecuteNonQuery();
            }

            // symbols rows — parents first so self-FK parent_id resolves under FK enforcement. The detail/body
            // columns are written from the row's optional init-props (NULL by default — M1 behavior preserved).
            foreach (var r in OrderParentsFirst(rows))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO symbols (id, name, kind, language, file_path, signature, start_line, parent_id, " +
                    "metadata, doc_comment, visibility, code_context, " +
                    "body_start_byte, body_end_byte, body_start_line, body_end_line) " +
                    "VALUES ($id, $name, $kind, $lang, $fp, $sig, $sl, $pid, " +
                    "$meta, $doc, $vis, $ctx, $bsb, $beb, $bsl, $bel);";
                cmd.Parameters.AddWithValue("$id", r.Id);
                cmd.Parameters.AddWithValue("$name", r.Name);
                cmd.Parameters.AddWithValue("$kind", r.Kind);
                cmd.Parameters.AddWithValue("$lang", r.Language);
                cmd.Parameters.AddWithValue("$fp", r.FilePath);
                cmd.Parameters.AddWithValue("$sig", (object?)r.Signature ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sl", (object?)r.StartLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$pid", (object?)r.ParentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$meta", (object?)r.Metadata ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$doc", (object?)r.DocComment ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$vis", (object?)r.Visibility ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ctx", (object?)r.CodeContext ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bsb", (object?)r.BodyStartByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$beb", (object?)r.BodyEndByte ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bsl", (object?)r.BodyStartLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bel", (object?)r.BodyEndLine ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            // identifiers rows — target_symbol_id is ALWAYS NULL from extract (not written here).
            if (identifiers is not null)
            {
                foreach (var ident in identifiers)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        "INSERT INTO identifiers (id, name, kind, language, file_path, " +
                        "start_line, start_col, end_line, end_col, containing_symbol_id, target_symbol_id) " +
                        "VALUES ($id, $name, $kind, $lang, $fp, $sl, 0, $sl, 0, $cid, NULL);";
                    cmd.Parameters.AddWithValue("$id", ident.Id);
                    cmd.Parameters.AddWithValue("$name", ident.Name);
                    cmd.Parameters.AddWithValue("$kind", ident.Kind);
                    cmd.Parameters.AddWithValue("$lang", ident.Language);
                    cmd.Parameters.AddWithValue("$fp", ident.FilePath);
                    cmd.Parameters.AddWithValue("$sl", ident.StartLine);
                    cmd.Parameters.AddWithValue("$cid", (object?)ident.ContainingSymbolId ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
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

            if (createMetadataTable && workspaceId is not null)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO external_extract_metadata (key, value, updated_at) " +
                    "VALUES ('workspace_id', $val, 0);";
                cmd.Parameters.AddWithValue("$val", workspaceId);
                cmd.ExecuteNonQuery();
            }
        }

        // The write connection above was Pooling=false, so its handle is already released — no global
        // SqliteConnection.ClearAllPools() (which would race a parallel test's live connection).
        return new JulieDbFixture(dir, dbPath, rows);
    }

    /// <summary>
    /// The canonical fixture: schema 26 / contract '1' with ~12 realistic rows — mixed kinds/languages,
    /// some NULL signatures, at least one NULL start_line, parent/child pairs via parent_id, distinct files.
    /// </summary>
    public static JulieDbFixture CreateDefault() => Create(26, "1", DefaultRows);

    // ----- M2 inspect/ExtractReader fixture -----

    /// <summary>The byte content of <c>auth/UserService.cs</c> in <see cref="CreateForInspect"/>.</summary>
    public const string UserServiceContent =
        "public class UserService {\n" +   // bytes 0..26  (line 1)
        "  public User GetUser(int id) {\n" + // line 2
        "    return _repo.Find(id);\n" +    // line 3
        "  }\n" +                            // line 4
        "}\n";                               // line 5

    /// <summary>
    /// The id of <c>GetUser</c> — the symbol carrying full detail (doc_comment/visibility/body spans) and the
    /// one whose body slices out of <see cref="UserServiceContent"/> in <see cref="CreateForInspect"/>.
    /// </summary>
    public const string GetUserId = "b2c3d4e5f6001122334455667788990a";

    /// <summary>The id of <c>UserService</c> (the parent class of GetUser/DeleteUser).</summary>
    public const string UserServiceId = "a1b2c3d4e5f600112233445566778899";

    /// <summary>
    /// A fixture wired for the M2 inspect/ExtractReader tests: GetUser carries doc_comment + visibility +
    /// body byte/line spans into <see cref="UserServiceContent"/>; identifiers record two name-based refs to
    /// GetUser (in two enclosing symbols) and one call FROM GetUser to a helper (callee). DeleteUser carries
    /// NULL body spans (the graceful-degradation case). workspace_id is set so startup can read it.
    /// </summary>
    public static JulieDbFixture CreateForInspect()
    {
        // GetUser's body is the slice from just after "{" on line 1 to the closing "}" on line 4.
        // Byte offsets into UserServiceContent (computed against the literal above).
        int bodyStart = UserServiceContent.IndexOf("public User GetUser", StringComparison.Ordinal);
        int bodyEnd = UserServiceContent.IndexOf("  }\n", StringComparison.Ordinal) + 3; // include the '}'

        var rows = new[]
        {
            new SymbolRow(UserServiceId, "UserService", "class", "csharp",
                "auth/UserService.cs", "public class UserService", 1, null)
            { Visibility = "public", DocComment = "The user service." },

            new SymbolRow(GetUserId, "GetUser", "method", "csharp",
                "auth/UserService.cs", "public User GetUser(int id)", 2, UserServiceId)
            {
                Visibility = "public",
                DocComment = "Gets a user by id.",
                CodeContext = "public User GetUser(int id) { ... }",
                BodyStartByte = bodyStart, BodyEndByte = bodyEnd,
                BodyStartLine = 2, BodyEndLine = 4,
            },

            // DeleteUser: NULL body spans (graceful body degradation) + a NULL body line range.
            new SymbolRow("c3d4e5f6001122334455667788990a1b", "DeleteUser", "method", "csharp",
                "auth/UserService.cs", "public void DeleteUser(int id)", 6, UserServiceId)
            { Visibility = "public" },

            // A helper that GetUser calls (callee target by name).
            new SymbolRow("dd001122334455667788990a1b2c3d4e", "Find", "method", "csharp",
                "auth/Repo.cs", "public User Find(int id)", 3, null),

            // An unrelated caller in another file that references GetUser by name.
            new SymbolRow("ee001122334455667788990a1b2c3d4e", "Controller", "class", "csharp",
                "web/Controller.cs", "public class Controller", 1, null),
        };

        var identifiers = new[]
        {
            // Two name-based refs to "GetUser": one inside Controller, one inside Find's file (top-level).
            new IdentifierRow("f100000000000000000000000000000a", "GetUser", "call", "csharp",
                "web/Controller.cs", 4, "ee001122334455667788990a1b2c3d4e"),
            new IdentifierRow("f100000000000000000000000000000b", "GetUser", "call", "csharp",
                "auth/Repo.cs", 9, "dd001122334455667788990a1b2c3d4e"),
            // A call FROM GetUser to "Find" (callee one-hop): containing_symbol_id == GetUser, kind 'call'.
            new IdentifierRow("f100000000000000000000000000000c", "Find", "call", "csharp",
                "auth/UserService.cs", 3, GetUserId),
        };

        var content = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth/UserService.cs"] = UserServiceContent,
        };

        return Create(26, "1", rows, identifiers: identifiers, fileContent: content, workspaceId: "ws-inspect-001");
    }

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

    private static IEnumerable<string> DistinctPaths(
        IReadOnlyList<SymbolRow> rows, IReadOnlyList<IdentifierRow>? identifiers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rows)
            if (seen.Add(r.FilePath))
                yield return r.FilePath;
        if (identifiers is not null)
            foreach (var i in identifiers)
                if (seen.Add(i.FilePath))
                    yield return i.FilePath;
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
        // Release THIS fixture's pooled reader handles so the temp dir can be deleted — but scope it to this
        // DB only (ClearPool, NOT the process-global ClearAllPools), so a concurrently running test's live
        // connection is never disposed out from under it (xUnit parallelizes collections).
        using (var c = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            SqliteConnection.ClearPool(c);
        }
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

    private const string IdentifiersDdl = """
        CREATE TABLE IF NOT EXISTS identifiers (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            language TEXT NOT NULL,
            file_path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE,
            start_line INTEGER NOT NULL, start_col INTEGER NOT NULL, end_line INTEGER NOT NULL, end_col INTEGER NOT NULL,
            start_byte INTEGER, end_byte INTEGER,
            containing_symbol_id TEXT REFERENCES symbols(id) ON DELETE CASCADE,
            target_symbol_id TEXT REFERENCES symbols(id) ON DELETE SET NULL,
            confidence REAL DEFAULT 1.0,
            code_context TEXT,
            last_indexed INTEGER DEFAULT 0
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
