using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Live proof of the build-to-temp force scan (2026-06-11 Eros field report #2): a real
/// <c>julie-extract scan --force</c> over an EXISTING artifact must REPLACE the file (extract into
/// <c>symbols.db.rebuild</c>, promote) rather than merge in-place — pinned by a marker table injected into
/// the old artifact vanishing, the julie <c>artifact_id</c> changing, and no rebuild trio surviving. Spawns
/// the pinned binary, so it is <c>[Trait("Category","Scale")]</c> and obtains the binary via
/// <see cref="ScaleTestSupport.RequireJulieServer"/>; SKIPS when <c>.tools/julie-extract</c> is absent.
/// </summary>
[Trait("Category", "Scale")]
public sealed class FullRebuildScanScaleTests
{
    [Fact]
    public void ForceScan_OverAnExistingArtifact_ReplacesTheFileInsteadOfMergingInPlace()
    {
        string binary = ScaleTestSupport.RequireJulieServer();

        string work = Path.Combine(Path.GetTempPath(), "miller-full-rebuild-" + Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(work, "repo");
        string db = Path.Combine(work, ".miller", "symbols.db");
        Directory.CreateDirectory(repo);
        try
        {
            File.WriteAllText(Path.Combine(repo, "widget.cs"), """
                namespace Demo;

                public sealed class RebuildWidget
                {
                    public int MillerRebuildMarker(int size) => size;
                }
                """);

            var runner = new JulieExtractRunner(binary);
            ExtractReport first = runner.Scan(repo, db);
            Assert.NotEqual("failed", first.Status);
            string? oldArtifactId = ReadArtifactId(db);
            Assert.False(string.IsNullOrWhiteSpace(oldArtifactId));

            // Brand the OLD artifact so in-place merging is detectable: a true file replacement loses the
            // marker table; an in-place merge would carry it through.
            using (var brand = OpenReadWrite(db))
            {
                using var cmd = brand.CreateCommand();
                cmd.CommandText = "CREATE TABLE miller_test_marker (value TEXT NOT NULL);";
                cmd.ExecuteNonQuery();
            }

            ExtractReport rebuilt = runner.Scan(repo, db, force: true);

            // File-state assertions FIRST: any later read-only open legitimately re-creates empty -wal/-shm
            // sidecars (the WAL trap — a reader writes the wal-index), which must not fail these checks.
            Assert.NotEqual("failed", rebuilt.Status);
            string rebuild = FullRebuildPromotion.RebuildDbPathFor(db);
            Assert.False(File.Exists(rebuild), "the promoted rebuild file must not linger");
            Assert.False(File.Exists(rebuild + "-wal"));
            Assert.False(File.Exists(rebuild + "-shm"));
            Assert.False(File.Exists(db + "-wal"), "the promoted artifact must be a self-contained single file");

            Assert.False(TableExists(db, "miller_test_marker"), "the old artifact must have been replaced, not merged into");
            Assert.NotEqual(oldArtifactId, ReadArtifactId(db));

            var symbols = SqliteSymbolReader.Read(db);
            Assert.Contains(symbols, s => s.Name == "MillerRebuildMarker");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
        }
    }

    private static SqliteConnection OpenReadWrite(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string? ReadArtifactId(string dbPath)
    {
        using SqliteConnection connection = SqliteReadOnlyAccessTestSeam.Open(dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'artifact_id';";
        return cmd.ExecuteScalar() as string;
    }

    private static bool TableExists(string dbPath, string table)
    {
        using SqliteConnection connection = SqliteReadOnlyAccessTestSeam.Open(dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}

/// <summary>Read-only open with the production discipline for test assertions (Pooling=false, Mode=ReadOnly).</summary>
file static class SqliteReadOnlyAccessTestSeam
{
    public static SqliteConnection Open(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
