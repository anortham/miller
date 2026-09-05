using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Store;

public enum StoreWalCheckpointStatus
{
    Ok,
    Busy,
    Skipped,
}

public sealed record StoreWalObservation(long? StoreBytes, long? CoordinatorBytes, double? DebtAgeSeconds)
{
    public bool NeedsCheckpoint => StoreBytes is null or > 0 || CoordinatorBytes is null or > 0 || DebtAgeSeconds is not null;
    public bool NeedsWarning => StoreBytes is null or >= 268435456 || CoordinatorBytes is null or >= 268435456 || DebtAgeSeconds >= 300;
}

public sealed record StoreWalCheckpointReport(
    string StoreRoot, StoreWalCheckpointStatus Status, StoreWalObservation Before,
    StoreWalObservation After, TimeSpan Elapsed);

public static class StoreWalCheckpoint
{
    public const string OwedFileName = "wal-checkpoint-owed";

    public static string OwedPath(string storeRoot) =>
        Path.Combine(Path.GetFullPath(storeRoot), OwedFileName);

    public static void MarkOwed(string storeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        string path = OwedPath(storeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var marker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Repeated writes must not reset how long cleanup has been owed.
        }
    }

    public static bool IsOwed(string storeRoot)
    {
        try
        {
            return File.Exists(OwedPath(storeRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void ClearOwed(string storeRoot)
    {
        try
        {
            string path = OwedPath(storeRoot);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Clears checkpoint debt only after both family databases checkpoint successfully.</summary>
    public static StoreWalCheckpointStatus TryCompleteOwedFamily(string storeRoot)
    {
        StoreWalCheckpointStatus status = TryTruncateFamily(storeRoot);
        if (status == StoreWalCheckpointStatus.Ok)
            ClearOwed(storeRoot);
        return status;
    }

    /// <summary>Read-only filesystem observation. A missing WAL is empty; an unreadable layout is unknown.</summary>
    public static StoreWalObservation Observe(string storeRoot)
    {
        string? generation = ReadGeneration(storeRoot);
        double? age = null;
        try
        {
            var marker = new FileInfo(OwedPath(storeRoot));
            if (marker.Exists)
                age = Math.Max(0, (DateTime.UtcNow - marker.LastWriteTimeUtc).TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return new StoreWalObservation(
            generation is null ? null : WalBytes(Path.Combine(storeRoot, generation, "store.db")),
            WalBytes(Path.Combine(storeRoot, "coord.db")), age);
    }

    /// <summary>Called only on maintenance/write paths, never from freshness-disabled reads.</summary>
    public static StoreWalCheckpointReport? Maintain(string storeRoot)
    {
        StoreWalObservation before = Observe(storeRoot);
        if (!before.NeedsCheckpoint)
            return null;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        StoreWalCheckpointStatus status;
        try
        {
            MarkOwed(storeRoot);
            status = TryCompleteOwedFamily(storeRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status = StoreWalCheckpointStatus.Skipped;
        }
        return new StoreWalCheckpointReport(Path.GetFullPath(storeRoot), status, before,
            Observe(storeRoot), System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    private static long? WalBytes(string database)
    {
        try
        {
            // FileInfo.Exists hides access errors. GetAttributes distinguishes absence from failure.
            _ = File.GetAttributes(database);
            return new FileInfo(database + "-wal").Length;
        }
        catch (FileNotFoundException ex)
        {
            return string.Equals(ex.FileName, database, StringComparison.Ordinal) ? null : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static StoreWalCheckpointStatus TryTruncateFamily(string storeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        string? generationName = ReadGeneration(storeRoot);
        if (generationName is null)
            return StoreWalCheckpointStatus.Skipped;

        string storeDb = Path.Combine(storeRoot, generationName, "store.db");
        string coordDb = Path.Combine(storeRoot, "coord.db");
        // Cleanup is retryable maintenance. Do not turn an exclusive writer lock into
        // a five-minute refresh/idle stall. This bounds lock waiting, not disk I/O.
        StoreWalCheckpointStatus store = TryTruncate(storeDb, timeoutSeconds: 1);
        StoreWalCheckpointStatus coord = TryTruncate(coordDb, timeoutSeconds: 1);
        if (store == StoreWalCheckpointStatus.Busy || coord == StoreWalCheckpointStatus.Busy)
            return StoreWalCheckpointStatus.Busy;
        if (store == StoreWalCheckpointStatus.Ok && coord == StoreWalCheckpointStatus.Ok)
            return StoreWalCheckpointStatus.Ok;
        return StoreWalCheckpointStatus.Skipped;
    }

    private static string? ReadGeneration(string storeRoot)
    {
        string currentPath = Path.Combine(storeRoot, "CURRENT");
        if (!File.Exists(currentPath))
            return null;

        string generationName;
        try
        {
            generationName = File.ReadAllText(currentPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(generationName) ||
            generationName.Contains("..", StringComparison.Ordinal) ||
            generationName == "." || generationName.Contains('/') || generationName.Contains('\\') ||
            Path.IsPathRooted(generationName))
        {
            return null;
        }
        return generationName;
    }

    public static StoreWalCheckpointStatus TryTruncate(string databasePath, int timeoutSeconds = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
            return StoreWalCheckpointStatus.Skipped;

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = timeoutSeconds,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return StoreWalCheckpointStatus.Skipped;
            return reader.GetInt32(0) == 0
                ? StoreWalCheckpointStatus.Ok
                : StoreWalCheckpointStatus.Busy;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return StoreWalCheckpointStatus.Busy;
        }
        catch (SqliteException)
        {
            return StoreWalCheckpointStatus.Skipped;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StoreWalCheckpointStatus.Skipped;
        }
    }
}
