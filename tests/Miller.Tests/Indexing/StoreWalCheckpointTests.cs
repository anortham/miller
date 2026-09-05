using Microsoft.Data.Sqlite;
using Miller.Indexing.Store;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreWalCheckpointTests
{
    [Fact]
    public void FamilyCheckpointDoesNotReportSuccessWhenTheCoordinatorIsUnreadable()
    {
        using StoreFixture fixture = StoreFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Binding.StoreRoot, "coord.db"), "not a SQLite database");
        StoreWalCheckpoint.MarkOwed(fixture.Binding.StoreRoot);

        Assert.Equal(StoreWalCheckpointStatus.Skipped, StoreWalCheckpoint.TryCompleteOwedFamily(fixture.Binding.StoreRoot));
        Assert.True(StoreWalCheckpoint.IsOwed(fixture.Binding.StoreRoot));
    }

    [Fact]
    public async Task FamilyCheckpointKeepsDebtUntilAnActiveReaderReleasesItsSnapshot()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string storeRoot = fixture.Binding.StoreRoot;
        string generation = Path.Combine(storeRoot, "gen-001");
        string database = Path.Combine(generation, "store.db");
        using var setup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
        }.ToString());
        setup.Open();
        using SqliteCommand write = setup.CreateCommand();
        write.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE t(x); INSERT INTO t VALUES (1);";
        write.ExecuteNonQuery();
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task readerTask = Task.Run(() =>
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "BEGIN; SELECT COUNT(*) FROM t;";
            command.ExecuteScalar();
            ready.Set();
            release.Wait(TimeSpan.FromSeconds(4), cancellationToken);
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }, cancellationToken);

        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            write.CommandText = "INSERT INTO t VALUES (2);";
            write.ExecuteNonQuery();
            StoreWalCheckpoint.MarkOwed(storeRoot);
            Assert.Equal(StoreWalCheckpointStatus.Busy, StoreWalCheckpoint.TryCompleteOwedFamily(storeRoot));
            Assert.True(StoreWalCheckpoint.IsOwed(storeRoot));
        }
        finally
        {
            release.Set();
            await readerTask;
        }
        Assert.Equal(StoreWalCheckpointStatus.Ok, StoreWalCheckpoint.TryCompleteOwedFamily(storeRoot));
        Assert.False(StoreWalCheckpoint.IsOwed(storeRoot));
    }

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
