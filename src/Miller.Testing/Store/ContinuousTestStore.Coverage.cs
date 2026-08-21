using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Miller.Testing;

public sealed record CoverageFile(
    string Id,
    string WorkspaceId,
    string IndexIdentity,
    long Revision,
    string Format,
    string Path,
    string Parser,
    string SourceHash,
    string? ArtifactId = null,
    DateTimeOffset? GeneratedAt = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = Metadata ?? new Dictionary<string, object?>();
}

public sealed record CoverageSpan(
    string Id,
    string WorkspaceId,
    string IndexIdentity,
    long Revision,
    string CoverageFileId,
    int StartLine,
    int EndLine,
    int Hits,
    string? FilePath = null,
    string? ContentHash = null,
    string? SymbolName = null,
    string? SymbolPath = null,
    int? BranchHits = null,
    IReadOnlyDictionary<string, object?>? Metadata = null)
{
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = Metadata ?? new Dictionary<string, object?>();
}

public sealed record CtCoverageMapFile(string FilePath, string? ContentHash);

public sealed record CtFreshWatermark(string TestCaseId, string IndexIdentity, long Revision);

public sealed partial class ContinuousTestStore
{
    private const string CtCoverageMapNamespace = "ct_coverage_map";

    public static string CtCoverageMapId(string workspaceId, string testCaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(testCaseId);
        return StableId(CtCoverageMapNamespace, workspaceId, testCaseId);
    }

    public void PutCoverageFile(CoverageFile coverageFile)
    {
        ArgumentNullException.ThrowIfNull(coverageFile);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO coverage_files (
                    id, workspace_id, index_identity, revision, artifact_id, format, path,
                    parser, source_hash, generated_at, metadata_json
                )
                VALUES (
                    $id, $ws, $identity, $revision, $artifact, $format, $path,
                    $parser, $hash, $generated, $metadata
                )
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    index_identity = excluded.index_identity,
                    revision = excluded.revision,
                    artifact_id = excluded.artifact_id,
                    format = excluded.format,
                    path = excluded.path,
                    parser = excluded.parser,
                    source_hash = excluded.source_hash,
                    generated_at = excluded.generated_at,
                    metadata_json = excluded.metadata_json;
                """;
            command.Parameters.AddWithValue("$id", coverageFile.Id);
            command.Parameters.AddWithValue("$ws", coverageFile.WorkspaceId);
            command.Parameters.AddWithValue("$identity", coverageFile.IndexIdentity);
            command.Parameters.AddWithValue("$revision", coverageFile.Revision);
            command.Parameters.AddWithValue("$artifact", (object?)coverageFile.ArtifactId ?? DBNull.Value);
            command.Parameters.AddWithValue("$format", coverageFile.Format);
            command.Parameters.AddWithValue("$path", coverageFile.Path);
            command.Parameters.AddWithValue("$parser", coverageFile.Parser);
            command.Parameters.AddWithValue("$hash", coverageFile.SourceHash);
            command.Parameters.AddWithValue("$generated", DateTimeText(coverageFile.GeneratedAt));
            command.Parameters.AddWithValue("$metadata", JsonText(coverageFile.Metadata));
            command.ExecuteNonQuery();
        });
    }

    public CoverageFile? GetCoverageFile(string coverageFileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coverageFileId);
        return WithRead<CoverageFile?>(
            static () => null,
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, index_identity, revision, artifact_id, format, path,
                           parser, source_hash, generated_at, metadata_json
                    FROM coverage_files
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", coverageFileId);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;
                return new CoverageFile(
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
                    Metadata: MetadataFromJson(reader.GetString(10)));
            });
    }

    public void PutCoverageSpan(CoverageSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO coverage_spans (
                    id, workspace_id, index_identity, revision, coverage_file_id, file_path,
                    content_hash, symbol_name, symbol_path, start_line, end_line, hits,
                    branch_hits, metadata_json
                )
                VALUES (
                    $id, $ws, $identity, $revision, $file, $path, $hash, $symbolName, $symbolPath,
                    $start, $end, $hits, $branch, $metadata
                )
                ON CONFLICT(id) DO UPDATE SET
                    workspace_id = excluded.workspace_id,
                    index_identity = excluded.index_identity,
                    revision = excluded.revision,
                    coverage_file_id = excluded.coverage_file_id,
                    file_path = excluded.file_path,
                    content_hash = excluded.content_hash,
                    symbol_name = excluded.symbol_name,
                    symbol_path = excluded.symbol_path,
                    start_line = excluded.start_line,
                    end_line = excluded.end_line,
                    hits = excluded.hits,
                    branch_hits = excluded.branch_hits,
                    metadata_json = excluded.metadata_json;
                """;
            command.Parameters.AddWithValue("$id", span.Id);
            command.Parameters.AddWithValue("$ws", span.WorkspaceId);
            command.Parameters.AddWithValue("$identity", span.IndexIdentity);
            command.Parameters.AddWithValue("$revision", span.Revision);
            command.Parameters.AddWithValue("$file", span.CoverageFileId);
            command.Parameters.AddWithValue("$path", (object?)span.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)span.ContentHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolName", (object?)span.SymbolName ?? DBNull.Value);
            command.Parameters.AddWithValue("$symbolPath", (object?)span.SymbolPath ?? DBNull.Value);
            command.Parameters.AddWithValue("$start", span.StartLine);
            command.Parameters.AddWithValue("$end", span.EndLine);
            command.Parameters.AddWithValue("$hits", span.Hits);
            command.Parameters.AddWithValue("$branch", (object?)span.BranchHits ?? DBNull.Value);
            command.Parameters.AddWithValue("$metadata", JsonText(span.Metadata));
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<CoverageSpan> ListCoverageSpans(string coverageFileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coverageFileId);
        return WithRead<IReadOnlyList<CoverageSpan>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT id, workspace_id, index_identity, revision, coverage_file_id, file_path,
                           content_hash, symbol_name, symbol_path, start_line, end_line, hits,
                           branch_hits, metadata_json
                    FROM coverage_spans
                    WHERE coverage_file_id = $file
                    ORDER BY start_line, id;
                    """;
                command.Parameters.AddWithValue("$file", coverageFileId);
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

    public void UpsertCtCoverageMap(CtCoverageMapRecord record, IReadOnlyList<CtCoverageMapFile> files)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.TestCaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.ProjectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.GenerationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Granularity);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.IndexIdentity);

        string expectedMapId = CtCoverageMapId(record.WorkspaceId, record.TestCaseId);
        if (!string.Equals(record.MapId, expectedMapId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"map id must be {expectedMapId} for ({record.WorkspaceId}, {record.TestCaseId})",
                nameof(record));
        }

        Transaction(() =>
        {
            using (var deleteFiles = _write!.CreateCommand())
            {
                deleteFiles.CommandText = "DELETE FROM ct_coverage_map_files WHERE map_id = $map_id;";
                deleteFiles.Parameters.AddWithValue("$map_id", expectedMapId);
                deleteFiles.ExecuteNonQuery();
            }

            using (var deleteMap = _write!.CreateCommand())
            {
                deleteMap.CommandText = """
                    DELETE FROM ct_coverage_maps
                    WHERE workspace_id = $workspace_id AND test_case_id = $test_case_id;
                    """;
                deleteMap.Parameters.AddWithValue("$workspace_id", record.WorkspaceId);
                deleteMap.Parameters.AddWithValue("$test_case_id", record.TestCaseId);
                deleteMap.ExecuteNonQuery();
            }

            using (var insertMap = _write!.CreateCommand())
            {
                insertMap.CommandText = """
                    INSERT INTO ct_coverage_maps (
                        map_id, workspace_id, index_identity, revision, test_case_id, project_path,
                        run_id, generation_id, revision_at_start, start_converged, revision_at_end,
                        end_converged, complete, failure_reason, granularity, valid_through_revision,
                        invalidated_at_revision, recorded_at, source
                    )
                    VALUES (
                        $map_id, $workspace_id, $identity, $revision, $test_case_id, $project_path,
                        $run_id, $generation_id, $revision_at_start, $start_converged, $revision_at_end,
                        $end_converged, $complete, $failure_reason, $granularity, $valid_through_revision,
                        $invalidated_at_revision, $recorded_at, $source
                    );
                    """;
                insertMap.Parameters.AddWithValue("$map_id", expectedMapId);
                insertMap.Parameters.AddWithValue("$workspace_id", record.WorkspaceId);
                insertMap.Parameters.AddWithValue("$identity", record.IndexIdentity);
                insertMap.Parameters.AddWithValue("$revision", record.Revision);
                insertMap.Parameters.AddWithValue("$test_case_id", record.TestCaseId);
                insertMap.Parameters.AddWithValue("$project_path", record.ProjectPath);
                insertMap.Parameters.AddWithValue("$run_id", record.RunId);
                insertMap.Parameters.AddWithValue("$generation_id", record.GenerationId);
                insertMap.Parameters.AddWithValue("$revision_at_start", NullableText(record.RevisionAtStart));
                insertMap.Parameters.AddWithValue("$start_converged", record.StartConverged ? 1 : 0);
                insertMap.Parameters.AddWithValue("$revision_at_end", NullableText(record.RevisionAtEnd));
                insertMap.Parameters.AddWithValue("$end_converged", record.EndConverged ? 1 : 0);
                insertMap.Parameters.AddWithValue("$complete", record.Complete ? 1 : 0);
                insertMap.Parameters.AddWithValue("$failure_reason", NullableText(record.FailureReason));
                insertMap.Parameters.AddWithValue("$granularity", record.Granularity);
                insertMap.Parameters.AddWithValue("$valid_through_revision", NullableText(record.ValidThroughRevision));
                insertMap.Parameters.AddWithValue("$invalidated_at_revision", NullableText(record.InvalidatedAtRevision));
                insertMap.Parameters.AddWithValue("$recorded_at", DateTimeText(record.RecordedAt));
                insertMap.Parameters.AddWithValue("$source", record.Source);
                insertMap.ExecuteNonQuery();
            }

            foreach (CtCoverageMapFile file in files
                .DistinctBy(row => row.FilePath, StringComparer.Ordinal)
                .OrderBy(row => row.FilePath, StringComparer.Ordinal))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(file.FilePath, nameof(files));
                using var insertFile = _write!.CreateCommand();
                insertFile.CommandText = """
                    INSERT INTO ct_coverage_map_files (
                        map_id, workspace_id, index_identity, revision, file_path, content_hash
                    )
                    VALUES ($map_id, $workspace_id, $identity, $revision, $file_path, $hash);
                    """;
                insertFile.Parameters.AddWithValue("$map_id", expectedMapId);
                insertFile.Parameters.AddWithValue("$workspace_id", record.WorkspaceId);
                insertFile.Parameters.AddWithValue("$identity", record.IndexIdentity);
                insertFile.Parameters.AddWithValue("$revision", record.Revision);
                insertFile.Parameters.AddWithValue("$file_path", file.FilePath);
                insertFile.Parameters.AddWithValue("$hash", (object?)file.ContentHash ?? DBNull.Value);
                insertFile.ExecuteNonQuery();
            }
        });
    }

    public CtCoverageMapRecord? GetCtCoverageMap(string workspaceId, string testCaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(testCaseId);

        return WithRead<CtCoverageMapRecord?>(
            static () => null,
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = CtCoverageOwnedMapSelect + """
                    WHERE m.workspace_id = $workspace_id AND m.test_case_id = $test_case_id
                    """;
                command.Parameters.AddWithValue("$workspace_id", workspaceId);
                command.Parameters.AddWithValue("$test_case_id", testCaseId);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;
                CtCoverageMapRecord map = ReadCtCoverageMap(reader);
                return !reader.IsDBNull(19) && PathEquals(map.ProjectPath, reader.GetString(19)) ? map : null;
            });
    }

    public IReadOnlyList<CtCoverageMapRecord> ListCtCoverageMaps(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        return WithRead<IReadOnlyList<CtCoverageMapRecord>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = CtCoverageOwnedMapSelect + """
                    WHERE m.workspace_id = $workspace_id
                    ORDER BY m.test_case_id
                    """;
                command.Parameters.AddWithValue("$workspace_id", workspaceId);
                using SqliteDataReader reader = command.ExecuteReader();
                var maps = new List<CtCoverageMapRecord>();
                while (reader.Read())
                {
                    CtCoverageMapRecord map = ReadCtCoverageMap(reader);
                    if (!reader.IsDBNull(19) && PathEquals(map.ProjectPath, reader.GetString(19)))
                        maps.Add(map);
                }

                return maps;
            });
    }

    public IReadOnlyList<CtCoverageMapFile> ListCtCoverageMapFiles(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        return WithRead<IReadOnlyList<CtCoverageMapFile>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT file.file_path, file.content_hash, map.project_path,
                           json_extract(test.metadata_json, '$.ct_project_path')
                    FROM ct_coverage_map_files file
                    JOIN ct_coverage_maps map ON map.map_id = file.map_id
                    JOIN test_cases test
                      ON test.id = map.test_case_id AND test.workspace_id = map.workspace_id
                    WHERE file.map_id = $map_id
                    ORDER BY file.file_path;
                    """;
                command.Parameters.AddWithValue("$map_id", mapId);
                using SqliteDataReader reader = command.ExecuteReader();
                var files = new List<CtCoverageMapFile>();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(3) && PathEquals(reader.GetString(2), reader.GetString(3)))
                        files.Add(new CtCoverageMapFile(reader.GetString(0), NullableString(reader, 1)));
                }

                return files;
            });
    }

    public IReadOnlyList<CtCoverageNarrowingEvidence> ListCtCoverageNarrowingEvidence(
        string workspaceId,
        string projectPath,
        IReadOnlyList<string> testCaseIds,
        CtFreshnessKey selected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(testCaseIds);

        string[] requestedIds = testCaseIds
            .Select(id =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(testCaseIds));
                return id;
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (requestedIds.Length == 0)
            return [];

        string normalizedProjectPath = Path.GetFullPath(projectPath);
        return WithRead<IReadOnlyList<CtCoverageNarrowingEvidence>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT requested.value,
                           m.map_id, m.workspace_id, m.test_case_id, m.project_path, m.run_id,
                           m.generation_id, m.index_identity, m.revision, m.revision_at_start,
                           m.start_converged, m.revision_at_end, m.end_converged, m.complete,
                           m.failure_reason, m.granularity, m.valid_through_revision,
                           m.invalidated_at_revision, m.recorded_at, m.source,
                           json_extract(tc.metadata_json, '$.ct_project_path')
                    FROM json_each($test_case_ids) requested
                    LEFT JOIN test_cases tc
                      ON tc.id = requested.value AND tc.workspace_id = $workspace_id
                    LEFT JOIN ct_coverage_maps m
                      ON m.test_case_id = tc.id AND m.workspace_id = tc.workspace_id
                    ORDER BY requested.value;
                    """;
                command.Parameters.AddWithValue("$test_case_ids", TestingJson.Strings(requestedIds));
                command.Parameters.AddWithValue("$workspace_id", workspaceId);
                using SqliteDataReader reader = command.ExecuteReader();
                var evidence = new List<CtCoverageNarrowingEvidence>(requestedIds.Length);
                while (reader.Read())
                {
                    string testCaseId = reader.GetString(0);
                    CtCoverageMapRecord? map = null;
                    if (!reader.IsDBNull(1)
                        && !reader.IsDBNull(20)
                        && PathEquals(reader.GetString(4), reader.GetString(20))
                        && PathEquals(reader.GetString(20), normalizedProjectPath))
                    {
                        map = ReadCtCoverageMap(reader, offset: 1);
                    }

                    evidence.Add(new CtCoverageNarrowingEvidence(
                        testCaseId,
                        map,
                        map is not null && IsTrustedAt(map, selected)));
                }

                return evidence;
            });
    }

    public CtCoverageDeltaApplyResult ApplyCtCoverageDelta(
        string workspaceId,
        CtFreshnessKey from,
        CtFreshnessKey to,
        IReadOnlyList<string> changedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(changedPaths);
        if (!string.Equals(from.IndexIdentity, to.IndexIdentity, StringComparison.Ordinal))
            throw new ArgumentException("delta endpoints must share an index identity", nameof(to));

        string[] normalizedPaths = changedPaths
            .Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(changedPaths));
                return NormalizeCoveragePath(path);
            })
            .Distinct(PathComparer)
            .Order(PathComparer)
            .ToArray();
        var normalizedPathSet = normalizedPaths.ToHashSet(PathComparer);
        string digest = CoveragePathDigest(normalizedPaths);
        string fromRevision = RevisionText(from.Revision);
        string toRevision = RevisionText(to.Revision);
        var result = new CtCoverageDeltaApplyResult(CtCoverageDeltaApplyStatus.Applied, 0, 0);

        Transaction(() =>
        {
            using (var receipt = _write!.CreateCommand())
            {
                receipt.CommandText = """
                    SELECT changed_paths_digest
                    FROM ct_coverage_delta_receipts
                    WHERE workspace_id = $workspace_id
                      AND index_identity = $identity
                      AND from_revision = $from_revision
                      AND to_revision = $to_revision;
                    """;
                receipt.Parameters.AddWithValue("$workspace_id", workspaceId);
                receipt.Parameters.AddWithValue("$identity", from.IndexIdentity);
                receipt.Parameters.AddWithValue("$from_revision", fromRevision);
                receipt.Parameters.AddWithValue("$to_revision", toRevision);
                if (receipt.ExecuteScalar() is string previousDigest)
                {
                    bool mismatch = !string.Equals(previousDigest, digest, StringComparison.Ordinal);
                    int mismatchInvalidated = 0;
                    if (mismatch)
                    {
                        using var invalidate = _write!.CreateCommand();
                        invalidate.CommandText = """
                            UPDATE ct_coverage_maps
                            SET invalidated_at_revision = $to_revision
                            WHERE workspace_id = $workspace_id
                              AND index_identity = $identity
                              AND invalidated_at_revision IS NULL
                              AND map_id IN (
                                  SELECT map_id
                                  FROM ct_coverage_delta_map_applications
                                  WHERE workspace_id = $workspace_id
                                    AND index_identity = $identity
                                    AND from_revision = $from_revision
                                    AND to_revision = $to_revision
                              );
                            """;
                        invalidate.Parameters.AddWithValue("$to_revision", toRevision);
                        invalidate.Parameters.AddWithValue("$workspace_id", workspaceId);
                        invalidate.Parameters.AddWithValue("$identity", from.IndexIdentity);
                        invalidate.Parameters.AddWithValue("$from_revision", fromRevision);
                        mismatchInvalidated = invalidate.ExecuteNonQuery();
                    }

                    result = new CtCoverageDeltaApplyResult(
                        mismatch ? CtCoverageDeltaApplyStatus.Rejected : CtCoverageDeltaApplyStatus.AlreadyApplied,
                        0,
                        mismatchInvalidated);
                    return;
                }
            }

            using (var insertReceipt = _write!.CreateCommand())
            {
                insertReceipt.CommandText = """
                    INSERT INTO ct_coverage_delta_receipts (
                        workspace_id, index_identity, revision, from_revision, to_revision,
                        changed_paths_digest, applied_at
                    ) VALUES (
                        $workspace_id, $identity, $revision, $from_revision, $to_revision,
                        $changed_paths_digest, $applied_at
                    );
                    """;
                insertReceipt.Parameters.AddWithValue("$workspace_id", workspaceId);
                insertReceipt.Parameters.AddWithValue("$identity", from.IndexIdentity);
                insertReceipt.Parameters.AddWithValue("$revision", to.Revision);
                insertReceipt.Parameters.AddWithValue("$from_revision", fromRevision);
                insertReceipt.Parameters.AddWithValue("$to_revision", toRevision);
                insertReceipt.Parameters.AddWithValue("$changed_paths_digest", digest);
                insertReceipt.Parameters.AddWithValue("$applied_at", DateTimeText(DateTimeOffset.UtcNow));
                insertReceipt.ExecuteNonQuery();
            }

            IReadOnlyList<CoverageDeltaCandidate> candidates = ReadCoverageDeltaCandidates(workspaceId, from);
            int advanced = 0;
            int invalidated = 0;
            foreach (CoverageDeltaCandidate candidate in candidates)
            {
                bool intersects = candidate.FilePaths.Any(normalizedPathSet.Contains);
                using var update = _write!.CreateCommand();
                update.CommandText = intersects
                    ? """
                        UPDATE ct_coverage_maps
                        SET invalidated_at_revision = $to_revision
                        WHERE map_id = $map_id
                          AND workspace_id = $workspace_id
                          AND index_identity = $identity
                          AND valid_through_revision = $from_revision
                          AND invalidated_at_revision IS NULL;
                        """
                    : """
                        UPDATE ct_coverage_maps
                        SET valid_through_revision = $to_revision
                        WHERE map_id = $map_id
                          AND workspace_id = $workspace_id
                          AND index_identity = $identity
                          AND valid_through_revision = $from_revision
                          AND invalidated_at_revision IS NULL;
                        """;
                update.Parameters.AddWithValue("$to_revision", toRevision);
                update.Parameters.AddWithValue("$map_id", candidate.Map.MapId);
                update.Parameters.AddWithValue("$workspace_id", workspaceId);
                update.Parameters.AddWithValue("$identity", from.IndexIdentity);
                update.Parameters.AddWithValue("$from_revision", fromRevision);
                if (update.ExecuteNonQuery() != 1)
                    continue;

                using var application = _write!.CreateCommand();
                application.CommandText = """
                    INSERT INTO ct_coverage_delta_map_applications (
                        workspace_id, index_identity, revision, from_revision, to_revision, map_id
                    ) VALUES ($workspace_id, $identity, $revision, $from_revision, $to_revision, $map_id);
                    """;
                application.Parameters.AddWithValue("$workspace_id", workspaceId);
                application.Parameters.AddWithValue("$identity", from.IndexIdentity);
                application.Parameters.AddWithValue("$revision", to.Revision);
                application.Parameters.AddWithValue("$from_revision", fromRevision);
                application.Parameters.AddWithValue("$to_revision", toRevision);
                application.Parameters.AddWithValue("$map_id", candidate.Map.MapId);
                application.ExecuteNonQuery();

                if (intersects)
                    invalidated += 1;
                else
                    advanced += 1;
            }

            result = new CtCoverageDeltaApplyResult(CtCoverageDeltaApplyStatus.Applied, advanced, invalidated);
        });

        return result;
    }

    public CtCoverageMaintenanceBatch? ClaimNextCtCoverageMaintenanceBatch(
        string workspaceId,
        IReadOnlyList<string> eligibleProjectPaths,
        int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(eligibleProjectPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        string[] eligible = eligibleProjectPaths
            .Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(eligibleProjectPaths));
                return Path.GetFullPath(path);
            })
            .Distinct(PathComparer)
            .Order(PathComparer)
            .ToArray();
        if (eligible.Length == 0)
            return null;

        CtCoverageMaintenanceBatch? claimed = null;
        Transaction(() =>
        {
            var offers = new List<(string ProjectPath, long Sequence)>();
            using (var readOffers = _write!.CreateCommand())
            {
                readOffers.CommandText = """
                    SELECT project_path, last_offer_sequence
                    FROM ct_coverage_project_offers
                    WHERE workspace_id = $workspace_id;
                    """;
                readOffers.Parameters.AddWithValue("$workspace_id", workspaceId);
                using SqliteDataReader reader = readOffers.ExecuteReader();
                while (reader.Read())
                    offers.Add((reader.GetString(0), reader.GetInt64(1)));
            }

            var projects = eligible
                .Select(path =>
                {
                    (string ProjectPath, long Sequence) offer =
                        offers.FirstOrDefault(existing => PathEquals(existing.ProjectPath, path));
                    return (Path: path, StoredPath: offer.ProjectPath, Sequence: offer.Sequence);
                })
                .OrderBy(project => project.StoredPath is null ? 0 : 1)
                .ThenBy(project => project.Sequence)
                .ThenBy(project => project.Path, PathComparer)
                .ToArray();

            string? chosenPath = null;
            string? storedPath = null;
            IReadOnlyList<string>? testCaseIds = null;
            foreach (var project in projects)
            {
                IReadOnlyList<string> candidates = ListCtCoverageMapCandidates(workspaceId, project.Path, limit);
                if (candidates.Count == 0)
                    continue;

                chosenPath = project.Path;
                storedPath = project.StoredPath;
                testCaseIds = candidates;
                break;
            }

            if (chosenPath is null || testCaseIds is null)
                return;

            using (var createState = _write!.CreateCommand())
            {
                createState.CommandText = """
                    INSERT INTO ct_coverage_maintenance_state (workspace_id, next_offer_sequence)
                    VALUES ($workspace_id, 1)
                    ON CONFLICT(workspace_id) DO NOTHING;
                    """;
                createState.Parameters.AddWithValue("$workspace_id", workspaceId);
                createState.ExecuteNonQuery();
            }

            long sequence;
            using (var readSequence = _write!.CreateCommand())
            {
                readSequence.CommandText = """
                    SELECT next_offer_sequence
                    FROM ct_coverage_maintenance_state
                    WHERE workspace_id = $workspace_id;
                    """;
                readSequence.Parameters.AddWithValue("$workspace_id", workspaceId);
                sequence = (long)readSequence.ExecuteScalar()!;
            }

            using (var advanceSequence = _write!.CreateCommand())
            {
                advanceSequence.CommandText = """
                    UPDATE ct_coverage_maintenance_state
                    SET next_offer_sequence = next_offer_sequence + 1
                    WHERE workspace_id = $workspace_id
                      AND next_offer_sequence = $expected_sequence;
                    """;
                advanceSequence.Parameters.AddWithValue("$workspace_id", workspaceId);
                advanceSequence.Parameters.AddWithValue("$expected_sequence", sequence);
                if (advanceSequence.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("coverage maintenance offer sequence changed unexpectedly");
            }

            if (storedPath is null)
            {
                using var insertOffer = _write!.CreateCommand();
                insertOffer.CommandText = """
                    INSERT INTO ct_coverage_project_offers (
                        workspace_id, project_path, last_offer_sequence
                    ) VALUES ($workspace_id, $project_path, $last_offer_sequence);
                    """;
                insertOffer.Parameters.AddWithValue("$workspace_id", workspaceId);
                insertOffer.Parameters.AddWithValue("$project_path", chosenPath);
                insertOffer.Parameters.AddWithValue("$last_offer_sequence", sequence);
                insertOffer.ExecuteNonQuery();
            }
            else
            {
                using var updateOffer = _write!.CreateCommand();
                updateOffer.CommandText = """
                    UPDATE ct_coverage_project_offers
                    SET last_offer_sequence = $last_offer_sequence
                    WHERE workspace_id = $workspace_id AND project_path = $project_path;
                    """;
                updateOffer.Parameters.AddWithValue("$last_offer_sequence", sequence);
                updateOffer.Parameters.AddWithValue("$workspace_id", workspaceId);
                updateOffer.Parameters.AddWithValue("$project_path", storedPath);
                if (updateOffer.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("coverage maintenance project offer disappeared");
            }

            claimed = new CtCoverageMaintenanceBatch(chosenPath, testCaseIds, sequence);
        });

        return claimed;
    }

    public IReadOnlyList<string> ListCtCoverageMapCandidates(string workspaceId, string projectPath, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        string normalizedProjectPath = Path.GetFullPath(projectPath);
        return WithRead<IReadOnlyList<string>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT tc.id,
                           json_extract(tc.metadata_json, '$.ct_project_path'),
                           m.project_path,
                           m.complete,
                           m.start_converged,
                           m.end_converged,
                           m.revision_at_start,
                           m.revision_at_end,
                           m.recorded_at
                    FROM test_cases tc
                    LEFT JOIN ct_coverage_maps m
                        ON m.test_case_id = tc.id AND m.workspace_id = tc.workspace_id
                    WHERE tc.workspace_id = $workspace_id
                      AND tc.source LIKE 'ct-provider:%';
                    """;
                command.Parameters.AddWithValue("$workspace_id", workspaceId);
                using SqliteDataReader reader = command.ExecuteReader();
                var candidates = new List<(string Id, int Priority, string? RecordedAt)>();
                while (reader.Read())
                {
                    if (reader.IsDBNull(1))
                        continue;

                    string currentProjectPath = reader.GetString(1);
                    if (!PathEquals(currentProjectPath, normalizedProjectPath))
                        continue;

                    bool ownedMap = !reader.IsDBNull(2) && PathEquals(reader.GetString(2), currentProjectPath);
                    bool trustedMap = ownedMap
                        && reader.GetInt64(3) != 0
                        && reader.GetInt64(4) != 0
                        && reader.GetInt64(5) != 0
                        && string.Equals(NullableString(reader, 6), NullableString(reader, 7), StringComparison.Ordinal);
                    candidates.Add((
                        reader.GetString(0),
                        ownedMap ? trustedMap ? 2 : 1 : 0,
                        ownedMap ? NullableString(reader, 8) : null));
                }

                return candidates
                    .OrderBy(candidate => candidate.Priority)
                    .ThenBy(candidate => candidate.RecordedAt, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                    .Take(limit)
                    .Select(candidate => candidate.Id)
                    .ToArray();
            });
    }

    /// <summary>
    /// Test-only fault hook, invoked inside the <see cref="ApplyRevisionAdvance"/> transaction
    /// between the staleness half and the watermark half. A throw here proves the two halves are
    /// one atomic unit: the abort must leave every case stale, never fresh.
    /// </summary>
    internal Action? RevisionAdvanceFaultInjection { get; set; }

    /// <summary>
    /// THE one production write for a revision advance: marks the impacted set stale AND advances
    /// the fresh watermarks of the keep-set (currently fresh GREEN cases the change cannot reach)
    /// from <paramref name="from"/> to <paramref name="to"/> — in a SINGLE transaction. Staleness
    /// lands before any advance, so no committed or aborted state exists in which an impacted case
    /// reads fresh at <paramref name="to"/>.
    ///
    /// <para>Outcome semantics: <see cref="ContinuousTestSelectionOutcome.Impacted"/> stales the
    /// named set and advances the rest; <see cref="ContinuousTestSelectionOutcome.KnownEmpty"/>
    /// advances every currently fresh green; <see cref="ContinuousTestSelectionOutcome.Unknown"/>
    /// and <see cref="ContinuousTestSelectionOutcome.WorkspaceScope"/> (and any future outcome)
    /// advance NOTHING — with the cursor moved and no advance, everything previously fresh reads
    /// stale at the new key. Fail closed.</para>
    /// </summary>
    public void ApplyRevisionAdvance(
        string workspaceId,
        string projectPath,
        CtFreshnessKey from,
        CtFreshnessKey to,
        IReadOnlyList<string> impactedTestCaseIds,
        ContinuousTestSelectionOutcome outcome)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));
        if (string.IsNullOrEmpty(projectPath))
            throw new ArgumentException("must not be empty", nameof(projectPath));
        ArgumentNullException.ThrowIfNull(impactedTestCaseIds);
        if (!string.Equals(from.IndexIdentity, to.IndexIdentity, StringComparison.Ordinal))
            throw new ArgumentException("advance endpoints must share an index identity", nameof(to));
        if (!CanWriteExistingFile())
            return;

        Transaction(() =>
        {
            // Staleness FIRST. It also deletes the impacted cases' watermark rows, so the advance
            // below (green-only, and blind to rows now marked stale) can never re-freshen them.
            MarkContinuousTestsStale(workspaceId, impactedTestCaseIds, to);
            RevisionAdvanceFaultInjection?.Invoke();
            if (outcome is ContinuousTestSelectionOutcome.Impacted or ContinuousTestSelectionOutcome.KnownEmpty)
                AdvanceContinuousTestFreshWatermark(workspaceId, projectPath, from, to);
        });
    }

    /// <summary>
    /// The watermark write. Private on purpose: <see cref="ApplyRevisionAdvance"/> is the only
    /// path that may advance watermarks, so staleness and advance stay one atomic unit. The keep
    /// predicate is GREEN-ONLY — a red or skipped row never rides the watermark.
    /// </summary>
    private void AdvanceContinuousTestFreshWatermark(
        string workspaceId,
        string projectPath,
        CtFreshnessKey from,
        CtFreshnessKey to)
    {
        string normalizedProjectPath = Path.GetFullPath(projectPath);
        WithWrite(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ct_case_fresh_watermarks (
                    test_case_id, workspace_id, index_identity, revision, updated_at
                )
                SELECT tc.id, tc.workspace_id, $identity, $to, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM test_cases tc
                LEFT JOIN ct_test_states s
                    ON s.test_case_id = tc.id AND s.workspace_id = tc.workspace_id
                LEFT JOIN ct_case_fresh_watermarks w
                    ON w.test_case_id = tc.id AND w.index_identity = $identity
                WHERE tc.workspace_id = $ws
                  AND tc.source LIKE 'ct-provider:%'
                  AND json_extract(tc.metadata_json, '$.ct_project_path') = $project
                  AND s.state = 'green'
                  AND (
                        (s.index_identity = $identity
                            AND s.last_run_revision IS NOT NULL
                            AND CAST(s.last_run_revision AS INTEGER) >= $from)
                        OR (w.revision IS NOT NULL AND w.revision >= $from)
                  )
                ON CONFLICT(test_case_id, index_identity) DO UPDATE SET
                    revision = max(ct_case_fresh_watermarks.revision, excluded.revision),
                    workspace_id = excluded.workspace_id,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$ws", workspaceId);
            command.Parameters.AddWithValue("$project", normalizedProjectPath);
            command.Parameters.AddWithValue("$identity", from.IndexIdentity);
            command.Parameters.AddWithValue("$from", from.Revision);
            command.Parameters.AddWithValue("$to", to.Revision);
            command.ExecuteNonQuery();
        });
    }

    public IReadOnlyList<CtFreshWatermark> ListContinuousTestFreshWatermarks(string workspaceId)
    {
        if (string.IsNullOrEmpty(workspaceId))
            throw new ArgumentException("must not be empty", nameof(workspaceId));

        return WithRead<IReadOnlyList<CtFreshWatermark>>(
            static () => [],
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT test_case_id, index_identity, revision
                    FROM ct_case_fresh_watermarks
                    WHERE workspace_id = $ws
                    ORDER BY test_case_id, index_identity;
                    """;
                command.Parameters.AddWithValue("$ws", workspaceId);
                var rows = new List<CtFreshWatermark>();
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new CtFreshWatermark(
                        TestCaseId: reader.GetString(0),
                        IndexIdentity: reader.GetString(1),
                        Revision: reader.GetInt64(2)));
                }

                return rows;
            });
    }

    public IReadOnlyDictionary<string, CtFreshnessKey> ListContinuousTestFreshWatermarks(
        string workspaceId,
        string indexIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexIdentity);
        return ListContinuousTestFreshWatermarks(workspaceId)
            .Where(row => string.Equals(row.IndexIdentity, indexIdentity, StringComparison.Ordinal))
            .ToDictionary(
                row => row.TestCaseId,
                row => new CtFreshnessKey(row.IndexIdentity, row.Revision),
                StringComparer.Ordinal);
    }

    private const string CtCoverageOwnedMapSelect = """
        SELECT m.map_id, m.workspace_id, m.test_case_id, m.project_path, m.run_id, m.generation_id,
               m.index_identity, m.revision, m.revision_at_start, m.start_converged, m.revision_at_end,
               m.end_converged, m.complete, m.failure_reason, m.granularity, m.valid_through_revision,
               m.invalidated_at_revision, m.recorded_at, m.source,
               json_extract(tc.metadata_json, '$.ct_project_path')
        FROM ct_coverage_maps m
        JOIN test_cases tc ON tc.id = m.test_case_id AND tc.workspace_id = m.workspace_id

        """;

    private static CtCoverageMapRecord ReadCtCoverageMap(SqliteDataReader reader, int offset = 0) =>
        new(
            MapId: reader.GetString(offset),
            WorkspaceId: reader.GetString(offset + 1),
            TestCaseId: reader.GetString(offset + 2),
            ProjectPath: reader.GetString(offset + 3),
            RunId: reader.GetString(offset + 4),
            GenerationId: reader.GetString(offset + 5),
            IndexIdentity: reader.GetString(offset + 6),
            Revision: reader.GetInt64(offset + 7),
            RevisionAtStart: NullableString(reader, offset + 8),
            StartConverged: reader.GetInt64(offset + 9) != 0,
            RevisionAtEnd: NullableString(reader, offset + 10),
            EndConverged: reader.GetInt64(offset + 11) != 0,
            Complete: reader.GetInt64(offset + 12) != 0,
            FailureReason: NullableString(reader, offset + 13),
            Granularity: reader.GetString(offset + 14),
            ValidThroughRevision: NullableString(reader, offset + 15),
            InvalidatedAtRevision: NullableString(reader, offset + 16),
            RecordedAt: DateTimeOffset.Parse(reader.GetString(offset + 17), CultureInfo.InvariantCulture),
            Source: reader.GetString(offset + 18));

    private IReadOnlyList<CoverageDeltaCandidate> ReadCoverageDeltaCandidates(
        string workspaceId,
        CtFreshnessKey from)
    {
        using var command = _write!.CreateCommand();
        command.CommandText = """
            SELECT m.map_id, m.workspace_id, m.test_case_id, m.project_path, m.run_id,
                   m.generation_id, m.index_identity, m.revision, m.revision_at_start,
                   m.start_converged, m.revision_at_end, m.end_converged, m.complete,
                   m.failure_reason, m.granularity, m.valid_through_revision,
                   m.invalidated_at_revision, m.recorded_at, m.source,
                   json_extract(tc.metadata_json, '$.ct_project_path'), file.file_path
            FROM ct_coverage_maps m
            JOIN test_cases tc ON tc.id = m.test_case_id AND tc.workspace_id = m.workspace_id
            LEFT JOIN ct_coverage_map_files file ON file.map_id = m.map_id
            WHERE m.workspace_id = $workspace_id
              AND m.index_identity = $identity
              AND m.valid_through_revision = $from_revision
              AND m.invalidated_at_revision IS NULL
            ORDER BY m.map_id, file.file_path;
            """;
        command.Parameters.AddWithValue("$workspace_id", workspaceId);
        command.Parameters.AddWithValue("$identity", from.IndexIdentity);
        command.Parameters.AddWithValue("$from_revision", RevisionText(from.Revision));

        using SqliteDataReader reader = command.ExecuteReader();
        var candidates = new List<CoverageDeltaCandidate>();
        CoverageDeltaCandidate? current = null;
        while (reader.Read())
        {
            CtCoverageMapRecord map = ReadCtCoverageMap(reader);
            if (reader.IsDBNull(19)
                || !PathEquals(map.ProjectPath, reader.GetString(19))
                || !IsTrustedAt(map, from))
            {
                continue;
            }

            if (current is null || !string.Equals(current.Map.MapId, map.MapId, StringComparison.Ordinal))
            {
                current = new CoverageDeltaCandidate(map, []);
                candidates.Add(current);
            }

            if (!reader.IsDBNull(20))
                current.FilePaths.Add(NormalizeCoveragePath(reader.GetString(20)));
        }

        return candidates;
    }

    private static bool PathEquals(string left, string right) =>
        PathComparer.Equals(Path.GetFullPath(left), Path.GetFullPath(right));

    private static bool IsTrustedAt(CtCoverageMapRecord map, CtFreshnessKey selected) =>
        map.Complete
        && map.StartConverged
        && map.EndConverged
        && string.Equals(map.RevisionAtStart, map.RevisionAtEnd, StringComparison.Ordinal)
        && string.Equals(map.ValidThroughRevision, RevisionText(selected.Revision), StringComparison.Ordinal)
        && map.InvalidatedAtRevision is null
        && string.Equals(map.IndexIdentity, selected.IndexIdentity, StringComparison.Ordinal);

    private static string NormalizeCoveragePath(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static string CoveragePathDigest(IReadOnlyList<string> normalizedPaths)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalizedPaths)));
        return Convert.ToHexStringLower(bytes);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static object NullableText(string? value) =>
        value is null ? DBNull.Value : value;

    private static string RevisionText(long revision) =>
        revision.ToString(CultureInfo.InvariantCulture);

    private static string StableId(string @namespace, params object?[] parts)
    {
        string normalized = string.Join("\x1f", parts.Select(PartToString));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        string hex = Convert.ToHexString(digest).ToLowerInvariant()[..24];
        return $"{@namespace}:{hex}";
    }

    private static string PartToString(object? part) =>
        part switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture) ?? "",
            _ => part.ToString() ?? "",
        };

    private sealed record CoverageDeltaCandidate(
        CtCoverageMapRecord Map,
        List<string> FilePaths);
}
