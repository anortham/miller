using Microsoft.Data.Sqlite;

namespace Miller.Testing;

public sealed partial class ContinuousTestStore
{
    public CtFreshnessKey? ReadLastReconciledCursor(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead(
            static () => (CtFreshnessKey?)null,
            connection =>
            {
                using var exists = connection.CreateCommand();
                exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'ct_revision_cursors';";
                if (exists.ExecuteScalar() is null)
                    return null;

                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT index_identity, revision
                    FROM ct_revision_cursors
                    WHERE workspace_id = $workspace;
                    """;
                command.Parameters.AddWithValue("$workspace", workspaceId);
                using SqliteDataReader reader = command.ExecuteReader();
                return reader.Read()
                    ? new CtFreshnessKey(reader.GetString(0), reader.GetInt64(1))
                    : null;
            });
    }

    public void SaveLastReconciledCursor(string workspaceId, CtFreshnessKey cursor)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ct_revision_cursors(workspace_id, index_identity, revision, updated_at)
                VALUES ($workspace, $identity, $revision, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                ON CONFLICT(workspace_id) DO UPDATE SET
                    index_identity = excluded.index_identity,
                    revision = excluded.revision,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$workspace", workspaceId);
            command.Parameters.AddWithValue("$identity", cursor.IndexIdentity);
            command.Parameters.AddWithValue("$revision", cursor.Revision);
            command.ExecuteNonQuery();
        });
    }
}
