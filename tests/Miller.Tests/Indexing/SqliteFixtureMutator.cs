using Microsoft.Data.Sqlite;

namespace Miller.Tests.Indexing;

internal static class SqliteFixtureMutator
{
    public static void DropRelationshipsTable(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE relationships;";
        command.ExecuteNonQuery();
    }
}
