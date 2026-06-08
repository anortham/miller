namespace Miller.Indexing;

/// <summary>
/// Public read-only view of the extract contract Miller was built against. The schema gate keeps using
/// <see cref="MillerExtractContract"/> internally; CLI and integration surfaces use this DTO so downstream
/// consumers do not depend on internal gate implementation details.
/// </summary>
public sealed record MillerContractFacts(
    string PinnedJulieExtractVersion,
    long SchemaVersion,
    long SqliteSchemaVersion,
    long ExtractContractVersion,
    long ReportSchemaVersion,
    string HashAlgorithm)
{
    public static MillerContractFacts Current { get; } = new(
        MillerExtractContract.PinnedJulieExtractVersion,
        MillerExtractContract.ExpectedSchemaVersion,
        MillerExtractContract.ExpectedSqliteSchemaVersion,
        MillerExtractContract.ExpectedExtractContractVersion,
        MillerExtractContract.ExpectedReportSchemaVersion,
        MillerExtractContract.ExpectedHashAlgorithm);
}
