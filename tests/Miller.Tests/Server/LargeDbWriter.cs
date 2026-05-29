using Microsoft.Data.Sqlite;
using Miller.Indexing;

namespace Miller.Tests.Server;

/// <summary>
/// A fast bulk writer for a large synthesized julie-schema extract DB, used only by the Scale
/// <see cref="RebuildLatencyTests"/>. Unlike <see cref="Miller.Tests.Indexing.JulieDbFixture"/> (which inserts
/// row-by-row for fidelity on small fixtures), this inserts tens of thousands of symbols inside ONE transaction
/// with prepared, reused commands — so building the latency fixture is itself fast. The schema is the minimal
/// subset <see cref="SqliteSymbolReader.Read"/> + the schema gate require (files + symbols + schema_version +
/// external_extract_metadata), at the pinned schema 26 / contract 1.
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
                file_path TEXT NOT NULL, signature TEXT, start_line INTEGER, parent_id TEXT, metadata TEXT);
            """);
        Exec(conn, "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at INTEGER NOT NULL, description TEXT NOT NULL);");
        Exec(conn, "CREATE TABLE external_extract_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER NOT NULL);");
        Exec(conn, "INSERT INTO schema_version (version, applied_at, description) VALUES (26, 0, 'test');");
        Exec(conn, "INSERT INTO external_extract_metadata (key, value, updated_at) VALUES ('extract_contract_version', '1', 0);");

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
                "INSERT INTO symbols (id, name, kind, language, file_path, signature, start_line, parent_id, metadata) " +
                "VALUES ($id, $name, $kind, $lang, $fp, $sig, $sl, $pid, NULL);";
            var pId = Add(cmd, "$id");
            var pName = Add(cmd, "$name");
            var pKind = Add(cmd, "$kind");
            var pLang = Add(cmd, "$lang");
            var pFp = Add(cmd, "$fp");
            var pSig = Add(cmd, "$sig");
            var pSl = Add(cmd, "$sl");
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
                pPid.Value = (object?)s.ParentId ?? DBNull.Value;
                cmd.ExecuteNonQuery();
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
