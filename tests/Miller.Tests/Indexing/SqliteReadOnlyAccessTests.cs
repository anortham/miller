using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the shared read-only open discipline for julie extract DBs. The replaced-file case is load-bearing:
/// <c>julie-extract scan --force</c> heals an incompatible artifact by DELETING and RECREATING symbols.db, so a
/// pooled read connection surviving Dispose() would keep an fd to the unlinked old inode and every later read in
/// the same process would keep seeing the OLD database — the 2026-06-11 Eros fleet finding where <c>patterns</c>
/// kept failing with a schema-2 error after a successful <c>workspace full</c> rebuild.
/// </summary>
public sealed class SqliteReadOnlyAccessTests : IDisposable
{
    private readonly string _dir;

    public SqliteReadOnlyAccessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ro-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Open_AfterTheDbFileIsReplaced_SeesTheNewDatabase()
    {
        string dbPath = Path.Combine(_dir, "symbols.db");
        WriteMarkerDb(dbPath, "before-rebuild");
        using (SqliteConnection first = SqliteReadOnlyAccess.Open(dbPath))
            Assert.Equal("before-rebuild", ReadMarker(first));

        File.Delete(dbPath);
        WriteMarkerDb(dbPath, "after-rebuild");

        using SqliteConnection second = SqliteReadOnlyAccess.Open(dbPath);
        Assert.Equal("after-rebuild", ReadMarker(second));
    }

    private static void WriteMarkerDb(string dbPath, string marker)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE marker (value TEXT NOT NULL); INSERT INTO marker (value) VALUES ($value);";
        cmd.Parameters.AddWithValue("$value", marker);
        cmd.ExecuteNonQuery();
    }

    private static string ReadMarker(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM marker;";
        return (string)cmd.ExecuteScalar()!;
    }
}
