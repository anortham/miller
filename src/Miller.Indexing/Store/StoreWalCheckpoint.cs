using Microsoft.Data.Sqlite;

namespace Miller.Indexing.Store;

public enum StoreWalCheckpointStatus
{
    Ok,
    Busy,
    Skipped,
}

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
        File.WriteAllBytes(path, []);
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

    public static StoreWalCheckpointStatus TryTruncateFamily(string storeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        string currentPath = Path.Combine(storeRoot, "CURRENT");
        if (!File.Exists(currentPath))
            return StoreWalCheckpointStatus.Skipped;

        string generationName;
        try
        {
            generationName = File.ReadAllText(currentPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StoreWalCheckpointStatus.Skipped;
        }

        if (string.IsNullOrWhiteSpace(generationName) ||
            generationName.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(generationName))
        {
            return StoreWalCheckpointStatus.Skipped;
        }

        string storeDb = Path.Combine(storeRoot, generationName, "store.db");
        string coordDb = Path.Combine(storeRoot, "coord.db");
        StoreWalCheckpointStatus store = TryTruncate(storeDb, timeoutSeconds: 300);
        StoreWalCheckpointStatus coord = TryTruncate(coordDb, timeoutSeconds: 300);
        if (store == StoreWalCheckpointStatus.Busy || coord == StoreWalCheckpointStatus.Busy)
            return StoreWalCheckpointStatus.Busy;
        if (store == StoreWalCheckpointStatus.Ok || coord == StoreWalCheckpointStatus.Ok)
            return StoreWalCheckpointStatus.Ok;
        return StoreWalCheckpointStatus.Skipped;
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
