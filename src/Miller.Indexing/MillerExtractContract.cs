namespace Miller.Indexing;

/// <summary>
/// The single source of truth for the julie extract versions Miller is built against. Centralized so the
/// M4 bump (→ schema 28 / contract 2 when the bridge-anchor extraction enrichment is consumed) is a
/// one-line change. Both <see cref="JulieSchemaGate"/> (reading the DB) and <see cref="JulieExtractRunner"/>
/// (cross-checking the extract report) gate on these same constants.
/// </summary>
internal static class MillerExtractContract
{
    // Miller pins julie-server v7.12.2 → schema 26 / extract_contract_version 1.
    // Bumps to (28, 2) at M4 when the bridge-anchor extraction enrichment is consumed.
    public const long ExpectedSchemaVersion = 26;
    public const long ExpectedExtractContractVersion = 1;
    public const string PinnedJulieServerVersion = "7.12.2";
}
