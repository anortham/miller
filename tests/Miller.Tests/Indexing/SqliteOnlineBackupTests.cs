using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the rebind copy protocol (contract design §4): a page-stepped SQLite online backup that is consistent
/// under a live source writer, writes NOTHING to the source database file, honours a wall-clock budget between
/// steps, and deletes its partial destination trio on every non-completed exit.
/// </summary>
public sealed class SqliteOnlineBackupTests : IDisposable
{
    private readonly string _dir;

    public SqliteOnlineBackupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-online-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string SourceDb => Path.Combine(_dir, "symbols.db");

    private string DestinationDb => Path.Combine(_dir, "symbols.db.rebuild");

    [Fact]
    public void Copy_PopulatedDatabase_ProducesAnIntactRowEqualCopy()
    {
        WriteRows(SourceDb, 2000);

        BackupOutcome outcome = SqliteOnlineBackup.Copy(
            SourceDb, DestinationDb, TimeSpan.FromMinutes(5), () => DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(BackupOutcome.Kind.Completed, outcome.Result);
        Assert.Equal("ok", IntegrityCheck(DestinationDb));
        Assert.Equal(RowCount(SourceDb), RowCount(DestinationDb));
    }

    [Fact]
    public void Copy_CompletedCopy_LeavesASelfContainedDestinationFile()
    {
        WriteRows(SourceDb, 2000);

        SqliteOnlineBackup.Copy(
            SourceDb, DestinationDb, TimeSpan.FromMinutes(5), () => DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.True(File.Exists(DestinationDb));
        Assert.False(File.Exists(DestinationDb + "-wal"));
        Assert.False(File.Exists(DestinationDb + "-shm"));
    }

    [Fact]
    public void Copy_BudgetElapsedBetweenSteps_ReportsExhaustedAndDeletesThePartialDestination()
    {
        WriteRows(SourceDb, 4000);
        string sourceHashBefore = HashOf(SourceDb);

        BackupOutcome outcome = SqliteOnlineBackup.Copy(
            SourceDb,
            DestinationDb,
            TimeSpan.FromMinutes(1),
            ClockSteadyForThenJumping(reads: 2, jump: TimeSpan.FromMinutes(2)),
            pagesPerStep: 1,
            CancellationToken.None);

        Assert.Equal(BackupOutcome.Kind.BudgetExhausted, outcome.Result);
        Assert.False(File.Exists(DestinationDb));
        Assert.False(File.Exists(DestinationDb + "-wal"));
        Assert.False(File.Exists(DestinationDb + "-shm"));
        Assert.Equal(sourceHashBefore, HashOf(SourceDb));
    }

    [Fact]
    public void Copy_CancelledToken_ThrowsAndDeletesThePartialDestination()
    {
        WriteRows(SourceDb, 2000);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SqliteOnlineBackup.Copy(
            SourceDb, DestinationDb, TimeSpan.FromMinutes(5), () => DateTimeOffset.UnixEpoch, cancellation.Token));

        Assert.False(File.Exists(DestinationDb));
        Assert.False(File.Exists(DestinationDb + "-wal"));
        Assert.False(File.Exists(DestinationDb + "-shm"));
    }

    [Fact]
    public void Copy_SourceWithALiveWriterConnection_CopiesEveryCommittedRowWithoutTouchingTheSourceFile()
    {
        using SqliteConnection writer = OpenReadWriteCreate(SourceDb);
        Exec(writer, "PRAGMA journal_mode=WAL;");
        Exec(writer, "CREATE TABLE symbols (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");
        InsertRows(writer, 2000);
        string sourceHashBefore = HashOf(SourceDb);

        BackupOutcome outcome = SqliteOnlineBackup.Copy(
            SourceDb, DestinationDb, TimeSpan.FromMinutes(5), () => DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(BackupOutcome.Kind.Completed, outcome.Result);
        Assert.Equal("ok", IntegrityCheck(DestinationDb));
        Assert.Equal(2000, RowCount(DestinationDb));
        Assert.Equal(sourceHashBefore, HashOf(SourceDb));
    }

    [Fact]
    public void Copy_MissingSource_ReportsFailureNamingThePath()
    {
        BackupOutcome outcome = SqliteOnlineBackup.Copy(
            SourceDb, DestinationDb, TimeSpan.FromMinutes(5), () => DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(BackupOutcome.Kind.Failed, outcome.Result);
        Assert.Contains("symbols.db", outcome.FailureReason);
        Assert.False(File.Exists(DestinationDb));
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("0.5", 0.5)]
    [InlineData("00:00:42", 42)]
    [InlineData("00:04:00", 240)]
    public void ResolveBudget_ReadsSecondsAndTimeSpanSpellings(string value, double expectedSeconds)
    {
        TimeSpan budget = SqliteOnlineBackup.ResolveBudget(
            name => name == SqliteOnlineBackup.CopyBudgetEnvironmentVariable ? value : null);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), budget);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-00:00:01")]
    [InlineData("not-a-budget")]
    public void ResolveBudget_UnsetOrInvalid_FallsBackToThreeMinutes(string? value)
    {
        TimeSpan budget = SqliteOnlineBackup.ResolveBudget(
            name => name == SqliteOnlineBackup.CopyBudgetEnvironmentVariable ? value : null);

        Assert.Equal(TimeSpan.FromMinutes(3), budget);
    }

    private static Func<DateTimeOffset> ClockSteadyForThenJumping(int reads, TimeSpan jump)
    {
        int taken = 0;
        return () => Interlocked.Increment(ref taken) <= reads
            ? DateTimeOffset.UnixEpoch
            : DateTimeOffset.UnixEpoch + jump;
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

    private static SqliteConnection OpenReadOnly(string dbPath)
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

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void WriteRows(string dbPath, int rows)
    {
        using SqliteConnection connection = OpenReadWriteCreate(dbPath);
        Exec(connection, "CREATE TABLE symbols (id INTEGER PRIMARY KEY, name TEXT NOT NULL);");
        InsertRows(connection, rows);
    }

    private static void InsertRows(SqliteConnection connection, int rows)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO symbols (name) VALUES ($name);";
        SqliteParameter name = cmd.CreateParameter();
        name.ParameterName = "$name";
        cmd.Parameters.Add(name);
        for (int i = 0; i < rows; i++)
        {
            name.Value = $"symbol-{i}-{new string('x', 120)}";
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static long RowCount(string dbPath)
    {
        using SqliteConnection connection = OpenReadOnly(dbPath);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string IntegrityCheck(string dbPath)
    {
        using SqliteConnection connection = OpenReadOnly(dbPath);
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        return (string)cmd.ExecuteScalar()!;
    }

    private static string HashOf(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
