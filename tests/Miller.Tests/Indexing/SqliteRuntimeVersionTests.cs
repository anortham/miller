using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SqliteRuntimeVersionTests
{
    [Fact]
    public void BundledRuntimeIncludesWalResetFix()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        string versionText = Assert.IsType<string>(command.ExecuteScalar());
        var version = Version.Parse(versionText);

        Assert.True(
            version >= new Version(3, 51, 3),
            $"Bundled SQLite {version} is below the required 3.51.3 minimum with the WAL-reset fix.");
    }
}
