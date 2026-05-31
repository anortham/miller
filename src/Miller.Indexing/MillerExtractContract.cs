namespace Miller.Indexing;

/// <summary>
/// The single source of truth for the julie extract versions Miller is built against. Centralized so the
/// M4 bump (→ schema 28 / contract 2 when the bridge-anchor extraction enrichment is consumed) is a
/// one-line change. Both <see cref="JulieSchemaGate"/> (reading the DB) and <see cref="JulieExtractRunner"/>
/// (cross-checking the extract report) gate on these same constants.
/// </summary>
internal static class MillerExtractContract
{
    // Miller pins julie-server v7.13.0 → schema 28 / extract_contract_version 2.
    // Bumped to (28, 2) at M4 to consume the bridge-anchor extraction enrichment (type_arguments / literals).
    public const long ExpectedSchemaVersion = 28;
    public const long ExpectedExtractContractVersion = 2;
    public const string PinnedJulieServerVersion = "7.13.0";
}
