using Microsoft.Data.Sqlite;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreWalCheckpointTests
{
    [Fact]
    public void TruncateReportsBusyWhileAWriterTransactionIsHeld()
    {
        using var dir = new TempDir();
        string database = Path.Combine(dir.Root, "store.db");
        using var setup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
        }.ToString());
        setup.Open();
        using (SqliteCommand command = setup.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (1);";
            command.ExecuteNonQuery();
        }

        using var holder = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        holder.Open();
        using (SqliteCommand begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE; INSERT INTO t VALUES (2);";
            begin.ExecuteNonQuery();
        }

        Assert.Equal(StoreWalCheckpointStatus.Busy, StoreWalCheckpoint.TryTruncate(database));

        using (SqliteCommand rollback = holder.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        holder.Dispose();
        Assert.Equal(StoreWalCheckpointStatus.Ok, StoreWalCheckpoint.TryTruncate(database));
    }

    [Fact]
    public void OwedFlagRoundTripsOnTheStoreRoot()
    {
        using var dir = new TempDir();
        Assert.False(StoreWalCheckpoint.IsOwed(dir.Root));
        StoreWalCheckpoint.MarkOwed(dir.Root);
        Assert.True(StoreWalCheckpoint.IsOwed(dir.Root));
        StoreWalCheckpoint.ClearOwed(dir.Root);
        Assert.False(StoreWalCheckpoint.IsOwed(dir.Root));
    }

    [Fact]
    public void MissingDatabaseIsSkipped()
    {
        Assert.Equal(
            StoreWalCheckpointStatus.Skipped,
            StoreWalCheckpoint.TryTruncate(Path.Combine(Path.GetTempPath(), "missing-store-" + Guid.NewGuid() + ".db")));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "miller-wal-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
