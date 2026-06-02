using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Reads julie's v1 file-level freshness contract: <c>files.content_hash</c> (the <c>blake3:&lt;hex&gt;</c>
/// token, normalized to bare hex on the way out) plus <c>artifact_metadata.hash_algorithm</c>.
/// </summary>
public static class ExtractFileHashReader
{
    public static string? ReadFileHash(string dbPath, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var connection = Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_hash FROM files WHERE path = $path;";
        command.Parameters.AddWithValue("$path", filePath);

        var value = command.ExecuteScalar();
        // v1 stores "blake3:<hex>"; normalize to bare hex so freshness consumers compare against disk hashes. A
        // missing row yields null; a present-but-empty/whitespace value is returned verbatim (NOT normalized) so
        // the gate's own IsNullOrWhiteSpace guard trips to Stale rather than NormalizeHash throwing on it.
        return value is string s
            ? string.IsNullOrWhiteSpace(s) ? s : ContentHasher.NormalizeHash(s)
            : null;
    }

    public static string? ReadHashAlgorithm(string dbPath)
    {
        using var connection = Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'hash_algorithm';";

        try
        {
            var value = command.ExecuteScalar();
            return value is string s ? s : null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1
            && ex.Message.Contains("artifact_metadata", StringComparison.OrdinalIgnoreCase))
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
