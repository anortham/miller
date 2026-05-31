using Microsoft.Data.Sqlite;
using Miller.Indexing;

namespace Miller.Tests.Server;

/// <summary>
/// A fast bulk writer for a large synthesized julie-schema extract DB, used only by the Scale
/// <see cref="RebuildLatencyTests"/>. Unlike <see cref="Miller.Tests.Indexing.JulieDbFixture"/> (which inserts
/// row-by-row for fidelity on small fixtures), this inserts tens of thousands of symbols inside ONE transaction
/// with prepared, reused commands — so building the latency fixture is itself fast. The schema is the minimal
/// subset the production build path (<see cref="RepositoryIndexLoader.Load"/> → <see cref="SqliteSymbolReader.Read"/>
/// + <see cref="SymbolGraphReader.Read"/>) + the schema gate require (files + symbols [incl. <c>end_line</c>, D7]
/// + the <c>relationships</c>/<c>identifiers</c> edge tables [D2] + schema_version + external_extract_metadata),
/// at the pinned schema 28 / contract 3. The edge tables are created empty here — the rebuild latency this
/// measures is the read+build path, and empty edge reads still exercise the loader's two extra SELECTs.
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

        Exec(conn, """
            CREATE TABLE files (
                path TEXT PRIMARY KEY, language TEXT NOT NULL, hash TEXT NOT NULL, size INTEGER NOT NULL,
                last_modified INTEGER NOT NULL, content TEXT, line_count INTEGER DEFAULT 0);
            """);
        Exec(conn, """
            CREATE TABLE symbols (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, language TEXT NOT NULL,
                file_path TEXT NOT NULL, signature TEXT, start_line INTEGER, end_line INTEGER,
                parent_id TEXT, metadata TEXT);
            """);
        // D2 edge tables — the production build path (RepositoryIndexLoader) reads both. Minimal column subset
        // the readers SELECT (relationships: from/to/kind; identifiers: name/kind/containing_symbol_id).
        Exec(conn, """
            CREATE TABLE relationships (
                id TEXT PRIMARY KEY, from_symbol_id TEXT NOT NULL, to_symbol_id TEXT NOT NULL, kind TEXT NOT NULL);
            """);
        Exec(conn, """
            CREATE TABLE identifiers (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, language TEXT NOT NULL,
                file_path TEXT NOT NULL, start_line INTEGER NOT NULL, start_col INTEGER NOT NULL,
                end_line INTEGER NOT NULL, end_col INTEGER NOT NULL, containing_symbol_id TEXT, target_symbol_id TEXT);
            """);
        // M4 bridge tables (verbatim from julie v7.13.1 schema.rs). SqliteBridgeReader is on the single production
        // RepositoryIndexLoader.Load path (D9), so IndexRebuilder.Rebuild over this DB SELECTs all three — created
        // empty here, exactly as JulieDbFixture does, so the rebuild/latency path does not crash on
        // "no such table: type_arguments".
        Exec(conn, """
            CREATE TABLE type_arguments (
                id TEXT PRIMARY KEY, identifier_id TEXT NOT NULL, parent_arg_id TEXT, ordinal INTEGER NOT NULL,
                type_name TEXT NOT NULL, target_symbol_id TEXT, file_path TEXT NOT NULL, language TEXT NOT NULL,
                last_indexed INTEGER);
            """);
        Exec(conn, """
            CREATE TABLE literals (
                id TEXT PRIMARY KEY, literal_text TEXT NOT NULL, kind TEXT NOT NULL, carrier TEXT,
                arg_position INTEGER NOT NULL, language TEXT NOT NULL, file_path TEXT NOT NULL, start_line INTEGER,
                end_line INTEGER, start_byte INTEGER, end_byte INTEGER, containing_symbol_id TEXT, confidence REAL);
            """);
        Exec(conn, """
            CREATE TABLE symbol_annotations (
                id TEXT PRIMARY KEY, symbol_id TEXT NOT NULL, ordinal INTEGER NOT NULL, annotation TEXT NOT NULL,
                annotation_key TEXT, raw_text TEXT, carrier TEXT, UNIQUE (symbol_id, ordinal));
            """);
        Exec(conn, "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at INTEGER NOT NULL, description TEXT NOT NULL);");
        Exec(conn, "CREATE TABLE external_extract_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER NOT NULL);");
        Exec(conn, $"INSERT INTO schema_version (version, applied_at, description) VALUES ({MillerExtractContract.ExpectedSchemaVersion}, 0, 'test');");
        Exec(conn, $"INSERT INTO external_extract_metadata (key, value, updated_at) VALUES ('extract_contract_version', '{MillerExtractContract.ExpectedExtractContractVersion}', 0);");
        Exec(conn, $"INSERT INTO external_extract_metadata (key, value, updated_at) VALUES ('hash_algorithm', '{MillerExtractContract.ExpectedHashAlgorithm}', 0);");

        using var tx = conn.BeginTransaction();

        // Distinct files first (symbols.file_path has no FK here, but keep the files table populated/realistic).
        var distinctFiles = new HashSet<string>(StringComparer.Ordinal);
        using (var fcmd = conn.CreateCommand())
        {
            fcmd.Transaction = tx;
            fcmd.CommandText =
                "INSERT OR IGNORE INTO files (path, language, hash, size, last_modified, content, line_count) " +
                "VALUES ($p, 'csharp', 'h', 1, 0, '', 0);";
            var pPath = fcmd.CreateParameter(); pPath.ParameterName = "$p"; fcmd.Parameters.Add(pPath);
            fcmd.Prepare();
            foreach (var s in symbols)
            {
                if (!distinctFiles.Add(s.FilePath)) continue;
                pPath.Value = s.FilePath;
                fcmd.ExecuteNonQuery();
            }
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO symbols (id, name, kind, language, file_path, signature, start_line, end_line, parent_id, metadata) " +
                "VALUES ($id, $name, $kind, $lang, $fp, $sig, $sl, $el, $pid, NULL);";
            var pId = Add(cmd, "$id");
            var pName = Add(cmd, "$name");
            var pKind = Add(cmd, "$kind");
            var pLang = Add(cmd, "$lang");
            var pFp = Add(cmd, "$fp");
            var pSig = Add(cmd, "$sig");
            var pSl = Add(cmd, "$sl");
            var pEl = Add(cmd, "$el");
            var pPid = Add(cmd, "$pid");
            cmd.Prepare();

            foreach (var s in symbols)
            {
                pId.Value = s.SymbolId;
                pName.Value = s.Name;
                pKind.Value = s.Kind;
                pLang.Value = s.Language;
                pFp.Value = s.FilePath;
                pSig.Value = (object?)s.Signature ?? DBNull.Value;
                pSl.Value = s.StartLine;
                pEl.Value = s.EndLine;
                pPid.Value = (object?)s.ParentId ?? DBNull.Value;
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
                "INSERT INTO relationships (id, from_symbol_id, to_symbol_id, kind) VALUES ($id, $from, $to, 'calls');";
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
