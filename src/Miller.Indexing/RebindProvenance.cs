using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;

namespace Miller.Indexing;

/// <summary>
/// The rebind provenance an artifact carries after the julie-extract <c>rebind</c> verb retargeted it: the root
/// the snapshot was copied from, that source artifact's generation id, and the instant of the retarget. All
/// three live in additive, OPTIONAL <c>artifact_metadata</c> keys (<c>rebound_from_root</c>,
/// <c>rebound_from_artifact_id</c>, <c>rebound_at</c>) — an artifact that was never rebound carries none of
/// them, which is why <see cref="RebindProvenanceReader"/> answers null rather than an empty record.
/// </summary>
public sealed record RebindProvenanceMetadata(string SourceRoot, string? SourceArtifactId, string? ReboundAt);

/// <summary>
/// Tolerant reader of the three additive rebind-provenance keys. Absent keys, absent table, absent file, or any
/// read failure all report null — the never-rebound answer — so surfacing provenance can never turn a readable
/// workspace into a failed status call. Mirrors <see cref="ExtractIndexLevelReader"/>'s tolerance.
///
/// <para><c>rebound_from_root</c> is the identity of the provenance: the record exists only when that key is
/// present and non-blank. The other two are carried verbatim, null when absent, so a partially-written or
/// foreign artifact degrades field by field instead of suppressing the whole fact.</para>
/// </summary>
public static class RebindProvenanceReader
{
    private const string SourceRootKey = "rebound_from_root";
    private const string SourceArtifactIdKey = "rebound_from_artifact_id";
    private const string ReboundAtKey = "rebound_at";

    public static RebindProvenanceMetadata? Read(string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return null;

        try
        {
            using var connection = SqliteReadOnlyAccess.Open(dbPath);
            return Read(connection);
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    public static RebindProvenanceMetadata? ReadSession(IWorkspaceReadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Read(Read);
    }

    private static RebindProvenanceMetadata? Read(SqliteConnection connection)
    {
        try
        {
            string? sourceRoot = ReadKey(connection, SourceRootKey);
            if (string.IsNullOrWhiteSpace(sourceRoot))
                return null;

            return new RebindProvenanceMetadata(
                sourceRoot,
                ReadKey(connection, SourceArtifactIdKey),
                ReadKey(connection, ReboundAtKey));
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadKey(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM artifact_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string value && !string.IsNullOrWhiteSpace(value) ? value : null;
    }
}
