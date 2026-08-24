using Microsoft.Data.Sqlite;

namespace Miller.Testing;

public sealed partial class ContinuousTestStore
{
    internal IReadOnlyList<ContinuousTestCase> ListTestCasesForProject(
        string workspaceId,
        string projectPath,
        bool includeLifecycle = false)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("must not be empty", nameof(projectPath));

        string normalizedProjectPath = Path.GetFullPath(projectPath);
        return WithRead<IReadOnlyList<ContinuousTestCase>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, file_path, content_hash, symbol_name, symbol_path,
                           suite_id, name, qualified_name, selector, framework, role, source,
                           confidence, metadata_json, provenance_json
                    FROM test_cases
                    WHERE workspace_id = $ws
                      AND project_path = $project
                      AND ($includeLifecycle = 1 OR source LIKE 'ct-provider:%')
                    ORDER BY selector, id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                command.Parameters.AddWithValue("$project", normalizedProjectPath);
                command.Parameters.AddWithValue("$includeLifecycle", includeLifecycle ? 1 : 0);

                var rows = new List<ContinuousTestCase>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add(ReadTestCase(reader));
                return rows;
            });
    }

    internal IReadOnlyList<ContinuousTestStatus> ListContinuousTestStatusesForProject(
        string workspaceId,
        string projectPath)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("must not be empty", nameof(projectPath));

        string normalizedProjectPath = Path.GetFullPath(projectPath);
        return WithRead<IReadOnlyList<ContinuousTestStatus>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT s.workspace_id, s.test_case_id, s.state, s.index_identity, s.revision,
                           s.last_run_revision, s.stale_since_revision, s.running_run_id, s.running_revision,
                           s.last_result_status, s.last_result_at, s.failure_summary, s.flakiness_score
                    FROM ct_test_states s
                    JOIN test_cases tc
                      ON tc.workspace_id = s.workspace_id
                     AND tc.id = s.test_case_id
                    WHERE s.workspace_id = $ws
                      AND tc.project_path = $project
                    ORDER BY s.state, s.test_case_id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                command.Parameters.AddWithValue("$project", normalizedProjectPath);

                var rows = new List<ContinuousTestStatus>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    rows.Add(ReadStatus(reader));
                return rows;
            });
    }

    private static ContinuousTestCase ReadTestCase(SqliteDataReader reader) =>
        new(
            Id: reader.GetString(0),
            WorkspaceId: reader.GetString(1),
            FilePath: NullableString(reader, 2),
            ContentHash: NullableString(reader, 3),
            SymbolName: NullableString(reader, 4),
            SymbolPath: NullableString(reader, 5),
            SuiteId: NullableString(reader, 6),
            Name: reader.GetString(7),
            QualifiedName: reader.GetString(8),
            Selector: reader.GetString(9),
            Framework: NullableString(reader, 10),
            Role: RoleFromValue(reader.GetString(11)),
            Source: reader.GetString(12),
            Confidence: reader.GetDouble(13),
            Metadata: MetadataFromJson(reader.GetString(14)),
            Provenance: MetadataFromJson(reader.GetString(15)));

    private static ContinuousTestStatus ReadStatus(SqliteDataReader reader)
    {
        ContinuousTestState state = StateFromValue(reader.GetString(2));
        string indexIdentity = reader.GetString(3);
        long revision = reader.GetInt64(4);
        CtFreshnessKey? proven = state is ContinuousTestState.Green
            or ContinuousTestState.Red
            or ContinuousTestState.Skipped
            ? new CtFreshnessKey(indexIdentity, revision)
            : null;
        return new ContinuousTestStatus(
            WorkspaceId: reader.GetString(0),
            TestCaseId: reader.GetString(1),
            State: state,
            IndexIdentity: indexIdentity,
            Revision: revision,
            LastRunRevision: NullableString(reader, 5),
            StaleSinceRevision: NullableString(reader, 6),
            RunningRunId: NullableString(reader, 7),
            RunningRevision: NullableString(reader, 8),
            LastResultStatus: NullableString(reader, 9),
            LastResultAt: NullableDateTimeOffset(reader, 10),
            FailureSummary: NullableString(reader, 11),
            FlakinessScore: reader.GetDouble(12),
            ProvenFreshKey: proven);
    }
}
