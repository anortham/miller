using Microsoft.Data.Sqlite;
using Miller.Indexing.Store;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class StoreWalCheckpointTests
{
    [Fact]
    public async Task FamilyCheckpointDoesNotWaitForALongCoordinatorLock()
    {
        using StoreFixture fixture = StoreFixture.Create();
        using var writer = new SqliteConnection($"Data Source={Path.Combine(fixture.Binding.StoreRoot, "coord.db")};Pooling=False");
        writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "BEGIN EXCLUSIVE;";
        command.ExecuteNonQuery();
        StoreWalCheckpoint.MarkOwed(fixture.Binding.StoreRoot);
        Assert.True(StoreWalCheckpoint.IsOwed(fixture.Binding.StoreRoot));
        Task<StoreWalCheckpointReport?> attempt = Task.Run(() => StoreWalCheckpoint.Maintain(fixture.Binding.StoreRoot));
        bool returnedWhileLocked;
        try
        {
            returnedWhileLocked = await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken)) == attempt;
        }
        finally
        {
            command.CommandText = "ROLLBACK;";
            command.ExecuteNonQuery();
        }
        StoreWalCheckpointReport? report = await attempt;
        Assert.True(returnedWhileLocked, "A maintenance checkpoint must defer rather than wait behind a long writer lock.");
        Assert.Equal(StoreWalCheckpointStatus.Busy, report!.Status);
    }

    [Fact]
    public void RepeatedDebtMarksPreserveTheOriginalAge()
    {
        using var dir = new TempDir();
        StoreWalCheckpoint.MarkOwed(dir.Root);
        string marker = StoreWalCheckpoint.OwedPath(dir.Root);
        var original = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(marker, original);

        StoreWalCheckpoint.MarkOwed(dir.Root);

        Assert.Equal(original, File.GetLastWriteTimeUtc(marker));
    }

    [Fact]
    public void ObservationOfAnAbsentFamilyIsUnknownAndWritesNothing()
    {
        using var dir = new TempDir();
        string root = Path.Combine(dir.Root, "absent");
        StoreWalObservation observation = StoreWalCheckpoint.Observe(root);
        Assert.Null(observation.StoreBytes);
        Assert.Null(observation.CoordinatorBytes);
        Assert.Null(observation.DebtAgeSeconds);
        Assert.True(observation.NeedsWarning);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(268435455, 0, 299, false)]
    [InlineData(268435456, 0, 0, true)]
    [InlineData(0, 268435456, 0, true)]
    [InlineData(0, 0, 300, true)]
    public void WarningDetectsOversizedOrOverdueDebt(long store, long coord, double age, bool warning)
    {
        Assert.Equal(warning, new StoreWalObservation(store, coord, age).NeedsWarning);
    }

    [Fact]
    public void SustainedWritesRetainDebtAndRecoverOnANewMaintenanceInvocation()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string root = fixture.Binding.StoreRoot;
        string database = Path.Combine(root, "gen-001", "store.db");
        using var writer = new SqliteConnection($"Data Source={database};Pooling=False");
        writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE wal_batches(x); INSERT INTO wal_batches VALUES (zeroblob(4096));";
        command.ExecuteNonQuery();
        using var reader = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        reader.Open();
        using var read = reader.CreateCommand();
        read.CommandText = "BEGIN; SELECT COUNT(*) FROM wal_batches;";
        Assert.Equal(1L, read.ExecuteScalar());
        for (int batch = 0; batch < 3; batch++)
        {
            command.CommandText = "INSERT INTO wal_batches VALUES (zeroblob(4096));";
            command.ExecuteNonQuery();
            StoreWalCheckpointReport report = Assert.IsType<StoreWalCheckpointReport>(StoreWalCheckpoint.Maintain(root));
            Assert.Equal(StoreWalCheckpointStatus.Busy, report.Status);
            Assert.True(report.After.StoreBytes > 0);
            Assert.NotNull(report.After.DebtAgeSeconds);
        }
        File.SetLastWriteTimeUtc(StoreWalCheckpoint.OwedPath(root), DateTime.UtcNow.AddMinutes(-6));
        Assert.True(StoreWalCheckpoint.Maintain(root)!.After.NeedsWarning);
        read.CommandText = "ROLLBACK;";
        read.ExecuteNonQuery(); // Keep the idle connection alive so last-close cannot hide a missing checkpoint.

        StoreWalCheckpointReport recovered = Assert.IsType<StoreWalCheckpointReport>(StoreWalCheckpoint.Maintain(root));

        Assert.Equal(StoreWalCheckpointStatus.Ok, recovered.Status);
        Assert.Equal(0L, recovered.After.StoreBytes);
        Assert.False(StoreWalCheckpoint.IsOwed(root));
        Assert.Null(StoreWalCheckpoint.Maintain(root));
        command.CommandText = "SELECT COUNT(*) FROM wal_batches;";
        Assert.Equal(4L, command.ExecuteScalar());
    }

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
