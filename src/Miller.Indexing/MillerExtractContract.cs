namespace Miller.Indexing;

/// <summary>
/// The single source of truth for the julie-extract versions Miller is built against. Both
/// <see cref="JulieSchemaGate"/> (reading the DB's artifact_metadata) and <see cref="ExtractVersionMismatch"/>
/// (cross-checking the extract report's artifact block) gate on these constants. The runtime gate is the
/// schema/contract versions, NOT the product binary_version (D7 — product version and schema/contract version
/// are orthogonal: julie-extract 2.2.x ships schema/contract 3, and a future product bump that keeps the
/// contract must not break Miller); <see cref="PinnedJulieExtractVersion"/> is the download pin only.
/// </summary>
internal static class MillerExtractContract
{
    // julie-extract schema v4 (product 2.9.0): sqlite_schema_version 4 / extract_contract_version 3 /
    // report_schema_version 3. v4 adds workspace reference resolution: pending_resolutions and
    // identifier_resolutions overlay tables, an FK-consistent identifiers.target_symbol_id, and the
    // artifact_metadata key reference_resolution_version (currently 1). The extract contract and report
    // schema are unchanged from v3.
    public const long ExpectedSchemaVersion = 4;
    public const long ExpectedSqliteSchemaVersion = 4;
    public const long ExpectedExtractContractVersion = 3;
    public const long ExpectedReportSchemaVersion = 3;
    public const string ExpectedHashAlgorithm = "blake3";

    // Download pin only (restore-script + julie-pins.json target). This is the PRODUCT version,
    // orthogonal to the runtime schema/contract gate above (D7): product 2.9.x through 2.13.x ships schema 4 / contract 3.
    public const string PinnedJulieExtractVersion = "2.13.0"; // julie-extractors v2.13.0; schema and contract versions are unchanged.
}
