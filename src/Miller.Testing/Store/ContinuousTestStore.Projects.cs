using Microsoft.Data.Sqlite;

namespace Miller.Testing;

public sealed partial class ContinuousTestStore
{
    public void PutContinuousTestProject(ContinuousTestProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ct_test_projects (
                    id, workspace_id, project_path, framework, command, enabled,
                    metadata_json, exclude_traits, inventory_stale, created_at, updated_at
                )
                VALUES (
                    $id, $ws, $path, $framework, $command, $enabled,
                    $metadata, $traits, $stale,
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                )
                ON CONFLICT(workspace_id, project_path) DO UPDATE SET
                    framework = coalesce(excluded.framework, ct_test_projects.framework),
                    command = excluded.command,
                    enabled = excluded.enabled,
                    metadata_json = excluded.metadata_json,
                    exclude_traits = excluded.exclude_traits,
                    inventory_stale = max(ct_test_projects.inventory_stale, excluded.inventory_stale),
                    updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
                """;
            command.Parameters.AddWithValue("$id", project.Id);
            command.Parameters.AddWithValue("$ws", project.WorkspaceId);
            command.Parameters.AddWithValue("$path", project.ProjectPath);
            command.Parameters.AddWithValue("$framework", (object?)project.Framework ?? DBNull.Value);
            command.Parameters.AddWithValue("$command", (object?)project.Command ?? DBNull.Value);
            command.Parameters.AddWithValue("$enabled", project.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$metadata", JsonText(project.Metadata));
            command.Parameters.AddWithValue("$traits", JsonText(project.ExcludeTraits));
            command.Parameters.AddWithValue("$stale", project.InventoryStale ? 1 : 0);
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<ContinuousTestProject> ListContinuousTestProjects(
        string workspaceId,
        bool includeDisabled = false)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<ContinuousTestProject>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, project_path, framework, command, enabled,
                           metadata_json, exclude_traits, inventory_stale
                    FROM ct_test_projects
                    WHERE workspace_id = $ws AND ($includeDisabled = 1 OR enabled = 1)
                    ORDER BY project_path, id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                command.Parameters.AddWithValue("$includeDisabled", includeDisabled ? 1 : 0);

                var rows = new List<ContinuousTestProject>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ContinuousTestProject(
                        Id: reader.GetString(0),
                        WorkspaceId: reader.GetString(1),
                        ProjectPath: reader.GetString(2),
                        Framework: NullableString(reader, 3),
                        Command: NullableString(reader, 4),
                        Enabled: reader.GetInt64(5) != 0,
                        Metadata: MetadataFromJson(reader.GetString(6)),
                        ExcludeTraits: StringListFromJson(reader.GetString(7)),
                        InventoryStale: reader.GetInt64(8) != 0));
                }

                return rows;
            });
    }

    public int SetContinuousTestProjectEnabled(string workspaceId, string projectPath, bool enabled)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("must not be empty", nameof(projectPath));
        if (!CanWriteExistingFile())
            return 0;

        string fullPath = Path.GetFullPath(projectPath);
        int updated = 0;
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ct_test_projects
                SET enabled = $enabled,
                    updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE workspace_id = $ws AND project_path = $path;
                """;
            command.Parameters.AddWithValue("$ws", workspaceId);
            command.Parameters.AddWithValue("$path", fullPath);
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            updated = command.ExecuteNonQuery();
        });
        return updated;
    }
}
