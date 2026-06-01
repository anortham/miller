using System.Text.Json.Serialization;

namespace Miller.Indexing;

/// <summary>
/// The nested JSON report julie-extract v1 emits on stdout (verified against
/// julie-extract-artifact/src/reports.rs). Top-level: report_schema_version, status, operation, mode,
/// input{}, artifact{}, tool{}, revision{}, counts{rows_written{},totals{}}, errors[], warnings[].
/// The flat M1/M3 accessors (Revision, SymbolsExtracted, FilesUpdated/Deleted, HashAlgorithm) are exposed
/// as computed properties over the nested model so the report-consuming services need not relearn the path;
/// the nested records stay public for callers that need mode/input/artifact directly.
/// </summary>
public sealed record ExtractReport(
    [property: JsonPropertyName("report_schema_version")] int? ReportSchemaVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("input")] ExtractReportInput? Input,
    [property: JsonPropertyName("artifact")] ExtractArtifact? Artifact,
    [property: JsonPropertyName("tool")] ExtractTool? Tool,
    [property: JsonPropertyName("revision")] ExtractRevision? RevisionBlock,
    [property: JsonPropertyName("counts")] ExtractCounts? Counts,
    [property: JsonPropertyName("errors")] IReadOnlyList<ReportDiagnostic> Errors,
    [property: JsonPropertyName("warnings")] IReadOnlyList<ReportDiagnostic> Warnings)
{
    /// <summary>The freshness cursor: revision.latest_revision_id (present after any scan; null when absent).</summary>
    [JsonIgnore] public long? Revision => RevisionBlock?.LatestRevisionId;

    /// <summary>revision.created_revision_id — NULL on a no-op; signals whether THIS call mutated. Never the cursor.</summary>
    [JsonIgnore] public long? CreatedRevision => RevisionBlock?.CreatedRevisionId;

    /// <summary>artifact.hash_algorithm; null when the artifact block is absent (a failed op).</summary>
    [JsonIgnore] public string? HashAlgorithm => Artifact?.HashAlgorithm;

    // Transitional: v1 reports carry no workspace_id; the echo cross-checks in WorkspaceTool/CrossWorkspaceRefreshService
    // go inert (null can never mismatch) until E3/E4 remove them (Phase 4).
    [JsonIgnore] public string? WorkspaceId => null;

    [JsonIgnore] public ulong FilesScanned => ToU(Counts?.FilesScanned);
    [JsonIgnore] public ulong FilesUpdated => ToU(Counts?.FilesChanged);   // v1 calls it files_changed
    [JsonIgnore] public ulong FilesDeleted => ToU(Counts?.FilesDeleted);
    [JsonIgnore] public ulong SymbolsExtracted => ToU(Counts?.RowsWritten?.Symbols);
    [JsonIgnore] public ulong FilesTotal => ToU(Counts?.Totals?.Files);
    [JsonIgnore] public ulong SymbolsTotal => ToU(Counts?.Totals?.Symbols);
    [JsonIgnore] public ulong RelationshipsTotal => ToU(Counts?.Totals?.Relationships);
    [JsonIgnore] public ulong IdentifiersTotal => ToU(Counts?.Totals?.Identifiers);

    // julie emits signed counts (i64) that are non-negative in practice; clamp the rare negative to 0.
    private static ulong ToU(long? v) => v is { } n && n > 0 ? (ulong)n : 0UL;
}

public sealed record ExtractReportInput(
    [property: JsonPropertyName("db_path")] string? DbPath,
    [property: JsonPropertyName("root_path")] string? RootPath,
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("root_relative_path")] string? RootRelativePath,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("output_path")] string? OutputPath);

public sealed record ExtractArtifact(
    [property: JsonPropertyName("db_path")] string DbPath,
    [property: JsonPropertyName("root_path")] string RootPath,
    [property: JsonPropertyName("artifact_id")] string ArtifactId,
    [property: JsonPropertyName("schema_version")] long SchemaVersion,
    [property: JsonPropertyName("extract_contract_version")] long ExtractContractVersion,
    [property: JsonPropertyName("sqlite_schema_version")] long SqliteSchemaVersion,
    [property: JsonPropertyName("jsonl_schema_version")] long? JsonlSchemaVersion,
    [property: JsonPropertyName("hash_algorithm")] string HashAlgorithm,
    [property: JsonPropertyName("parser_inventory_fingerprint")] string? ParserInventoryFingerprint,
    [property: JsonPropertyName("capability_snapshot_fingerprint")] string? CapabilitySnapshotFingerprint);

public sealed record ExtractTool(
    [property: JsonPropertyName("binary_name")] string BinaryName,
    [property: JsonPropertyName("binary_version")] string BinaryVersion);

public sealed record ExtractRevision(
    [property: JsonPropertyName("latest_revision_id")] long? LatestRevisionId,
    [property: JsonPropertyName("created_revision_id")] long? CreatedRevisionId);

public sealed record ExtractCounts(
    [property: JsonPropertyName("files_scanned")] long FilesScanned,
    [property: JsonPropertyName("files_changed")] long FilesChanged,
    [property: JsonPropertyName("files_unchanged")] long FilesUnchanged,
    [property: JsonPropertyName("files_unsupported")] long FilesUnsupported,
    [property: JsonPropertyName("files_deleted")] long FilesDeleted,
    [property: JsonPropertyName("files_failed")] long FilesFailed,
    [property: JsonPropertyName("rows_written")] ExtractRowCounts? RowsWritten,
    [property: JsonPropertyName("totals")] ExtractRowCounts? Totals);

/// <summary>The 18 v1 row domains (reports.rs RowDomainCounts); Miller reads a handful, the rest deserialize for completeness.</summary>
public sealed record ExtractRowCounts(
    [property: JsonPropertyName("files")] long? Files,
    [property: JsonPropertyName("symbols")] long? Symbols,
    [property: JsonPropertyName("symbol_annotations")] long? SymbolAnnotations,
    [property: JsonPropertyName("identifiers")] long? Identifiers,
    [property: JsonPropertyName("relationships")] long? Relationships,
    [property: JsonPropertyName("type_arguments")] long? TypeArguments,
    [property: JsonPropertyName("type_argument_usages")] long? TypeArgumentUsages,
    [property: JsonPropertyName("literals")] long? Literals,
    [property: JsonPropertyName("extraction_revisions")] long? ExtractionRevisions,
    [property: JsonPropertyName("revision_file_changes")] long? RevisionFileChanges);

/// <summary>One julie-extract diagnostic (reports.rs ReportDiagnostic). `code` is a snake_case string.</summary>
public sealed record ReportDiagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("root_relative_path")] string? RootRelativePath,
    [property: JsonPropertyName("recoverable")] bool Recoverable,
    [property: JsonPropertyName("details")] System.Text.Json.JsonElement Details = default);
