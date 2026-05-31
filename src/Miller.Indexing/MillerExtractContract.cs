namespace Miller.Indexing;

/// <summary>
/// The single source of truth for the julie extract versions Miller is built against. Centralized so the
/// workspace-registry bump (→ schema 28 / contract 3 with hash_algorithm metadata) is a
/// one-line change. Both <see cref="JulieSchemaGate"/> (reading the DB) and <see cref="JulieExtractRunner"/>
/// (cross-checking the extract report) gate on these same constants.
/// </summary>
internal static class MillerExtractContract
{
    // Miller pins julie-server v7.13.1 → schema 28 / extract_contract_version 3.
    // Contract 3 adds external_extract_metadata.hash_algorithm without a schema migration.
    public const long ExpectedSchemaVersion = 28;
    public const long ExpectedExtractContractVersion = 3;
    public const string ExpectedHashAlgorithm = "blake3";
    public const string PinnedJulieServerVersion = "7.13.1";
}
