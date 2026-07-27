using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Tests.Indexing;

namespace Miller.Tests.Server;

/// <summary>
/// A fast bulk writer for a large synthesized current julie-extract DB, used only by the Scale
/// <see cref="RebuildLatencyTests"/>. Unlike <see cref="Miller.Tests.Indexing.JulieDbFixture"/> (which inserts
/// row-by-row for fidelity on small fixtures), this inserts tens of thousands of symbols inside ONE transaction
/// with prepared, reused commands. Schema ownership stays in <see cref="JulieDbFixture.EnsureCurrentSchema"/>
/// so the scale fixture cannot drift behind the production gate.
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
        Exec(conn, "PRAGMA foreign_keys=OFF;");
        Exec(conn, "PRAGMA synchronous=OFF;");
        JulieDbFixture.EnsureCurrentSchema(conn);
        WriteMetadata(conn);

        using var tx = conn.BeginTransaction();

        var distinctFiles = new HashSet<string>(StringComparer.Ordinal);
        using (var fcmd = conn.CreateCommand())
        {
            fcmd.Transaction = tx;
            fcmd.CommandText =
                "INSERT OR IGNORE INTO files (file_id, path, language, content_hash, content_bytes, line_count, " +
                "indexed_at, last_revision_id, status, metadata_json) " +
                "VALUES ($fid, $p, 'csharp', 'blake3:00', 0, 0, '1970-01-01T00:00:00Z', 0, 'indexed', NULL);";
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
                "end_line, parent_symbol_id, visibility, is_test, test_container, test_lifecycle, metadata_json) " +
                "VALUES ($id, $fid, $p, $lang, $name, $kind, $sig, $sl, $el, $pid, $visibility, $istest, $tcont, $tlife, NULL);";
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
            var pVisibility = Add(cmd, "$visibility");
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
                pVisibility.Value = (object?)s.Visibility ?? DBNull.Value;
                pIsTest.Value = s.IsTest ? 1 : 0;
                pTestContainer.Value = s.TestContainer ? 1 : 0;
                pTestLifecycle.Value = s.TestLifecycle ? 1 : 0;
                cmd.ExecuteNonQuery();
            }
        }

        if (symbols.Count > 1)
        {
            using var siteCommand = conn.CreateCommand();
            siteCommand.Transaction = tx;
            siteCommand.CommandText =
                "INSERT INTO reference_sites " +
                "(reference_site_id, file_id, path, language, containing_symbol_id, is_exact, provenance) " +
                "VALUES ($id, $fid, $path, $language, $containing, 0, 'spanless');";
            var siteId = Add(siteCommand, "$id");
            var siteFileId = Add(siteCommand, "$fid");
            var sitePath = Add(siteCommand, "$path");
            var siteLanguage = Add(siteCommand, "$language");
            var siteContaining = Add(siteCommand, "$containing");
            siteCommand.Prepare();

            using var rcmd = conn.CreateCommand();
            rcmd.Transaction = tx;
            rcmd.CommandText =
                "INSERT INTO relationships " +
                "(relationship_id, reference_site_id, from_symbol_id, to_symbol_id, file_id, path, kind) " +
                "VALUES ($id, $site, $from, $to, $fid, $path, 'calls');";
            var rId = Add(rcmd, "$id");
            var rSite = Add(rcmd, "$site");
            var rFrom = Add(rcmd, "$from");
            var rTo = Add(rcmd, "$to");
            var rFileId = Add(rcmd, "$fid");
            var rPath = Add(rcmd, "$path");
            rcmd.Prepare();
            for (int i = 0; i < symbols.Count; i++)
            {
                IndexedSymbol source = symbols[i];
                string relationshipId = "rel" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string referenceSiteId = "site:" + relationshipId;
                string fileId = FileId(source.FilePath);

                siteId.Value = referenceSiteId;
                siteFileId.Value = fileId;
                sitePath.Value = source.FilePath;
                siteLanguage.Value = source.Language;
                siteContaining.Value = source.SymbolId;
                siteCommand.ExecuteNonQuery();

                rId.Value = relationshipId;
                rSite.Value = referenceSiteId;
                rFrom.Value = source.SymbolId;
                rTo.Value = symbols[(i + 1) % symbols.Count].SymbolId;
                rFileId.Value = fileId;
                rPath.Value = source.FilePath;
                rcmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private static string FileId(string path) => "file:" + path;

    private static void WriteMetadata(SqliteConnection connection)
    {
        foreach ((string key, string value) in new[]
        {
            ("artifact_id", "artifact-large-db"),
            ("root_path", "/work/repo"),
            ("binary_version", MillerExtractContract.PinnedJulieExtractVersion),
            ("hash_algorithm", MillerExtractContract.ExpectedHashAlgorithm),
            ("parser_inventory_fingerprint", "sha256:" + new string('a', 64)),
            ("capability_snapshot_fingerprint", "sha256:" + new string('b', 64)),
            ("created_at", "1970-01-01T00:00:00Z"),
            ("updated_at", "1970-01-01T00:00:00Z"),
            ("sqlite_schema_version", MillerExtractContract.ExpectedSchemaVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            ("schema_version", MillerExtractContract.ExpectedSchemaVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            ("extract_contract_version", MillerExtractContract.ExpectedExtractContractVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO artifact_metadata (key, value) VALUES ($key, $value);";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
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
