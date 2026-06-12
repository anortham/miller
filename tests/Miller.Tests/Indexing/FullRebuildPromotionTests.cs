using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the build-to-temp-then-promote contract behind full (force) rebuilds (the 2026-06-11 Eros field
/// report #2 fix): a force scan extracts into <c>symbols.db.rebuild</c> and <see cref="FullRebuildPromotion"/>
/// atomically replaces the live artifact, so julie-extract never merges in-place into a served DB whose WAL
/// live readers keep from checkpointing (~7KB/s effective throughput on a 2GB artifact). The promote must
/// leave a SELF-CONTAINED single file: a leftover rebuild WAL is folded in, and the live DB's old -wal/-shm
/// are removed so the promoted file can never pair with stale sidecars (cross-inode WAL replay corrupts).
/// </summary>
public sealed class FullRebuildPromotionTests : IDisposable
{
    private readonly string _dir;

    public FullRebuildPromotionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-rebuild-promotion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string DbPath => Path.Combine(_dir, "symbols.db");

    [Fact]
    public void RebuildDbPathFor_AppendsTheRebuildSuffix()
    {
        Assert.Equal(DbPath + ".rebuild", FullRebuildPromotion.RebuildDbPathFor(DbPath));
    }

    [Fact]
    public void PrepareRebuildTarget_DeletesAStaleRebuildTrio()
    {
        string rebuild = FullRebuildPromotion.RebuildDbPathFor(DbPath);
        File.WriteAllText(rebuild, "stale");
        File.WriteAllText(rebuild + "-wal", "stale");
        File.WriteAllText(rebuild + "-shm", "stale");

        FullRebuildPromotion.PrepareRebuildTarget(DbPath);

        Assert.False(File.Exists(rebuild));
        Assert.False(File.Exists(rebuild + "-wal"));
        Assert.False(File.Exists(rebuild + "-shm"));
    }

    [Fact]
    public void Promote_ReplacesTheLiveDb_WithTheRebuiltArtifact()
    {
        WriteMarkerDb(DbPath, "before-rebuild");
        WriteMarkerDb(FullRebuildPromotion.RebuildDbPathFor(DbPath), "after-rebuild");

        FullRebuildPromotion.Promote(DbPath);

        Assert.Equal("after-rebuild", ReadMarker(DbPath));
        Assert.False(File.Exists(FullRebuildPromotion.RebuildDbPathFor(DbPath)));
    }

    [Fact]
    public void Promote_NoLiveDbYet_StillPromotes()
    {
        WriteMarkerDb(FullRebuildPromotion.RebuildDbPathFor(DbPath), "first-build");

        FullRebuildPromotion.Promote(DbPath);

        Assert.Equal("first-build", ReadMarker(DbPath));
    }

    [Fact]
    public void Promote_MissingRebuildArtifact_ThrowsNamingThePath()
    {
        WriteMarkerDb(DbPath, "live");

        var ex = Assert.Throws<InvalidOperationException>(() => FullRebuildPromotion.Promote(DbPath));

        Assert.Contains(FullRebuildPromotion.RebuildDbPathFor(DbPath), ex.Message);
        Assert.Equal("live", ReadMarker(DbPath)); // the live artifact is untouched on failure
    }

    [Fact]
    public void Promote_RemovesTheLiveDbsOldWalAndShm()
    {
        // Stale sidecars from the replaced artifact must go: a reader opening the PROMOTED db next to an OLD
        // -wal would attempt cross-inode WAL recovery and read garbage pages.
        WriteMarkerDb(DbPath, "before-rebuild");
        File.WriteAllText(DbPath + "-wal", "stale-wal-of-the-old-artifact");
        File.WriteAllText(DbPath + "-shm", "stale-shm-of-the-old-artifact");
        WriteMarkerDb(FullRebuildPromotion.RebuildDbPathFor(DbPath), "after-rebuild");

        FullRebuildPromotion.Promote(DbPath);

        Assert.False(File.Exists(DbPath + "-wal"));
        Assert.False(File.Exists(DbPath + "-shm"));
        Assert.Equal("after-rebuild", ReadMarker(DbPath));
    }

    [Fact]
    public void Promote_FoldsAnUncheckpointedRebuildWal_IntoTheSinglepromotedFile()
    {
        // Fabricate a rebuilt artifact whose committed rows live ONLY in its WAL (the shape a killed-after-
        // commit / PERSIST_WAL writer leaves behind): write with wal_autocheckpoint=0 and snapshot db+wal
        // while the writer is still open, BEFORE its close-checkpoint folds the frames.
        WriteMarkerDb(DbPath, "before-rebuild");
        string rebuild = FullRebuildPromotion.RebuildDbPathFor(DbPath);
        string source = Path.Combine(_dir, "wal-source.db");
        using (var writer = OpenReadWriteCreate(source))
        {
            Exec(writer, "PRAGMA journal_mode=WAL;");
            Exec(writer, "PRAGMA wal_autocheckpoint=0;");
            Exec(writer, "CREATE TABLE marker (value TEXT NOT NULL);");
            Exec(writer, "INSERT INTO marker (value) VALUES ('lived-only-in-the-wal');");
            Assert.True(new FileInfo(source + "-wal").Length > 0);
            File.Copy(source, rebuild);
            File.Copy(source + "-wal", rebuild + "-wal");
        }

        FullRebuildPromotion.Promote(DbPath);

        Assert.Equal("lived-only-in-the-wal", ReadMarker(DbPath));
        Assert.False(File.Exists(rebuild));
        Assert.False(File.Exists(rebuild + "-wal"));
        Assert.False(File.Exists(rebuild + "-shm"));
    }

    private static SqliteConnection OpenReadWriteCreate(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void WriteMarkerDb(string dbPath, string marker)
    {
        using var connection = OpenReadWriteCreate(dbPath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE marker (value TEXT NOT NULL); INSERT INTO marker (value) VALUES ($value);";
        cmd.Parameters.AddWithValue("$value", marker);
        cmd.ExecuteNonQuery();
    }

    private static string ReadMarker(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM marker;";
        return (string)cmd.ExecuteScalar()!;
    }
}
