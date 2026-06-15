using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// Tolerant reader of <c>artifact_metadata.binary_version</c> — the <c>julie-extract</c> version that last
/// wrote the artifact — feeding the <see cref="LeadershipEligibility"/> claim gate. Read-only, and null on
/// ANY failure (missing file, pre-v1 artifact without the <c>artifact_metadata</c> table, missing key,
/// not-a-database/corrupt file) rather than throwing: the eligibility matrix already treats an unknown
/// artifact version as "cannot prove a downgrade ⟹ eligible", so a broken artifact must degrade to that
/// verdict, not crash the claim loop. Mirrors <see cref="ExtractReader.ReadRootPath"/>'s tolerance.
/// </summary>
public static class ExtractBinaryVersionReader
{
    /// <summary>
    /// The artifact's recorded <c>binary_version</c>, or null when the DB, table, or key is absent or the
    /// file is unreadable as SQLite. Never throws.
    /// </summary>
    public static string? TryRead(string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return null;

        try
        {
            using var connection = SqliteReadOnlyAccess.Open(dbPath);
            return TryRead(connection);
        }
        catch (SqliteException)
        {
            // Missing artifact_metadata table (pre-v1 artifact), SQLITE_NOTADB (garbage file), or corruption.
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // File deleted between the Exists check and Open, an unwritable directory (the WAL sidecar probe
            // in SqliteReadOnlyAccess throws InvalidOperationException), or a locked file. All read as
            // "version unknown" for the eligibility gate.
            return null;
        }
    }

    /// <summary>
    /// The artifact's recorded <c>binary_version</c> query using an already active database connection.
    /// Never throws.
    /// </summary>
    public static string? TryRead(SqliteConnection connection)
    {
        if (connection is null)
            return null;

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM artifact_metadata WHERE key = 'binary_version';";
            object? value = command.ExecuteScalar();
            return value is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            return null;
        }
    }
}
