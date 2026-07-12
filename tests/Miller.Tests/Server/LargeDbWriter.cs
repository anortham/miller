using Microsoft.Data.Sqlite;
using Miller.Indexing;

namespace Miller.Tests.Server;

/// <summary>
/// A fast bulk writer for a large synthesized julie-extract v1 DB, used only by the Scale
/// <see cref="RebuildLatencyTests"/>. Unlike <see cref="Miller.Tests.Indexing.JulieDbFixture"/> (which inserts
/// row-by-row for fidelity on small fixtures), this inserts tens of thousands of symbols inside ONE transaction
/// with prepared, reused commands — so building the latency fixture is itself fast. The schema is the minimal v1
/// subset the production build path (<see cref="RepositoryIndexLoader.Load"/> → <see cref="SqliteSymbolReader.Read"/>
/// + <see cref="SymbolGraphReader.Read"/> + <see cref="SqliteBridgeReader.Read"/>) + the schema gate require:
/// v1 <c>files</c>/<c>symbols</c> (incl. <c>end_line</c> [D7] and typed test-role columns) + the
/// <c>relationships</c>/<c>identifiers</c> edge tables [D2] + the split bridge tables
/// (<c>type_argument_usages</c>/<c>type_arguments</c>/<c>literals</c>/<c>symbol_annotations</c>) + the single
/// empty <c>parse_diagnostics</c> table used by role-evidence currency + the single <c>artifact_metadata</c>
/// table carrying the gate's version/hash keys. The bridge + identifier tables are
/// created empty — the rebuild latency this measures is the read+build path, and empty reads still exercise the
/// loader's extra SELECTs.
/// </summary>
internal static class LargeDbWriter
{
    public static void Write(string dbPath, IReadOnlyList<IndexedSymbol> symbols)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        };
        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn, "PRAGMA foreign_keys=OFF;"); // bulk load: skip FK checks (the data is internally consistent)
        Exec(conn, "PRAGMA synchronous=OFF;");  // bulk load only; not a production setting

        // v1 files: file_id PK + path UNIQUE + content_hash/content_bytes (the symbol reader keys symbols to file_id).
        Exec(conn, """
            CREATE TABLE files (
                file_id TEXT PRIMARY KEY, path TEXT NOT NULL UNIQUE, language TEXT NOT NULL,
                content_hash TEXT NOT NULL, content_bytes INTEGER NOT NULL, line_count INTEGER,
                indexed_at TEXT NOT NULL, last_revision_id INTEGER NOT NULL, status TEXT NOT NULL,
                metadata_json TEXT, content TEXT);
            """);
        // v1 symbols: symbol_id PK, path (not file_path), parent_symbol_id (not parent_id), typed role columns.
        Exec(conn, """
            CREATE TABLE symbols (
                symbol_id TEXT PRIMARY KEY, file_id TEXT NOT NULL, path TEXT NOT NULL, language TEXT NOT NULL,
                name TEXT NOT NULL, kind TEXT NOT NULL, signature TEXT, start_line INTEGER, end_line INTEGER,
                parent_symbol_id TEXT, is_test INTEGER NOT NULL DEFAULT 0,
                test_container INTEGER NOT NULL DEFAULT 0, test_lifecycle INTEGER NOT NULL DEFAULT 0,
                metadata_json TEXT);
            """);
        Exec(conn, """
            CREATE TABLE parse_diagnostics (
                diagnostic_id TEXT PRIMARY KEY, file_id TEXT NOT NULL, path TEXT NOT NULL,
                language TEXT NOT NULL, kind TEXT NOT NULL, message TEXT,
                start_line INTEGER NOT NULL, start_column INTEGER NOT NULL,
                end_line INTEGER NOT NULL, end_column INTEGER NOT NULL,
                start_byte INTEGER NOT NULL, end_byte INTEGER NOT NULL, metadata_json TEXT);
            """);
        // D2 edge tables — the production build path reads both. v1 column names (relationships: relationship_id +
        // from/to/kind; identifiers: identifier_id + name/kind/path/containing_symbol_id).
        Exec(conn, """
            CREATE TABLE relationships (
                relationship_id TEXT PRIMARY KEY, from_symbol_id TEXT NOT NULL, to_symbol_id TEXT NOT NULL,
                path TEXT, kind TEXT NOT NULL);
            """);
        Exec(conn, """
            CREATE TABLE identifiers (
                identifier_id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, language TEXT NOT NULL,
                path TEXT NOT NULL, start_line INTEGER NOT NULL, start_column INTEGER NOT NULL,
                end_line INTEGER NOT NULL, end_column INTEGER NOT NULL, containing_symbol_id TEXT, target_symbol_id TEXT);
            """);
        Exec(conn, """
            CREATE TABLE pending_relationships (
                pending_relationship_id TEXT PRIMARY KEY,
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
                start_column INTEGER,
                end_line INTEGER,
                end_column INTEGER,
                start_byte INTEGER,
                end_byte INTEGER,
                confidence REAL NOT NULL,
                metadata_json TEXT,
                FOREIGN KEY (from_symbol_id) REFERENCES symbols(symbol_id) ON DELETE CASCADE,
                FOREIGN KEY (caller_scope_symbol_id) REFERENCES symbols(symbol_id) ON DELETE SET NULL,
                FOREIGN KEY (file_id) REFERENCES files(file_id) ON DELETE CASCADE);
            """);
        Exec(conn, """
            CREATE TABLE pending_resolutions (
                pending_relationship_id TEXT PRIMARY KEY
                    REFERENCES pending_relationships(pending_relationship_id) ON DELETE CASCADE,
                target_symbol_id TEXT NOT NULL REFERENCES symbols(symbol_id) ON DELETE CASCADE,
                tier INTEGER NOT NULL,
                confidence REAL NOT NULL,
                method TEXT NOT NULL,
                resolved_at_revision INTEGER NOT NULL);
            """);
        // M4 bridge tables (v1 split). SqliteBridgeReader is on the single production RepositoryIndexLoader.Load
        // path (D9), so IndexRebuilder.Rebuild over this DB SELECTs all of them — created empty so the rebuild path
        // does not crash on "no such table". v1 moves identifier_id/path onto type_argument_usages; the args JOIN by
        // usage_id; literals/symbol_annotations carry v1 columns (literal_id/path; annotation_id, no ordinal).
        Exec(conn, """
            CREATE TABLE type_argument_usages (
                usage_id TEXT PRIMARY KEY, identifier_id TEXT NOT NULL, path TEXT NOT NULL, language TEXT NOT NULL);
            """);
        Exec(conn, """
            CREATE TABLE type_arguments (
                type_argument_id TEXT PRIMARY KEY, usage_id TEXT NOT NULL, parent_type_argument_id TEXT,
                ordinal INTEGER NOT NULL, type_name TEXT NOT NULL);
            """);
        Exec(conn, """
            CREATE TABLE literals (
                literal_id TEXT PRIMARY KEY, literal_text TEXT NOT NULL, kind TEXT NOT NULL, carrier TEXT,
                arg_position INTEGER NOT NULL, language TEXT NOT NULL, path TEXT NOT NULL, start_line INTEGER,
                end_line INTEGER, start_byte INTEGER, end_byte INTEGER, containing_symbol_id TEXT, confidence REAL);
            """);
        Exec(conn, """
            CREATE TABLE symbol_annotations (
                annotation_id TEXT PRIMARY KEY, symbol_id TEXT NOT NULL, annotation TEXT NOT NULL,
                annotation_key TEXT, raw_text TEXT, carrier TEXT);
            """);
        // v1 single metadata table carrying the gate's version + hash keys.
        Exec(conn, "CREATE TABLE artifact_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        Exec(conn, $"INSERT INTO artifact_metadata (key, value) VALUES ('sqlite_schema_version', '{MillerExtractContract.ExpectedSchemaVersion}');");
        Exec(conn, $"INSERT INTO artifact_metadata (key, value) VALUES ('schema_version', '{MillerExtractContract.ExpectedSchemaVersion}');");
        Exec(conn, $"INSERT INTO artifact_metadata (key, value) VALUES ('extract_contract_version', '{MillerExtractContract.ExpectedExtractContractVersion}');");
        Exec(conn, $"INSERT INTO artifact_metadata (key, value) VALUES ('hash_algorithm', '{MillerExtractContract.ExpectedHashAlgorithm}');");

        using var tx = conn.BeginTransaction();

        // Distinct files first. The v1 symbol reader keys symbols to file_id; use a deterministic file_id per path.
        var distinctFiles = new HashSet<string>(StringComparer.Ordinal);
        using (var fcmd = conn.CreateCommand())
        {
            fcmd.Transaction = tx;
            fcmd.CommandText =
                "INSERT OR IGNORE INTO files (file_id, path, language, content_hash, content_bytes, line_count, " +
                "indexed_at, last_revision_id, status, metadata_json, content) " +
                "VALUES ($fid, $p, 'csharp', 'blake3:00', 0, 0, '1970-01-01T00:00:00Z', 0, 'indexed', NULL, '');";
            var pFid = Add(fcmd, "$fid");
            var pPath = Add(fcmd, "$p");
            fcmd.Prepare();
            foreach (var s in symbols)
            {
                if (!distinctFiles.Add(s.FilePath)) continue;
                pFid.Value = FileId(s.FilePath);
                pPath.Value = s.FilePath;
                fcmd.ExecuteNonQuery();
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO symbols (symbol_id, file_id, path, language, name, kind, signature, start_line, " +
                "end_line, parent_symbol_id, is_test, test_container, test_lifecycle, metadata_json) " +
                "VALUES ($id, $fid, $p, $lang, $name, $kind, $sig, $sl, $el, $pid, $istest, $tcont, $tlife, NULL);";
            var pId = Add(cmd, "$id");
            var pFid = Add(cmd, "$fid");
            var pPath = Add(cmd, "$p");
            var pLang = Add(cmd, "$lang");
            var pName = Add(cmd, "$name");
            var pKind = Add(cmd, "$kind");
            var pSig = Add(cmd, "$sig");
            var pSl = Add(cmd, "$sl");
            var pEl = Add(cmd, "$el");
            var pPid = Add(cmd, "$pid");
            var pIsTest = Add(cmd, "$istest");
            var pTestContainer = Add(cmd, "$tcont");
            var pTestLifecycle = Add(cmd, "$tlife");
            cmd.Prepare();

            foreach (var s in symbols)
            {
                pId.Value = s.SymbolId;
                pFid.Value = FileId(s.FilePath);
                pPath.Value = s.FilePath;
                pLang.Value = s.Language;
                pName.Value = s.Name;
                pKind.Value = s.Kind;
                pSig.Value = (object?)s.Signature ?? DBNull.Value;
                pSl.Value = s.StartLine;
                pEl.Value = s.EndLine;
                pPid.Value = (object?)s.ParentId ?? DBNull.Value;
                pIsTest.Value = s.IsTest ? 1 : 0;
                pTestContainer.Value = s.TestContainer ? 1 : 0;
                pTestLifecycle.Value = s.TestLifecycle ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
        }

        // A realistic edge population so the rebuild latency includes the graph build cost (D11). One precise
        // relationships edge per symbol → the next symbol's id (a deterministic chain), so the graph build over
        // ~50k edges is measured, not skipped. The last symbol points back to the first (a single cycle, which
        // the BFS visited-set tolerates) so every symbol has exactly one out-edge.
        if (symbols.Count > 1)
        {
            using var rcmd = conn.CreateCommand();
            rcmd.Transaction = tx;
            rcmd.CommandText =
                "INSERT INTO relationships (relationship_id, from_symbol_id, to_symbol_id, path, kind) " +
                "VALUES ($id, $from, $to, '', 'calls');";
            var rId = Add(rcmd, "$id");
            var rFrom = Add(rcmd, "$from");
            var rTo = Add(rcmd, "$to");
            rcmd.Prepare();
            for (int i = 0; i < symbols.Count; i++)
            {
                rId.Value = "rel" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                rFrom.Value = symbols[i].SymbolId;
                rTo.Value = symbols[(i + 1) % symbols.Count].SymbolId;
                rcmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>The deterministic synthetic v1 file_id for a path (symbols FK to it).</summary>
    private static string FileId(string path) => "file:" + path;

    private static SqliteParameter Add(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        cmd.Parameters.Add(p);
        return p;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
