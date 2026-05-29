using System.Globalization;

namespace Miller.Indexing;

/// <summary>
/// Shared builder for the version-mismatch message used by BOTH the post-extract cross-check in
/// <see cref="JulieExtractRunner"/> and the read-path gate <see cref="JulieSchemaGate"/>, so the runner and the
/// DB gate emit identical, actionable text for the same mismatch. julie only self-rejects a DB that is *newer*
/// than its binary, so detecting an older/drifted schema or contract is Miller's job — see D5
/// (docs/findings/julie-contract-verified.md) and m1-indexing-design.md.
/// </summary>
internal static class ExtractVersionMismatch
{
    /// <summary>
    /// Build the actionable mismatch message naming the offending value and the remedy.
    /// </summary>
    /// <param name="kind">"schema" or "extract_contract_version".</param>
    /// <param name="actual">The value observed in the DB / extract report.</param>
    /// <param name="expected">The value this Miller build was built against.</param>
    /// <param name="isNewer">True if the observed value is newer than expected (→ upgrade Miller path).</param>
    /// <param name="schemaVersion">The observed schema version, for the full-picture older-path message.</param>
    /// <param name="contractVersionForMessage">
    /// The observed contract version for the older-path message; null falls back to the expected contract.
    /// </param>
    public static string BuildMessage(
        string kind, string actual, string expected, bool isNewer,
        long schemaVersion, long? contractVersionForMessage)
    {
        if (isNewer)
            return $"DB {kind} is {actual} but this Miller build expects {expected}: the DB schema/contract " +
                   "is newer than this Miller build expects; upgrade Miller or re-pin julie-server.";

        // Older / unexpected: name both observed versions so the operator sees the full picture.
        long contractForMsg = contractVersionForMessage ?? MillerExtractContract.ExpectedExtractContractVersion;
        return $"DB {kind} is {actual} but this Miller build expects {expected}: DB is not a " +
               $"v{MillerExtractContract.PinnedJulieServerVersion} julie extract " +
               $"(schema {schemaVersion}, contract {contractForMsg}); re-run restore + `extract scan` " +
               "with the pinned julie-server.";
    }

    private static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Cross-check a successfully-parsed extract report's schema/contract versions against
    /// <see cref="MillerExtractContract"/>. julie only self-rejects a *newer* DB, so an older/drifted DB that
    /// julie tolerated must be caught here. Throws <see cref="IncompatibleExtractException"/> with the same
    /// wording <see cref="JulieSchemaGate"/> uses. Null versions (julie omitted them) are not cross-checked
    /// here — the read-path gate enforces presence before any read.
    /// </summary>
    public static void VerifyReport(ExtractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.SchemaVersion is int schema && schema != MillerExtractContract.ExpectedSchemaVersion)
            throw new IncompatibleExtractException(BuildMessage(
                kind: "schema",
                actual: Str(schema),
                expected: Str(MillerExtractContract.ExpectedSchemaVersion),
                isNewer: schema > MillerExtractContract.ExpectedSchemaVersion,
                schemaVersion: schema,
                contractVersionForMessage: report.ExtractContractVersion));

        if (report.ExtractContractVersion is int contract &&
            contract != MillerExtractContract.ExpectedExtractContractVersion)
            throw new IncompatibleExtractException(BuildMessage(
                kind: "extract_contract_version",
                actual: Str(contract),
                expected: Str(MillerExtractContract.ExpectedExtractContractVersion),
                isNewer: contract > MillerExtractContract.ExpectedExtractContractVersion,
                schemaVersion: report.SchemaVersion ?? 0,
                contractVersionForMessage: contract));
    }
}
