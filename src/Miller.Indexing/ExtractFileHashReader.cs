using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Reads julie's file-level freshness contract: <c>files.hash</c> plus
/// <c>external_extract_metadata.hash_algorithm</c>.
/// </summary>
public static class ExtractFileHashReader
{
    public static string? ReadFileHash(string dbPath, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var connection = Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hash FROM files WHERE path = $path;";
        command.Parameters.AddWithValue("$path", filePath);

        var value = command.ExecuteScalar();
        return value is string s ? s : null;
    }

    public static string? ReadHashAlgorithm(string dbPath)
    {
        using var connection = Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM external_extract_metadata WHERE key = 'hash_algorithm';";

        try
        {
            var value = command.ExecuteScalar();
            return value is string s ? s : null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1
            && ex.Message.Contains("external_extract_metadata", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    private static SqliteConnection Open(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        return SqliteReadOnlyAccess.Open(dbPath);
    }
}
