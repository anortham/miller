using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Support;

public sealed class StoreFixtureTests
{
    private const int SqliteFileChangeCounterOffset = 24;

    [Fact]
    public void Create_CommitsSchemaAndSeedDataInOneDeleteJournalTransaction()
    {
        using StoreFixture fixture = StoreFixture.Create();
        string databasePath = Path.Combine(fixture.Binding.StoreRoot, "gen-001", "store.db");
        byte[] header = new byte[100];
        using (FileStream database = File.OpenRead(databasePath))
            database.ReadExactly(header);

        Assert.Equal((byte)1, header[18]);
        Assert.Equal((byte)1, header[19]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(SqliteFileChangeCounterOffset, 4)));

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", command.ExecuteScalar());
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("delete", command.ExecuteScalar());
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name;";
        var tables = new List<string>();
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read())
                tables.Add(reader.GetString(0));
        Assert.Equal(new[]
        {
            "file_versions", "identifiers", "manifest_entries", "manifests", "parse_diagnostics",
            "pending_relationships", "reference_sites", "relationships", "source_regions", "store_log",
            "store_meta", "structural_facts", "symbols", "type_facts", "views",
        }, tables);

        long[] expectedRows = [2, 0, 2, 2, 0, 0, 0, 0, 1, 2, 7, 2, 2, 0, 1];
        for (int index = 0; index < tables.Count; index++)
        {
            command.CommandText = $"SELECT COUNT(*) FROM {tables[index]};";
            Assert.Equal(expectedRows[index], command.ExecuteScalar());
        }

        command.CommandText = """
            SELECT v.root, v.current_generation, v.resolution_state, m.manifest_hash, s.name
            FROM views v JOIN manifests m ON m.view_id=v.view_id AND m.generation=v.current_generation
            JOIN manifest_entries e ON e.view_id=m.view_id AND e.generation=m.generation
            JOIN symbols s ON s.version_id=e.version_id
            WHERE v.view_id='view-a';
            """;
        using SqliteDataReader visible = command.ExecuteReader();
        Assert.True(visible.Read());
        Assert.Equal(fixture.Binding.WorkspaceRoot, visible.GetString(0));
        Assert.Equal(2, visible.GetInt64(1));
        Assert.Equal("unbound", visible.GetString(2));
        Assert.Equal("manifest-current", visible.GetString(3));
        Assert.Equal("Visible", visible.GetString(4));
        Assert.False(visible.Read());
    }
}
