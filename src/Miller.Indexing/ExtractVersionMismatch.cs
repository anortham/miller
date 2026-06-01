using System.Globalization;

namespace Miller.Indexing;

/// <summary>
/// Shared builder for the version-mismatch message used by BOTH the post-extract cross-check in
/// <see cref="JulieExtractRunner"/> and the read-path gate <see cref="JulieSchemaGate"/>, so the runner and the
/// DB gate emit identical, actionable text for the same mismatch. julie-extract only self-rejects a DB that is
/// *newer* than its binary, so detecting an older/drifted schema, contract, or report hash algorithm is Miller's
/// job — see D5 (docs/findings/julie-contract-verified.md) and m1-indexing-design.md.
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
                   "is newer than this Miller build expects; upgrade Miller or re-pin julie-extract.";

        // Older / unexpected: name both observed versions so the operator sees the full picture.
        long contractForMsg = contractVersionForMessage ?? MillerExtractContract.ExpectedExtractContractVersion;
        return $"DB {kind} is {actual} but this Miller build expects {expected}: DB is not a " +
               $"julie-extract v{MillerExtractContract.PinnedJulieExtractVersion} artifact " +
               $"(schema {schemaVersion}, contract {contractForMsg}); re-run restore + `julie-extract scan` " +
               "with the pinned julie-extract.";
    }

    private static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Cross-check a successfully-parsed extract report against <see cref="MillerExtractContract"/>. In v1 the
    /// schema/contract/hash live in <c>report.artifact.*</c>; a null artifact block is itself a gate failure (a
    /// successful artifact-producing op must carry it). The report envelope version (<c>report_schema_version</c>)
    /// is gated too, since it frames the artifact/counts/revision shape (reconciliation #12). The product
    /// <c>tool.binary_version</c> is deliberately NOT cross-checked (D7). Throws
    /// <see cref="IncompatibleExtractException"/> with the same wording <see cref="JulieSchemaGate"/> uses.
    /// </summary>
    public static void VerifyReport(ExtractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        // The report envelope must be a v1 report. report_schema_version frames artifact/counts/revision; a
        // missing or different value means the producer's report contract changed (reports.rs:5,50). Gate it
        // alongside schema/contract/hash; keep tool.binary_version OUT of the gate (D7). (reconciliation #12)
        if (report.ReportSchemaVersion != MillerExtractContract.ExpectedReportSchemaVersion)
            throw new IncompatibleExtractException(
                $"Extract report_schema_version is '{report.ReportSchemaVersion?.ToString() ?? "(absent)"}' but this " +
                $"Miller build expects {MillerExtractContract.ExpectedReportSchemaVersion}: incompatible julie-extract " +
                "report contract. Re-run restore + `julie-extract scan` with the pinned binary.");

        // A v1 artifact-producing op MUST carry the artifact block. Its absence means the report is not a
        // julie-extract v1 artifact report — fail loud, never a silent pass.
        if (report.Artifact is not { } artifact)
            throw new IncompatibleExtractException(
                "Extract report has no artifact block; a julie-extract v1 scan/update/delete/info must carry " +
                "report.artifact (schema/contract/hash). Re-run restore + `julie-extract scan` with the pinned binary.");

        if (artifact.SqliteSchemaVersion != MillerExtractContract.ExpectedSqliteSchemaVersion)
            throw new IncompatibleExtractException(BuildMessage(
                kind: "schema",
                actual: Str(artifact.SqliteSchemaVersion),
                expected: Str(MillerExtractContract.ExpectedSqliteSchemaVersion),
                isNewer: artifact.SqliteSchemaVersion > MillerExtractContract.ExpectedSqliteSchemaVersion,
                schemaVersion: artifact.SqliteSchemaVersion,
                contractVersionForMessage: artifact.ExtractContractVersion));

        if (artifact.ExtractContractVersion != MillerExtractContract.ExpectedExtractContractVersion)
            throw new IncompatibleExtractException(BuildMessage(
                kind: "extract_contract_version",
                actual: Str(artifact.ExtractContractVersion),
                expected: Str(MillerExtractContract.ExpectedExtractContractVersion),
                isNewer: artifact.ExtractContractVersion > MillerExtractContract.ExpectedExtractContractVersion,
                schemaVersion: artifact.SqliteSchemaVersion,
                contractVersionForMessage: artifact.ExtractContractVersion));

        if (string.IsNullOrWhiteSpace(artifact.HashAlgorithm))
            throw new IncompatibleExtractException(
                "Extract report artifact is missing hash_algorithm; expected " +
                $"'{MillerExtractContract.ExpectedHashAlgorithm}'. Re-run restore + `julie-extract scan`.");

        if (!StringComparer.Ordinal.Equals(artifact.HashAlgorithm, MillerExtractContract.ExpectedHashAlgorithm))
            throw new IncompatibleExtractException(
                $"Extract report hash_algorithm is '{artifact.HashAlgorithm}' but this Miller build expects " +
                $"'{MillerExtractContract.ExpectedHashAlgorithm}': not a julie-extract v1 artifact; " +
                "re-run restore + `julie-extract scan` with the pinned binary.");
    }
}
