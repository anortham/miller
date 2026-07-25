namespace Miller.Indexing;

/// <summary>
/// Read-only overlay for julie-extractors' optional <c>pattern_catalog</c> table.
/// When the table is missing, catalog lookups return empty and observed facts remain authoritative.
/// </summary>
public sealed class PatternCatalogReader
{
    public IReadOnlyDictionary<string, PatternCatalogEntry> Read(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        using Microsoft.Data.Sqlite.SqliteConnection connection = SqliteReadOnlyAccess.Open(dbPath);
        try
        {
            JulieSchemaGate.Verify(connection);
        }
        catch
        {
            return new Dictionary<string, PatternCatalogEntry>(StringComparer.Ordinal);
        }

        return Read(connection, transaction: null);
    }

    internal IReadOnlyDictionary<string, PatternCatalogEntry> Read(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!TableExists(connection, transaction, "pattern_catalog"))
            return new Dictionary<string, PatternCatalogEntry>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pattern_id, label, description, tags_json, expected_metadata_keys_json
            FROM pattern_catalog
            ORDER BY pattern_id;
            """;

        var entries = new Dictionary<string, PatternCatalogEntry>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string patternId = reader.GetString(0);
            entries[patternId] = new PatternCatalogEntry(
                PatternId: patternId,
                Label: reader.GetString(1),
                Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                TagsJson: reader.IsDBNull(3) ? null : reader.GetString(3),
                ExpectedMetadataKeysJson: reader.IsDBNull(4) ? null : reader.GetString(4));
        }

        return entries;
    }

    private static bool TableExists(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        object? result = command.ExecuteScalar();
        return result is not null and not DBNull;
    }
}

public sealed record PatternCatalogEntry(
    string PatternId,
    string Label,
    string? Description,
    string? TagsJson,
    string? ExpectedMetadataKeysJson);
