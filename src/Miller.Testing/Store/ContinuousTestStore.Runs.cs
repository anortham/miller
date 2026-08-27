using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Testing;

public sealed partial class ContinuousTestStore
{
    /// <summary>
    /// Marks the selected cases <c>running</c> for <paramref name="run"/>. The state a start
    /// displaces is captured in <c>pre_run_state</c> (carried through a restart of a still-running
    /// case), and a RED case keeps its committed <c>index_identity</c>/<c>revision</c>: the red is
    /// the standing verdict until a result commits over it, so the run key must not overwrite the
    /// key the failure was proven at.
    /// </summary>
    public void StartContinuousTestRun(ContinuousTestRun run, IReadOnlyList<string> testCaseIds)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(testCaseIds);

        Transaction(() =>
        {
            UpsertContinuousTestRun(run);
            foreach (string testCaseId in testCaseIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                using var command = _write!.CreateCommand();
                command.CommandText = """
                    INSERT INTO ct_test_states (
                        test_case_id, workspace_id, index_identity, revision, state,
                        running_run_id, running_revision, updated_at
                    )
                    SELECT id, workspace_id, $identity, $revision, 'running', $runId, $selected,
                           strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                    FROM test_cases
                    WHERE workspace_id = $ws AND id = $id
                    ON CONFLICT(test_case_id) DO UPDATE SET
                        workspace_id = excluded.workspace_id,
                        index_identity = CASE
                            WHEN ct_test_states.state = 'red'
                                 OR (ct_test_states.state = 'running'
                                     AND ct_test_states.pre_run_state = 'red')
                            THEN ct_test_states.index_identity
                            ELSE excluded.index_identity
                        END,
                        revision = CASE
                            WHEN ct_test_states.state = 'red'
                                 OR (ct_test_states.state = 'running'
                                     AND ct_test_states.pre_run_state = 'red')
                            THEN ct_test_states.revision
                            ELSE excluded.revision
                        END,
                        pre_run_state = CASE
                            WHEN ct_test_states.state = 'running' THEN ct_test_states.pre_run_state
                            ELSE ct_test_states.state
                        END,
                        state = 'running',
                        running_run_id = $runId,
                        running_revision = $selected,
                        updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
                    """;
                command.Parameters.AddWithValue("$ws", run.WorkspaceId);
                command.Parameters.AddWithValue("$id", testCaseId);
                command.Parameters.AddWithValue("$identity", run.IndexIdentity);
                command.Parameters.AddWithValue("$revision", run.Revision);
                command.Parameters.AddWithValue("$runId", run.Id);
                command.Parameters.AddWithValue("$selected", run.SelectedRevision);
                command.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// Commits the run's reported results and retires its unreported cases. Returns the count of
    /// cases the run selected but never reported.
    /// </summary>
    public int CompleteContinuousTestRun(ContinuousTestRunCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        // A TERMINAL run always has an end time. The provider's own time wins when it reported one, but a
        // truncated run reports none: the xUnit parser assigns an end time only from the
        // `test-assembly-finished` event, and a stall kill means the child never emits it. The row then read
        // as a run that had not stopped — `status=failed` beside `ended_at=NULL` — long after the process
        // tree was gone. This is the single writer of a terminal status, so one stamp here covers every
        // provider and every importer.
        if (completion.EndedAt is null)
            completion = completion with { EndedAt = DateTimeOffset.UtcNow };

        int unreported = 0;
        Transaction(() =>
        {
            UpsertContinuousTestRun(new ContinuousTestRun(
                Id: completion.TestRunId,
                WorkspaceId: completion.WorkspaceId,
                Status: completion.Status,
                SelectedRevision: completion.SelectedRevision,
                IndexIdentity: completion.IndexIdentity,
                Revision: completion.Revision,
                EndedAt: completion.EndedAt,
                CompletedRevision: completion.CurrentRevision));

            bool commitsFresh = string.Equals(
                completion.SelectedRevision,
                completion.CurrentRevision,
                StringComparison.Ordinal);

            ContinuousTestResult[] results = completion.Results.ToArray();
            foreach (ContinuousTestResult result in results)
            {
                if (!string.Equals(result.WorkspaceId, completion.WorkspaceId, StringComparison.Ordinal))
                    throw new ArgumentException("result workspace must match completion workspace", nameof(completion));
                if (!string.Equals(result.TestRunId, completion.TestRunId, StringComparison.Ordinal))
                    throw new ArgumentException("result run must match completion run", nameof(completion));

                UpsertContinuousTestResult(result, DateTimeText(completion.EndedAt));
            }

            IReadOnlyDictionary<string, ContinuousTestFlakinessScore> scores =
                ScoreContinuousTestFlakinessBatch(completion.WorkspaceId, results);
            foreach (ContinuousTestResult result in results)
            {
                ContinuousTestFlakinessScore score = scores[result.TestCaseId];
                if (commitsFresh)
                    CommitFreshResult(completion, result, score);
                else
                    PreserveStaleResult(completion, result, score);
            }

            unreported = MarkUnreportedRunCasesStale(completion);
        });
        return unreported;
    }

    public void PutRunArtifact(ContinuousTestRunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO run_artifacts (id, workspace_id, kind, path, payload_json, created_at)
                VALUES ($id, $ws, $kind, $path, $payload, $created)
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    kind = excluded.kind,
                    path = excluded.path,
                    payload_json = excluded.payload_json,
                    created_at = excluded.created_at;
                """;
            command.Parameters.AddWithValue("$id", artifact.Id);
            command.Parameters.AddWithValue("$ws", artifact.WorkspaceId);
            command.Parameters.AddWithValue("$kind", artifact.Kind);
            command.Parameters.AddWithValue("$path", (object?)artifact.Path ?? DBNull.Value);
            command.Parameters.AddWithValue("$payload", JsonText(artifact.Payload));
            command.Parameters.AddWithValue(
                "$created",
                artifact.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        });
    }

    public void LinkContinuousTestRunArtifact(string workspaceId, string runId, string artifactId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrEmpty(runId))
            throw new ArgumentException("must not be empty", nameof(runId));
        if (string.IsNullOrEmpty(artifactId))
            throw new ArgumentException("must not be empty", nameof(artifactId));

        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE test_runs
                SET artifact_id = coalesce(artifact_id, $artifact)
                WHERE id = $id AND workspace_id = $ws;
                """;
            command.Parameters.AddWithValue("$id", runId);
            command.Parameters.AddWithValue("$ws", workspaceId);
            command.Parameters.AddWithValue("$artifact", artifactId);
            command.ExecuteNonQuery();
        });
    }

    public ContinuousTestFlakinessScore ScoreContinuousTestFlakiness(string workspaceId, string testCaseId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrEmpty(testCaseId))
            throw new ArgumentException("must not be empty", nameof(testCaseId));

        return ContinuousTestFlakiness.Score(RecentContinuousTestOutcomes(workspaceId, testCaseId));
    }

    private void UpsertContinuousTestRun(ContinuousTestRun run)
    {
        using var command = _write!.CreateCommand();
        command.CommandText = """
            INSERT INTO test_runs (
                id, workspace_id, index_identity, revision, command, framework, status,
                started_at, ended_at, artifact_id, metadata_json, selected_revision, completed_revision
            )
            VALUES (
                $id, $ws, $identity, $revision, $command, $framework, $status,
                $started, $ended, $artifact, $metadata, $selected, $completed
            )
            ON CONFLICT(id) DO UPDATE SET
                workspace_id = excluded.workspace_id,
                index_identity = excluded.index_identity,
                revision = excluded.revision,
                command = coalesce(excluded.command, test_runs.command),
                framework = coalesce(excluded.framework, test_runs.framework),
                status = excluded.status,
                started_at = coalesce(excluded.started_at, test_runs.started_at),
                ended_at = coalesce(excluded.ended_at, test_runs.ended_at),
                artifact_id = coalesce(excluded.artifact_id, test_runs.artifact_id),
                metadata_json = CASE
                    WHEN excluded.metadata_json = '{}' THEN test_runs.metadata_json
                    ELSE excluded.metadata_json
                END,
                selected_revision = excluded.selected_revision,
                completed_revision = excluded.completed_revision;
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$ws", run.WorkspaceId);
        command.Parameters.AddWithValue("$identity", run.IndexIdentity);
        command.Parameters.AddWithValue("$revision", run.Revision);
        command.Parameters.AddWithValue("$command", (object?)run.Command ?? DBNull.Value);
        command.Parameters.AddWithValue("$framework", (object?)run.Framework ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$started", DateTimeText(run.StartedAt));
        command.Parameters.AddWithValue("$ended", DateTimeText(run.EndedAt));
        command.Parameters.AddWithValue("$artifact", (object?)run.ArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata", JsonText(run.Metadata));
        command.Parameters.AddWithValue("$selected", run.SelectedRevision);
        command.Parameters.AddWithValue("$completed", (object?)run.CompletedRevision ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void UpsertContinuousTestResult(ContinuousTestResult result, object observedAt)
    {
        using var command = _write!.CreateCommand();
        command.CommandText = """
            INSERT INTO test_results (
                id, workspace_id, index_identity, revision, test_case_id, test_run_id, status,
                duration_seconds, source_artifact_id, metadata_json, result_revision, failure_summary, observed_at
            )
            VALUES (
                $id, $ws, $identity, $revision, $case, $run, $status,
                $duration, $artifact, $metadata, $resultRevision, $failure, $observedAt
            )
            ON CONFLICT(workspace_id, test_case_id, test_run_id) DO UPDATE SET
                id = excluded.id,
                index_identity = excluded.index_identity,
                revision = excluded.revision,
                status = excluded.status,
                duration_seconds = excluded.duration_seconds,
                source_artifact_id = excluded.source_artifact_id,
                metadata_json = excluded.metadata_json,
                result_revision = excluded.result_revision,
                failure_summary = excluded.failure_summary,
                observed_at = excluded.observed_at
            ON CONFLICT(id) DO UPDATE SET
                workspace_id = excluded.workspace_id,
                index_identity = excluded.index_identity,
                revision = excluded.revision,
                test_case_id = excluded.test_case_id,
                test_run_id = excluded.test_run_id,
                status = excluded.status,
                duration_seconds = excluded.duration_seconds,
                source_artifact_id = excluded.source_artifact_id,
                metadata_json = excluded.metadata_json,
                result_revision = excluded.result_revision,
                failure_summary = excluded.failure_summary,
                observed_at = excluded.observed_at;
            """;
        command.Parameters.AddWithValue("$id", result.Id);
        command.Parameters.AddWithValue("$ws", result.WorkspaceId);
        command.Parameters.AddWithValue("$identity", result.IndexIdentity);
        command.Parameters.AddWithValue("$revision", result.Revision);
        command.Parameters.AddWithValue("$case", result.TestCaseId);
        command.Parameters.AddWithValue("$run", result.TestRunId);
        command.Parameters.AddWithValue("$status", result.Status);
        command.Parameters.AddWithValue("$duration", (object?)result.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$artifact", (object?)result.SourceArtifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata", JsonText(result.Metadata));
        command.Parameters.AddWithValue("$resultRevision", result.ResultRevision);
        command.Parameters.AddWithValue("$failure", (object?)FailureSummaryText.Summarize(result.FailureSummary) ?? DBNull.Value);
        command.Parameters.AddWithValue("$observedAt", observedAt);
        command.ExecuteNonQuery();
    }

    private void CommitFreshResult(
        ContinuousTestRunCompletion completion,
        ContinuousTestResult result,
        ContinuousTestFlakinessScore score)
    {
        using var command = _write!.CreateCommand();
        command.CommandText = """
            INSERT INTO ct_test_states (
                test_case_id, workspace_id, index_identity, revision, state, last_run_revision,
                stale_since_revision, running_run_id, running_revision,
                last_result_status, last_result_at, failure_summary, flakiness_score, updated_at
            )
            SELECT id, workspace_id, $identity, $revision, $state, $lastRun, NULL, NULL, NULL, $status, $ended, $failure,
                   $score, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            FROM test_cases
            WHERE workspace_id = $ws AND id = $id
            ON CONFLICT(test_case_id) DO UPDATE SET
                workspace_id = excluded.workspace_id,
                index_identity = excluded.index_identity,
                revision = excluded.revision,
                state = $state,
                last_run_revision = $lastRun,
                stale_since_revision = NULL,
                running_run_id = NULL,
                running_revision = NULL,
                pre_run_state = NULL,
                last_result_status = $status,
                last_result_at = $ended,
                failure_summary = $failure,
                flakiness_score = $score,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
            """;
        command.Parameters.AddWithValue("$ws", completion.WorkspaceId);
        command.Parameters.AddWithValue("$id", result.TestCaseId);
        command.Parameters.AddWithValue("$identity", completion.IndexIdentity);
        command.Parameters.AddWithValue("$revision", completion.Revision);
        command.Parameters.AddWithValue("$state", StateValue(StateForResult(result.Status)));
        command.Parameters.AddWithValue("$lastRun", completion.CurrentRevision);
        command.Parameters.AddWithValue("$status", result.Status);
        command.Parameters.AddWithValue("$ended", DateTimeText(completion.EndedAt));
        command.Parameters.AddWithValue("$failure", (object?)FailureSummaryText.Summarize(result.FailureSummary) ?? DBNull.Value);
        command.Parameters.AddWithValue("$score", score.FailureRate);
        command.ExecuteNonQuery();
    }

    private void PreserveStaleResult(
        ContinuousTestRunCompletion completion,
        ContinuousTestResult result,
        ContinuousTestFlakinessScore score)
    {
        using var command = _write!.CreateCommand();
        command.CommandText = """
            INSERT INTO ct_test_states (
                test_case_id, workspace_id, index_identity, revision, state, stale_since_revision,
                running_run_id, running_revision, last_result_status,
                last_result_at, failure_summary, flakiness_score, updated_at
            )
            SELECT id, workspace_id, $identity, $revision, 'stale', $current, NULL, NULL, $status, $ended, $failure,
                   $score, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            FROM test_cases
            WHERE workspace_id = $ws AND id = $id
            ON CONFLICT(test_case_id) DO UPDATE SET
                workspace_id = excluded.workspace_id,
                index_identity = excluded.index_identity,
                revision = excluded.revision,
                state = 'stale',
                stale_since_revision = CASE
                    WHEN ct_test_states.stale_since_revision IS NULL
                         OR ct_test_states.stale_since_revision = $selected THEN $current
                    ELSE ct_test_states.stale_since_revision
                END,
                running_run_id = NULL,
                running_revision = NULL,
                pre_run_state = NULL,
                last_result_status = $status,
                last_result_at = $ended,
                failure_summary = $failure,
                flakiness_score = $score,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
            """;
        command.Parameters.AddWithValue("$ws", completion.WorkspaceId);
        command.Parameters.AddWithValue("$id", result.TestCaseId);
        command.Parameters.AddWithValue("$identity", completion.IndexIdentity);
        command.Parameters.AddWithValue("$revision", completion.Revision);
        command.Parameters.AddWithValue("$selected", completion.SelectedRevision);
        command.Parameters.AddWithValue("$current", completion.CurrentRevision);
        command.Parameters.AddWithValue("$status", result.Status);
        command.Parameters.AddWithValue("$ended", DateTimeText(completion.EndedAt));
        command.Parameters.AddWithValue("$failure", (object?)FailureSummaryText.Summarize(result.FailureSummary) ?? DBNull.Value);
        command.Parameters.AddWithValue("$score", score.FailureRate);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Retires the cases the run selected but never reported. A case that was RED before the run
    /// keeps its verdict — the state string, the committed key <see cref="StartContinuousTestRun"/>
    /// preserved, and a <c>stale_since_revision</c> stamped once record the owed rerun, the same
    /// arms <c>MarkContinuousTestsStale</c> applies. Every other unreported case retires to
    /// <c>stale</c>. Returns the unreported-case count.
    /// </summary>
    private int MarkUnreportedRunCasesStale(ContinuousTestRunCompletion completion)
    {
        using var command = _write!.CreateCommand();
        command.CommandText = """
            UPDATE ct_test_states
            SET state = CASE WHEN pre_run_state = 'red' THEN 'red' ELSE 'stale' END,
                stale_since_revision = CASE
                    WHEN pre_run_state = 'red' THEN coalesce(stale_since_revision, $current)
                    ELSE $current
                END,
                pre_run_state = NULL,
                running_run_id = NULL,
                running_revision = NULL,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE workspace_id = $ws
              AND running_run_id = $run;
            """;
        command.Parameters.AddWithValue("$ws", completion.WorkspaceId);
        command.Parameters.AddWithValue("$run", completion.TestRunId);
        command.Parameters.AddWithValue("$current", completion.CurrentRevision);
        return command.ExecuteNonQuery();
    }

    private IReadOnlyList<ContinuousTestOutcome> RecentContinuousTestOutcomes(string workspaceId, string testCaseId)
    {
        return RecentContinuousTestOutcomes(workspaceId, [testCaseId])
            .GetValueOrDefault(testCaseId, []);
    }

    private IReadOnlyDictionary<string, ContinuousTestFlakinessScore> ScoreContinuousTestFlakinessBatch(
        string workspaceId,
        IReadOnlyCollection<ContinuousTestResult> results)
    {
        string[] testCaseIds = results
            .Select(result => result.TestCaseId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<ContinuousTestOutcome>> histories =
            RecentContinuousTestOutcomes(workspaceId, testCaseIds);
        return testCaseIds.ToDictionary(
            testCaseId => testCaseId,
            testCaseId => ContinuousTestFlakiness.Score(histories.GetValueOrDefault(testCaseId, [])),
            StringComparer.Ordinal);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<ContinuousTestOutcome>> RecentContinuousTestOutcomes(
        string workspaceId,
        IReadOnlyCollection<string> testCaseIds)
    {
        string[] ids = testCaseIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, IReadOnlyList<ContinuousTestOutcome>>(StringComparer.Ordinal);

        return WithRead<IReadOnlyDictionary<string, IReadOnlyList<ContinuousTestOutcome>>>(
            static () => new Dictionary<string, IReadOnlyList<ContinuousTestOutcome>>(StringComparer.Ordinal),
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $$"""
                    SELECT r.test_case_id, r.id, r.status, r.observed_at
                    FROM json_each($caseIds) requested
                    JOIN test_results r
                      ON r.workspace_id = $ws AND r.test_case_id = requested.value
                    WHERE (
                          SELECT COUNT(*)
                          FROM test_results newer
                          WHERE newer.workspace_id = r.workspace_id
                            AND newer.test_case_id = r.test_case_id
                            AND (
                                newer.observed_at > r.observed_at
                                OR (newer.observed_at = r.observed_at AND newer.id >= r.id)
                            )
                      ) <= $limit;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                command.Parameters.AddWithValue("$limit", ContinuousTestFlakiness.MaxHistory);
                command.Parameters.AddWithValue("$caseIds", TestingJson.Strings(ids));

                var outcomes = ids.ToDictionary(
                    testCaseId => testCaseId,
                    _ => new List<RecentContinuousTestOutcomeRow>(),
                    StringComparer.Ordinal);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string testCaseId = reader.GetString(0);
                    string id = reader.GetString(1);
                    string status = reader.GetString(2);
                    if (ContinuousTestFlakiness.NormalizeStatus(status) is null)
                        continue;
                    outcomes[testCaseId].Add(new RecentContinuousTestOutcomeRow(
                        id,
                        status,
                        DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)));
                }

                return outcomes.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<ContinuousTestOutcome>)pair.Value
                        .OrderByDescending(row => row.ObservedAt)
                        .ThenByDescending(row => row.Id, StringComparer.Ordinal)
                        .Select(row => new ContinuousTestOutcome(row.Status, row.ObservedAt))
                        .ToArray(),
                    StringComparer.Ordinal);
            });
    }

    private sealed record RecentContinuousTestOutcomeRow(
        string Id,
        string Status,
        DateTimeOffset ObservedAt);
}
