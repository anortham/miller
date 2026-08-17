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
        StoreWalCheckpointStatus store = TryTruncate(storeDb);
        StoreWalCheckpointStatus coord = TryTruncate(coordDb);
        if (store == StoreWalCheckpointStatus.Busy || coord == StoreWalCheckpointStatus.Busy)
            return StoreWalCheckpointStatus.Busy;
        if (store == StoreWalCheckpointStatus.Ok || coord == StoreWalCheckpointStatus.Ok)
            return StoreWalCheckpointStatus.Ok;
        return StoreWalCheckpointStatus.Skipped;
    }

    public static StoreWalCheckpointStatus TryTruncate(string databasePath)
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
                DefaultTimeout = 1,
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
        catch (SqliteException)
        {
            return StoreWalCheckpointStatus.Busy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StoreWalCheckpointStatus.Skipped;
        }
    }
}
