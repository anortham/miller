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
    // julie-extract v3: sqlite_schema_version 3 / extract_contract_version 3 / report_schema_version 3.
    // v3 adds parser-backed structural_facts and complexity_metrics, while keeping symbol body_hash as the
    // clone-ready normalized body fingerprint.
    public const long ExpectedSchemaVersion = 3;
    public const long ExpectedSqliteSchemaVersion = 3;
    public const long ExpectedExtractContractVersion = 3;
    public const long ExpectedReportSchemaVersion = 3;
    public const string ExpectedHashAlgorithm = "blake3";

    // Download pin only (restore-script + julie-pins.json target). This is the PRODUCT version,
    // orthogonal to the runtime schema/contract gate above (D7): product 2.5.x ships schema/contract 3.
    public const string PinnedJulieExtractVersion = "2.5.9"; // julie-extractors release tag v2.5.9 (published 2026-07-01).
}
