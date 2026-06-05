namespace Miller.Indexing;

/// <summary>
/// The single source of truth for the julie-extract versions Miller is built against. Both
/// <see cref="JulieSchemaGate"/> (reading the DB's artifact_metadata) and <see cref="ExtractVersionMismatch"/>
/// (cross-checking the extract report's artifact block) gate on these constants. The runtime gate is the
/// schema/contract versions, NOT the product binary_version (D7 — product version and schema/contract version
/// are orthogonal: julie-extract 2.1.1 ships schema/contract 2, and a future product bump that keeps the
/// contract must not break Miller); <see cref="PinnedJulieExtractVersion"/> is the download pin only.
/// </summary>
internal static class MillerExtractContract
{
    // julie-extract v2: sqlite_schema_version 2 / extract_contract_version 2 / report_schema_version 2.
    // schema_version and sqlite_schema_version are both 2 in v2 (schema.rs). v2 adds the source_regions
    // table; every table Miller reads is otherwise unchanged from v1.
    public const long ExpectedSchemaVersion = 2;
    public const long ExpectedSqliteSchemaVersion = 2;
    public const long ExpectedExtractContractVersion = 2;
    public const long ExpectedReportSchemaVersion = 2;
    public const string ExpectedHashAlgorithm = "blake3";

    // Download pin only (restore-script + julie-pins.json target). This is the PRODUCT version,
    // orthogonal to the runtime schema/contract gate above (D7): product 2.1.1 ships schema/contract 2.
    public const string PinnedJulieExtractVersion = "2.1.1"; // julie-extractors release tag v2.1.1 (published 2026-06-05).
}
