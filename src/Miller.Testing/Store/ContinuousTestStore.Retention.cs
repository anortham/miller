using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Testing;

internal sealed record ContinuousTestHistoryPruneResult(
    string WorkspaceId,
    DateTimeOffset AsOf,
    long ConsideredRuns,
    long DeletedRuns,
    long ProtectedRuns,
    long ConsideredResults,
    long DeletedResults,
    long ProtectedResults,
    long ConsideredArtifacts,
    long DeletedArtifacts,
    long ProtectedArtifacts,
    long LegacyUnlinkedArtifacts,
    long PageCount,
    long FreelistCount);

public sealed partial class ContinuousTestStore
{
    internal ContinuousTestHistoryPruneResult PruneContinuousTestHistory(
        string workspaceId,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        ContinuousTestHistoryPruneResult? result = null;
        Transaction(() => result = PruneContinuousTestHistoryInTransaction(workspaceId, now));
        return result!;
    }

    private ContinuousTestHistoryPruneResult PruneContinuousTestHistoryInTransaction(
        string workspaceId,
        DateTimeOffset now)
    {
        SqliteConnection connection = _write ?? throw new InvalidOperationException("write transaction is not active");
        string cutoff = now.ToUniversalTime().AddDays(-30).ToString("O", CultureInfo.InvariantCulture);
        long consideredRuns = CountRows("test_runs", workspaceId);
        long consideredResults = CountRows("test_results", workspaceId);
        long consideredArtifacts = CountRows("run_artifacts", workspaceId);
        long legacyArtifacts = 0;

        DropRetentionTables(connection);
        CreateRetentionTables(connection);
        try
        {
            ExecuteRetention(
                """
                INSERT OR IGNORE INTO temp.ct_retention_runs(id)
                SELECT id
                FROM test_runs
                WHERE workspace_id = $ws
                  AND (
                      ended_at IS NULL
                      OR lower(status) = 'running'
                      OR (
                          julianday(coalesce(ended_at, started_at)) > julianday($cutoff)
                          OR (
                              julianday(coalesce(ended_at, started_at)) = julianday($cutoff)
                              AND coalesce(ended_at, started_at) >= $cutoff
                          )
                      )
                  );
                """,
                workspaceId,
                cutoff);
            ExecuteRetention(
                """
                INSERT OR IGNORE INTO temp.ct_retention_runs(id)
                SELECT running_run_id
                FROM ct_test_states
                WHERE workspace_id = $ws AND running_run_id IS NOT NULL;
                """,
                workspaceId,
                cutoff);
            ExecuteRetention(
                """
                WITH ranked AS (
                    SELECT id,
                           ROW_NUMBER() OVER (
                               PARTITION BY test_case_id
                               ORDER BY julianday(observed_at) DESC, observed_at DESC, id DESC
                           ) AS outcome_rank
                    FROM test_results
                    WHERE workspace_id = $ws
                      AND lower(trim(status)) IN ('passed', 'failed', 'skipped', 'error', 'errored')
                )
                INSERT OR IGNORE INTO temp.ct_retention_results(id)
                SELECT id
                FROM ranked
                WHERE outcome_rank <= 50
                UNION
                SELECT id
                FROM test_results
                WHERE workspace_id = $ws
                  AND (
                      julianday(observed_at) > julianday($cutoff)
                      OR (julianday(observed_at) = julianday($cutoff) AND observed_at >= $cutoff)
                  );
                """,
                workspaceId,
                cutoff);
            ExecuteRetention(
                """
                INSERT OR IGNORE INTO temp.ct_retention_runs(id)
                SELECT test_run_id
                FROM test_results
                WHERE workspace_id = $ws
                  AND id IN (SELECT id FROM temp.ct_retention_results);
                """,
                workspaceId,
                cutoff);
            ExecuteRetention(
                """
                WITH newest AS (
                    SELECT a.id,
                           ROW_NUMBER() OVER (
                               PARTITION BY json_extract(a.payload_json, '$.project_path')
                               ORDER BY julianday(a.created_at) DESC, a.created_at DESC, a.id DESC
                           ) AS artifact_rank
                    FROM run_artifacts a
                    JOIN ct_test_projects p
                      ON p.workspace_id = a.workspace_id
                     AND p.enabled = 1
                     AND json_extract(a.payload_json, '$.project_path') = p.project_path
                    WHERE a.workspace_id = $ws
                )
                INSERT OR IGNORE INTO temp.ct_retention_artifacts(id)
                SELECT a.id
                FROM run_artifacts a
                WHERE a.workspace_id = $ws
                  AND (
                      (
                          julianday(a.created_at) > julianday($cutoff)
                          OR (julianday(a.created_at) = julianday($cutoff) AND a.created_at >= $cutoff)
                      )
                      OR json_extract(a.payload_json, '$.run_id') IS NULL
                      OR trim(CAST(json_extract(a.payload_json, '$.run_id') AS TEXT)) = ''
                      OR json_extract(a.payload_json, '$.project_path') IS NULL
                      OR trim(CAST(json_extract(a.payload_json, '$.project_path') AS TEXT)) = ''
                      OR a.id IN (SELECT id FROM newest WHERE artifact_rank = 1)
                  );
                """,
                workspaceId,
                cutoff);

            int added;
            do
            {
                added = 0;
                added += ExecuteRetention(
                    """
                    INSERT OR IGNORE INTO temp.ct_retention_results(id)
                    SELECT id
                    FROM test_results
                    WHERE workspace_id = $ws
                      AND test_run_id IN (SELECT id FROM temp.ct_retention_runs);
                    """,
                    workspaceId,
                    cutoff);
                added += ExecuteRetention(
                    """
                    INSERT OR IGNORE INTO temp.ct_retention_artifacts(id)
                    SELECT artifact_id
                    FROM test_runs
                    WHERE workspace_id = $ws
                      AND id IN (SELECT id FROM temp.ct_retention_runs)
                      AND artifact_id IS NOT NULL
                    UNION
                    SELECT source_artifact_id
                    FROM test_results
                    WHERE workspace_id = $ws
                      AND id IN (SELECT id FROM temp.ct_retention_results)
                      AND source_artifact_id IS NOT NULL;
                    """,
                    workspaceId,
                    cutoff);
                added += ExecuteRetention(
                    """
                    INSERT OR IGNORE INTO temp.ct_retention_runs(id)
                    SELECT json_extract(a.payload_json, '$.run_id')
                    FROM run_artifacts a
                    JOIN test_runs r
                      ON r.workspace_id = a.workspace_id
                     AND r.id = json_extract(a.payload_json, '$.run_id')
                    WHERE a.workspace_id = $ws
                      AND a.id IN (SELECT id FROM temp.ct_retention_artifacts)
                      AND json_extract(a.payload_json, '$.run_id') IS NOT NULL;
                    """,
                    workspaceId,
                    cutoff);
            }
            while (added > 0);

            legacyArtifacts = ScalarLong(
                """
                SELECT COUNT(*)
                FROM run_artifacts
                WHERE workspace_id = $ws
                  AND (
                      json_extract(payload_json, '$.run_id') IS NULL
                      OR trim(CAST(json_extract(payload_json, '$.run_id') AS TEXT)) = ''
                      OR json_extract(payload_json, '$.project_path') IS NULL
                      OR trim(CAST(json_extract(payload_json, '$.project_path') AS TEXT)) = ''
                  );
                """,
                workspaceId,
                cutoff);

            ExecuteRetention(
                """
                DELETE FROM coverage_spans
                WHERE workspace_id = $ws
                  AND coverage_file_id IN (
                      SELECT id
                      FROM coverage_files
                      WHERE workspace_id = $ws
                        AND artifact_id IS NOT NULL
                        AND artifact_id NOT IN (SELECT id FROM temp.ct_retention_artifacts)
                  );
                """,
                workspaceId,
                cutoff);
            ExecuteRetention(
                """
                DELETE FROM coverage_files
                WHERE workspace_id = $ws
                  AND artifact_id IS NOT NULL
                  AND artifact_id NOT IN (SELECT id FROM temp.ct_retention_artifacts);
                """,
                workspaceId,
                cutoff);
            ExecuteRetention(
                """
                DELETE FROM test_results
                WHERE workspace_id = $ws
                  AND id NOT IN (SELECT id FROM temp.ct_retention_results);
                """,
                workspaceId,
                cutoff);
            ExecuteRetention(
                """
                DELETE FROM test_runs
                WHERE workspace_id = $ws
                  AND id NOT IN (SELECT id FROM temp.ct_retention_runs);
                """,
                workspaceId,
                cutoff);
            ExecuteRetention(
                """
                DELETE FROM run_artifacts
                WHERE workspace_id = $ws
                  AND id NOT IN (SELECT id FROM temp.ct_retention_artifacts);
                """,
                workspaceId,
                cutoff);

            long protectedRuns = CountRows("test_runs", workspaceId);
            long protectedResults = CountRows("test_results", workspaceId);
            long protectedArtifacts = CountRows("run_artifacts", workspaceId);
            long deletedRuns = consideredRuns - protectedRuns;
            long deletedResults = consideredResults - protectedResults;
            long deletedArtifacts = consideredArtifacts - protectedArtifacts;
            long pageCount = ScalarLong("PRAGMA page_count;", workspaceId, cutoff);
            long freelistCount = ScalarLong("PRAGMA freelist_count;", workspaceId, cutoff);
            return new ContinuousTestHistoryPruneResult(
                workspaceId,
                now,
                consideredRuns,
                deletedRuns,
                protectedRuns,
                consideredResults,
                deletedResults,
                protectedResults,
                consideredArtifacts,
                deletedArtifacts,
                protectedArtifacts,
                legacyArtifacts,
                pageCount,
                freelistCount);
        }
        finally
        {
            DropRetentionTables(connection);
        }
    }

    private long CountRows(string table, string workspaceId)
    {
        return ScalarLong($"SELECT COUNT(*) FROM {table} WHERE workspace_id = $ws;", workspaceId, string.Empty);
    }

    private int ExecuteRetention(string sql, string workspaceId, string cutoff)
    {
        using var command = _write!.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ws", workspaceId);
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return command.ExecuteNonQuery();
    }

    private long ScalarLong(string sql, string workspaceId, string cutoff)
    {
        using var command = _write!.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ws", workspaceId);
        command.Parameters.AddWithValue("$cutoff", cutoff);
        object? value = command.ExecuteScalar();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void CreateRetentionTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TEMP TABLE ct_retention_runs(id TEXT PRIMARY KEY);
            CREATE TEMP TABLE ct_retention_results(id TEXT PRIMARY KEY);
            CREATE TEMP TABLE ct_retention_artifacts(id TEXT PRIMARY KEY);
            """;
        command.ExecuteNonQuery();
    }

    private static void DropRetentionTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS temp.ct_retention_runs;
            DROP TABLE IF EXISTS temp.ct_retention_results;
            DROP TABLE IF EXISTS temp.ct_retention_artifacts;
            """;
        command.ExecuteNonQuery();
    }
}
