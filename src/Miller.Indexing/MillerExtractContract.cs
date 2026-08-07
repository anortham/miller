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
    public const long ExpectedSchemaVersion = 5;
    public const long ExpectedSqliteSchemaVersion = 5;
    public const long ExpectedExtractContractVersion = 4;
    public const long ExpectedReportSchemaVersion = 3;
    public const long ExpectedJsonlSchemaVersion = 4;
    public const string ExpectedHashAlgorithm = "blake3";

    public const string PinnedJulieExtractVersion = "2.28.0";
}
