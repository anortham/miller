using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

internal static class SqliteSchemaObjects
{
    public static bool Exists(SqliteConnection connection, string name)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM sqlite_master
            WHERE type = 'table' AND name = $name
            UNION ALL
            SELECT 1 FROM sqlite_temp_master
            WHERE type IN ('table','view') AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }
}
