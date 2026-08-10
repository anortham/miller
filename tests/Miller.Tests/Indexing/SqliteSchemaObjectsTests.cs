using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SqliteSchemaObjectsTests
{
    [Fact]
    public void ExistsFindsMainTablesAndFamilyStoreTempViews()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE main_table (value TEXT); CREATE TEMP VIEW temp_view AS SELECT 1 AS value;";
            command.ExecuteNonQuery();
        }

        Assert.True(SqliteSchemaObjects.Exists(connection, "main_table"));
        Assert.True(SqliteSchemaObjects.Exists(connection, "temp_view"));
        Assert.False(SqliteSchemaObjects.Exists(connection, "missing"));
    }
}
