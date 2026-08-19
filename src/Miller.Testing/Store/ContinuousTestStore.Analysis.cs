using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Testing;

public sealed record CtTestLink(
    string Id,
    string WorkspaceId,
    string Tier,
    double Confidence,
    string Explanation,
    string? TestCaseId = null,
    string? SourceFilePath = null,
    string? SourceContentHash = null,
    string? SourceSymbolName = null,
    string? SourceSymbolPath = null,
    IReadOnlyList<string>? SourceFactIds = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public IReadOnlyList<string> SourceFactIds { get; init; } = SourceFactIds ?? [];
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = Metadata ?? new Dictionary<string, object?>();
}

public sealed record CtLatestTestResult(
    string Id,
    string Status,
    string TestRunId,
    string? ResultRevision,
    string? FailureSummary);

public sealed record CtTestResultHistoryRow(
    string TestCaseId,
    string Selector,
    string Status,
    DateTimeOffset? ObservedAt);

public sealed partial class ContinuousTestStore
{
    public IReadOnlyList<ContinuousTestRunArtifact> ListRunArtifacts(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<ContinuousTestRunArtifact>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, kind, path, payload_json, created_at
                    FROM run_artifacts
                    WHERE workspace_id = $ws
                    ORDER BY id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                var rows = new List<ContinuousTestRunArtifact>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ContinuousTestRunArtifact(
                        Id: reader.GetString(0),
                        WorkspaceId: reader.GetString(1),
                        Kind: reader.GetString(2),
                        Path: NullableString(reader, 3),
                        Payload: MetadataFromJson(reader.GetString(4)),
                        CreatedAt: NullableDateTimeOffset(reader, 5) ?? DateTimeOffset.UnixEpoch));
                }

                return rows;
            });
    }

    public IReadOnlyList<ContinuousTestRun> ListTestRuns(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<ContinuousTestRun>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, index_identity, revision, command, framework, status,
                           started_at, ended_at, selected_revision, completed_revision, artifact_id, metadata_json
                    FROM test_runs
                    WHERE workspace_id = $ws
                    ORDER BY id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                var rows = new List<ContinuousTestRun>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ContinuousTestRun(
                        Id: reader.GetString(0),
                        WorkspaceId: reader.GetString(1),
                        Status: reader.GetString(6),
                        SelectedRevision: reader.GetString(9),
                        IndexIdentity: reader.GetString(2),
                        Revision: reader.GetInt64(3),
                        Command: NullableString(reader, 4),
                        Framework: NullableString(reader, 5),
                        StartedAt: NullableDateTimeOffset(reader, 7),
                        EndedAt: NullableDateTimeOffset(reader, 8),
                        CompletedRevision: NullableString(reader, 10),
                        ArtifactId: NullableString(reader, 11),
                        Metadata: MetadataFromJson(reader.GetString(12))));
                }

                return rows;
            });
    }

    public IReadOnlyList<ContinuousTestResult> ListTestResults(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<ContinuousTestResult>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, index_identity, revision, test_case_id, test_run_id, status,
                           result_revision, duration_seconds, failure_summary, source_artifact_id, metadata_json
                    FROM test_results
                    WHERE workspace_id = $ws
                    ORDER BY test_case_id, id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                var rows = new List<ContinuousTestResult>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ContinuousTestResult(
                        Id: reader.GetString(0),
                        WorkspaceId: reader.GetString(1),
                        TestCaseId: reader.GetString(4),
                        TestRunId: reader.GetString(5),
                        Status: reader.GetString(6),
                        ResultRevision: reader.GetString(7),
                        IndexIdentity: reader.GetString(2),
                        Revision: reader.GetInt64(3),
                        DurationSeconds: reader.IsDBNull(8) ? null : reader.GetDouble(8),
                        FailureSummary: NullableString(reader, 9),
                        SourceArtifactId: NullableString(reader, 10),
                        Metadata: MetadataFromJson(reader.GetString(11))));
                }

                return rows;
            });
    }

    public IReadOnlyList<CoverageFile> ListCoverageFiles(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<CoverageFile>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, index_identity, revision, artifact_id, format, path,
                           parser, source_hash, generated_at, metadata_json
                    FROM coverage_files
                    WHERE workspace_id = $ws
                    ORDER BY id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                var rows = new List<CoverageFile>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new CoverageFile(
                        Id: reader.GetString(0),
                        WorkspaceId: reader.GetString(1),
                        IndexIdentity: reader.GetString(2),
                        Revision: reader.GetInt64(3),
                        Format: reader.GetString(5),
                        Path: reader.GetString(6),
                        Parser: reader.GetString(7),
                        SourceHash: reader.GetString(8),
                        ArtifactId: NullableString(reader, 4),
                        GeneratedAt: NullableDateTimeOffset(reader, 9),
                        Metadata: MetadataFromJson(reader.GetString(10))));
                }

                return rows;
            });
    }

    public IReadOnlyList<CoverageSpan> ListCoverageSpansCovering(
        string workspaceId,
        IReadOnlyList<string> symbolNames,
        IReadOnlyList<string> filePaths)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(symbolNames);
        ArgumentNullException.ThrowIfNull(filePaths);

        string[] symbols = symbolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] paths = filePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (symbols.Length == 0 && paths.Length == 0)
            return [];

        return WithRead<IReadOnlyList<CoverageSpan>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT coverage_spans.id, coverage_spans.workspace_id, coverage_spans.index_identity,
                           coverage_spans.revision, coverage_spans.coverage_file_id,
                           coalesce(coverage_spans.file_path, coverage_files.path),
                           coverage_spans.content_hash, coverage_spans.symbol_name, coverage_spans.symbol_path,
                           coverage_spans.start_line, coverage_spans.end_line, coverage_spans.hits,
                           coverage_spans.branch_hits, coverage_spans.metadata_json
                    FROM coverage_spans
                    JOIN coverage_files ON coverage_files.id = coverage_spans.coverage_file_id
                    WHERE coverage_spans.workspace_id = $ws
                      AND coverage_spans.hits > 0
                      AND (
                            EXISTS (SELECT 1 FROM json_each($symbols) WHERE json_each.value = coverage_spans.symbol_name)
                         OR EXISTS (SELECT 1 FROM json_each($paths) WHERE json_each.value = coverage_spans.file_path)
                         OR EXISTS (SELECT 1 FROM json_each($paths) WHERE json_each.value = coverage_files.path)
                      )
                    ORDER BY coverage_spans.id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                command.Parameters.AddWithValue("$symbols", TestingJson.Strings(symbols));
                command.Parameters.AddWithValue("$paths", TestingJson.Strings(paths));
                var rows = new List<CoverageSpan>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new CoverageSpan(
                        Id: reader.GetString(0),
                        WorkspaceId: reader.GetString(1),
                        IndexIdentity: reader.GetString(2),
                        Revision: reader.GetInt64(3),
                        CoverageFileId: reader.GetString(4),
                        StartLine: reader.GetInt32(9),
                        EndLine: reader.GetInt32(10),
                        Hits: reader.GetInt32(11),
                        FilePath: NullableString(reader, 5),
                        ContentHash: NullableString(reader, 6),
                        SymbolName: NullableString(reader, 7),
                        SymbolPath: NullableString(reader, 8),
                        BranchHits: reader.IsDBNull(12) ? null : reader.GetInt32(12),
                        Metadata: MetadataFromJson(reader.GetString(13))));
                }

                return rows;
            });
    }

    public void PutTestLink(CtTestLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO test_links (
                    id, workspace_id, test_case_id, source_file_path, source_content_hash,
                    source_symbol_name, source_symbol_path, tier, confidence, explanation,
                    source_fact_ids_json, metadata_json
                )
                VALUES (
                    $id, $ws, $case, $file, $hash, $symbolName, $symbolPath, $tier, $confidence,
                    $explanation, $facts, $metadata
                )
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    test_case_id = excluded.test_case_id,
                    source_file_path = excluded.source_file_path,
                    source_content_hash = excluded.source_content_hash,
                    source_symbol_name = excluded.source_symbol_name,
                    source_symbol_path = excluded.source_symbol_path,
                    tier = excluded.tier,
                    confidence = excluded.confidence,
                    explanation = excluded.explanation,
                    source_fact_ids_json = excluded.source_fact_ids_json,
                    metadata_json = excluded.metadata_json;
                """;
            command.Parameters.AddWithValue("$id", link.Id);
            command.Parameters.AddWithValue("$ws", link.WorkspaceId);
            command.Parameters.AddWithValue("$case", (object?)link.TestCaseId ?? DBNull.Value);
            command.Parameters.AddWithValue("$file", (object?)link.SourceFilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)link.SourceContentHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolName", (object?)link.SourceSymbolName ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolPath", (object?)link.SourceSymbolPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$tier", link.Tier);
            command.Parameters.AddWithValue("$confidence", link.Confidence);
            command.Parameters.AddWithValue("$explanation", link.Explanation);
            command.Parameters.AddWithValue("$facts", JsonText(link.SourceFactIds));
            command.Parameters.AddWithValue("$metadata", JsonText(link.Metadata));
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<CtTestLink> ListTestLinks(
        string workspaceId,
        string? sourceSymbolName = null,
        string? sourceFilePath = null)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<CtTestLink>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, test_case_id, source_file_path, source_content_hash,
                           source_symbol_name, source_symbol_path, tier, confidence, explanation,
                           source_fact_ids_json, metadata_json
                    FROM test_links
                    WHERE workspace_id = $ws
                      AND ($symbol IS NULL OR source_symbol_name = $symbol)
                      AND ($file IS NULL OR source_file_path = $file)
                    ORDER BY id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                command.Parameters.AddWithValue("$symbol", (object?)sourceSymbolName ?? DBNull.Value);
                command.Parameters.AddWithValue("$file", (object?)sourceFilePath ?? DBNull.Value);
                var rows = new List<CtTestLink>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new CtTestLink(
                        Id: reader.GetString(0),
                        WorkspaceId: reader.GetString(1),
                        Tier: reader.GetString(7),
                        Confidence: reader.GetDouble(8),
                        Explanation: reader.GetString(9),
                        TestCaseId: NullableString(reader, 2),
                        SourceFilePath: NullableString(reader, 3),
                        SourceContentHash: NullableString(reader, 4),
                        SourceSymbolName: NullableString(reader, 5),
                        SourceSymbolPath: NullableString(reader, 6),
                        SourceFactIds: StringListFromJson(reader.GetString(10)),
                        Metadata: MetadataFromJson(reader.GetString(11))));
                }

                return rows;
            });
    }

    public CtLatestTestResult? GetLatestTestResult(string workspaceId, string testCaseId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrEmpty(testCaseId))
            throw new ArgumentException("must not be empty", nameof(testCaseId));

        return WithRead<CtLatestTestResult?>(
            static () => null,
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT test_results.id,
                           test_results.status,
                           test_results.test_run_id,
                           test_results.result_revision,
                           test_results.failure_summary
                    FROM test_results
                    LEFT JOIN test_runs ON test_runs.id = test_results.test_run_id
                    WHERE test_results.workspace_id = $ws AND test_results.test_case_id = $id
                    ORDER BY coalesce(test_runs.ended_at, test_runs.started_at, test_results.id) DESC,
                             test_results.id DESC
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                command.Parameters.AddWithValue("$id", testCaseId);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;
                return new CtLatestTestResult(
                    Id: reader.GetString(0),
                    Status: reader.GetString(1),
                    TestRunId: reader.GetString(2),
                    ResultRevision: NullableString(reader, 3),
                    FailureSummary: NullableString(reader, 4));
            });
    }

    public void PutTestQualityFinding(ContinuousTestQualityFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO test_quality_findings (
                    id, workspace_id, test_case_id, file_path, content_hash, symbol_name, symbol_path,
                    finding_type, severity, confidence, explanation, evidence_json
                )
                VALUES (
                    $id, $ws, $case, $file, $hash, $symbolName, $symbolPath,
                    $type, $severity, $confidence, $explanation, $evidence
                )
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    test_case_id = excluded.test_case_id,
                    file_path = excluded.file_path,
                    content_hash = excluded.content_hash,
                    symbol_name = excluded.symbol_name,
                    symbol_path = excluded.symbol_path,
                    finding_type = excluded.finding_type,
                    severity = excluded.severity,
                    confidence = excluded.confidence,
                    explanation = excluded.explanation,
                    evidence_json = excluded.evidence_json;
                """;
            command.Parameters.AddWithValue("$id", finding.Id);
            command.Parameters.AddWithValue("$ws", finding.WorkspaceId);
            command.Parameters.AddWithValue("$case", finding.TestCaseId);
            command.Parameters.AddWithValue("$file", (object?)finding.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)finding.ContentHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolName", (object?)finding.SymbolName ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolPath", (object?)finding.SymbolPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", finding.FindingType);
            command.Parameters.AddWithValue("$severity", finding.Severity);
            command.Parameters.AddWithValue("$confidence", finding.Confidence);
            command.Parameters.AddWithValue("$explanation", finding.Explanation);
            command.Parameters.AddWithValue("$evidence", JsonText(finding.Evidence));
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<ContinuousTestQualityFinding> ListTestQualityFindings(string workspaceId, string testCaseId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrEmpty(testCaseId))
            throw new ArgumentException("must not be empty", nameof(testCaseId));

        return WithRead<IReadOnlyList<ContinuousTestQualityFinding>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, test_case_id, file_path, content_hash, symbol_name, symbol_path,
                           finding_type, severity, confidence, explanation, evidence_json
                    FROM test_quality_findings
                    WHERE workspace_id = $ws AND test_case_id = $id
                    ORDER BY id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                command.Parameters.AddWithValue("$id", testCaseId);
                var rows = new List<ContinuousTestQualityFinding>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ContinuousTestQualityFinding(
                        Id: reader.GetString(0),
                        WorkspaceId: reader.GetString(1),
                        TestCaseId: reader.GetString(2),
                        FindingType: reader.GetString(7),
                        Severity: reader.GetString(8),
                        Confidence: reader.GetDouble(9),
                        Explanation: reader.GetString(10),
                        Evidence: MetadataFromJson(reader.GetString(11)),
                        FilePath: NullableString(reader, 3),
                        ContentHash: NullableString(reader, 4),
                        SymbolName: NullableString(reader, 5),
                        SymbolPath: NullableString(reader, 6)));
                }

                return rows;
            });
    }

    public void PutImplementationQualityFinding(ContinuousImplementationQualityFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO implementation_quality_findings (
                    id, workspace_id, file_path, content_hash, symbol_name, symbol_path,
                    finding_type, severity, confidence, explanation, evidence_json
                )
                VALUES (
                    $id, $ws, $file, $hash, $symbolName, $symbolPath,
                    $type, $severity, $confidence, $explanation, $evidence
                )
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    file_path = excluded.file_path,
                    content_hash = excluded.content_hash,
                    symbol_name = excluded.symbol_name,
                    symbol_path = excluded.symbol_path,
                    finding_type = excluded.finding_type,
                    severity = excluded.severity,
                    confidence = excluded.confidence,
                    explanation = excluded.explanation,
                    evidence_json = excluded.evidence_json;
                """;
            command.Parameters.AddWithValue("$id", finding.Id);
            command.Parameters.AddWithValue("$ws", finding.WorkspaceId);
            command.Parameters.AddWithValue("$file", (object?)finding.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)finding.ContentHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolName", (object?)finding.SymbolName ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolPath", (object?)finding.SymbolPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", finding.FindingType);
            command.Parameters.AddWithValue("$severity", finding.Severity);
            command.Parameters.AddWithValue("$confidence", finding.Confidence);
            command.Parameters.AddWithValue("$explanation", finding.Explanation);
            command.Parameters.AddWithValue("$evidence", JsonText(finding.Evidence));
            command.ExecuteNonQuery();
        });
    }

    public void PutConfidenceSnapshot(ContinuousTestConfidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO confidence_snapshots (
                    id, workspace_id, index_identity, revision, subject_type, subject_id, state, score,
                    evidence_json, freshness_json, limitations_json, recommended_command
                )
                VALUES (
                    $id, $ws, $identity, $revision, $type, $subject, $state, $score,
                    $evidence, $freshness, $limitations, $command
                )
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    index_identity = excluded.index_identity,
                    revision = excluded.revision,
                    subject_type = excluded.subject_type,
                    subject_id = excluded.subject_id,
                    state = excluded.state,
                    score = excluded.score,
                    evidence_json = excluded.evidence_json,
                    freshness_json = excluded.freshness_json,
                    limitations_json = excluded.limitations_json,
                    recommended_command = excluded.recommended_command,
                    computed_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now');
                """;
            command.Parameters.AddWithValue("$id", snapshot.Id);
            command.Parameters.AddWithValue("$ws", snapshot.WorkspaceId);
            command.Parameters.AddWithValue("$identity", snapshot.IndexIdentity);
            command.Parameters.AddWithValue("$revision", snapshot.Revision);
            command.Parameters.AddWithValue("$type", snapshot.SubjectType);
            command.Parameters.AddWithValue("$subject", snapshot.SubjectId);
            command.Parameters.AddWithValue("$state", ContinuousTestConfidenceEngine.StateValue(snapshot.State));
            command.Parameters.AddWithValue("$score", snapshot.Score);
            command.Parameters.AddWithValue("$evidence", JsonText(snapshot.Evidence));
            command.Parameters.AddWithValue("$freshness", JsonText(snapshot.Freshness));
            command.Parameters.AddWithValue("$limitations", JsonText(snapshot.Limitations));
            command.Parameters.AddWithValue("$command", (object?)snapshot.RecommendedCommand ?? DBNull.Value);
            command.ExecuteNonQuery();
        });
    }

    public ContinuousTestConfidenceSnapshot? GetConfidenceSnapshot(string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        return WithRead<ContinuousTestConfidenceSnapshot?>(
            static () => null,
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, index_identity, revision, subject_type, subject_id, state, score,
                           evidence_json, freshness_json, limitations_json, recommended_command
                    FROM confidence_snapshots
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", snapshotId);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;
                return ReadConfidenceSnapshot(reader);
            });
    }

    public IReadOnlyDictionary<string, int> CountConfidenceStates(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        var counts = ContinuousTestConfidenceEngine.StateNames.ToDictionary(state => state, _ => 0, StringComparer.Ordinal);
        return WithRead(
            () => counts,
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT state, count(*) AS count
                    FROM confidence_snapshots
                    WHERE workspace_id = $ws
                    GROUP BY state;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    counts[reader.GetString(0)] = Convert.ToInt32(reader.GetInt64(1));
                return counts;
            });
    }

    public IReadOnlyDictionary<string, int> CountArtifacts(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        Dictionary<string, int> empty = new()
        {
            ["result_artifacts"] = 0,
            ["coverage_artifacts"] = 0,
            ["test_results"] = 0,
            ["coverage_files"] = 0,
            ["coverage_spans"] = 0,
            ["diagnostics"] = 0,
        };

        return WithRead<IReadOnlyDictionary<string, int>>(
            () => empty,
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        (SELECT count(*) FROM run_artifacts
                         WHERE workspace_id = $ws AND kind = 'test_results') AS result_artifacts,
                        (SELECT count(*) FROM run_artifacts
                         WHERE workspace_id = $ws AND kind = 'coverage') AS coverage_artifacts,
                        (SELECT count(*) FROM test_results WHERE workspace_id = $ws) AS test_results,
                        (SELECT count(*) FROM coverage_files WHERE workspace_id = $ws) AS coverage_files,
                        (SELECT count(*) FROM coverage_spans WHERE workspace_id = $ws) AS coverage_spans,
                        (SELECT count(*) FROM ct_parser_diagnostics
                         WHERE workspace_id = $ws AND code LIKE 'test_artifact.%') AS diagnostics;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return empty;
                return new Dictionary<string, int>
                {
                    ["result_artifacts"] = Convert.ToInt32(reader.GetInt64(0)),
                    ["coverage_artifacts"] = Convert.ToInt32(reader.GetInt64(1)),
                    ["test_results"] = Convert.ToInt32(reader.GetInt64(2)),
                    ["coverage_files"] = Convert.ToInt32(reader.GetInt64(3)),
                    ["coverage_spans"] = Convert.ToInt32(reader.GetInt64(4)),
                    ["diagnostics"] = Convert.ToInt32(reader.GetInt64(5)),
                };
            });
    }

    public ContinuousTestQualityCounts CountQualityFindings(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead(
            static () => new ContinuousTestQualityCounts(0, 0),
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        (SELECT count(*) FROM test_quality_findings WHERE workspace_id = $ws) AS weak_tests,
                        (SELECT count(*) FROM implementation_quality_findings WHERE workspace_id = $ws) AS stubs;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return new ContinuousTestQualityCounts(0, 0);
                return new ContinuousTestQualityCounts(
                    WeakTests: Convert.ToInt32(reader.GetInt64(0)),
                    Stubs: Convert.ToInt32(reader.GetInt64(1)));
            });
    }

    public void PutParserDiagnostic(string workspaceId, ContinuousTestParserDiagnostic diagnostic)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(diagnostic);

        string id = CtStableIds.StableId("ct_parser_diagnostic", workspaceId, diagnostic.Code, diagnostic.Message);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ct_parser_diagnostics (id, workspace_id, code, message, severity)
                VALUES ($id, $ws, $code, $message, $severity)
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    code = excluded.code,
                    message = excluded.message,
                    severity = excluded.severity;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$ws", workspaceId);
            command.Parameters.AddWithValue("$code", (object?)diagnostic.Code ?? DBNull.Value);
            command.Parameters.AddWithValue("$message", diagnostic.Message);
            command.Parameters.AddWithValue("$severity", diagnostic.Severity);
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<ContinuousTestParserDiagnostic> ListParserDiagnostics(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<ContinuousTestParserDiagnostic>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT code, message, severity
                    FROM ct_parser_diagnostics
                    WHERE workspace_id = $ws AND code LIKE 'test_artifact.%'
                    ORDER BY severity DESC, code, id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                var rows = new List<ContinuousTestParserDiagnostic>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ContinuousTestParserDiagnostic(
                        Code: NullableString(reader, 0),
                        Message: reader.GetString(1),
                        Severity: reader.GetString(2)));
                }

                return rows;
            });
    }

    public IReadOnlyList<CtTestResultHistoryRow> ListTestResultHistories(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<CtTestResultHistoryRow>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT tr.test_case_id,
                           coalesce(tc.selector, tr.test_case_id) AS selector,
                           tr.status,
                           coalesce(r.ended_at, r.started_at) AS observed_at
                    FROM test_results tr
                    LEFT JOIN test_runs r ON r.id = tr.test_run_id
                    LEFT JOIN test_cases tc
                      ON tc.id = tr.test_case_id AND tc.workspace_id = tr.workspace_id
                    WHERE tr.workspace_id = $ws
                    ORDER BY tr.test_case_id,
                             coalesce(julianday(coalesce(r.ended_at, r.started_at)), 0),
                             tr.id;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                var rows = new List<CtTestResultHistoryRow>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new CtTestResultHistoryRow(
                        TestCaseId: reader.GetString(0),
                        Selector: reader.GetString(1),
                        Status: reader.GetString(2),
                        ObservedAt: NullableDateTimeOffset(reader, 3)));
                }

                return rows;
            });
    }

    public string? LatestTestRunAt(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<string?>(
            static () => null,
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT coalesce(ended_at, started_at) AS timestamp
                    FROM test_runs
                    WHERE workspace_id = $ws
                    ORDER BY julianday(coalesce(ended_at, started_at)) DESC, id DESC
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                object? value = command.ExecuteScalar();
                return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            });
    }

    public string? LatestCoverageGeneratedAt(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<string?>(
            static () => null,
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT generated_at
                    FROM coverage_files
                    WHERE workspace_id = $ws
                    ORDER BY coalesce(generated_at, '') DESC, id DESC
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                object? value = command.ExecuteScalar();
                return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            });
    }

    private static ContinuousTestConfidenceSnapshot ReadConfidenceSnapshot(SqliteDataReader reader) =>
        new(
            Id: reader.GetString(0),
            WorkspaceId: reader.GetString(1),
            SubjectType: reader.GetString(4),
            SubjectId: reader.GetString(5),
            State: ContinuousTestConfidenceEngine.ParseState(reader.GetString(6)),
            Score: reader.GetDouble(7),
            Evidence: DictListFromJson(reader.GetString(8)),
            Freshness: MetadataFromJson(reader.GetString(9)),
            Limitations: StringListFromJson(reader.GetString(10)),
            RecommendedCommand: NullableString(reader, 11),
            IndexIdentity: reader.GetString(2),
            Revision: reader.GetInt64(3));

    private static IReadOnlyList<string> StringListFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        return TestingJson.ReadStrings(json);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> DictListFromJson(string json) =>
        TestingJson.ObjectList(json);
}
