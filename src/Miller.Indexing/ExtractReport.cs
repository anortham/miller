using System.Text.Json.Serialization;

namespace Miller.Indexing;

/// <summary>
/// The flat JSON report julie-server emits on stdout for an <c>extract</c> operation (verified against
/// julie v7.13.1's <c>report.rs</c>). <c>info</c> reuses the same shape: its counts arrive in the
/// <c>*_total</c> fields, not the scan counters. Note serde renames the Rust field <c>db</c> to
/// <c>db_path</c> in the JSON, captured by the <see cref="JsonPropertyNameAttribute"/> on
/// <see cref="DbPath"/>. Counts are unsigned (julie emits them as non-negative).
/// </summary>
public sealed record ExtractReport(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("db_path")] string DbPath,                          // serde renames `db`→`db_path`
    [property: JsonPropertyName("root")] string? Root,
    [property: JsonPropertyName("schema_version")] int? SchemaVersion,              // expect MillerExtractContract.ExpectedSchemaVersion (28)
    [property: JsonPropertyName("schema_state")] string? SchemaState,               // missing|older|current|newer
    [property: JsonPropertyName("extract_contract_version")] int? ExtractContractVersion, // expect MillerExtractContract.ExpectedExtractContractVersion (3)
    [property: JsonPropertyName("analysis_state")] string? AnalysisState,
    [property: JsonPropertyName("files_scanned")] ulong FilesScanned,
    [property: JsonPropertyName("symbols_extracted")] ulong SymbolsExtracted,
    [property: JsonPropertyName("files_total")] ulong FilesTotal,                   // info counts land here
    [property: JsonPropertyName("symbols_total")] ulong SymbolsTotal,
    [property: JsonPropertyName("relationships_total")] ulong RelationshipsTotal,
    [property: JsonPropertyName("identifiers_total")] ulong IdentifiersTotal,
    [property: JsonPropertyName("types_total")] ulong TypesTotal,
    [property: JsonPropertyName("errors")] IReadOnlyList<ExtractError> Errors,
    // ----- M3 freshness fields (verified-fact 7); absent on scan/info reports → null/0 defaults. -----
    [property: JsonPropertyName("workspace_id")] string? WorkspaceId = null,        // the poll's WHERE-clause id
    [property: JsonPropertyName("revision")] long? Revision = null,                 // the canonical_revisions cursor
    [property: JsonPropertyName("files_updated")] ulong FilesUpdated = 0,           // `update` outcome counter
    [property: JsonPropertyName("files_deleted")] ulong FilesDeleted = 0,           // `delete` outcome counter
    [property: JsonPropertyName("hash_algorithm")] string? HashAlgorithm = null);   // expect MillerExtractContract.ExpectedHashAlgorithm

/// <summary>One entry in an <see cref="ExtractReport.Errors"/> array (julie's per-operation error record).</summary>
public sealed record ExtractError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("path")] string? Path);
