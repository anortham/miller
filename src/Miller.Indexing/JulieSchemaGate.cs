using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// The D5 compatibility gate. Runs on an open read-only connection BEFORE any read to confirm the DB is a
/// compatible julie-extract artifact (sqlite schema <see cref="MillerExtractContract.ExpectedSchemaVersion"/>,
/// contract <see cref="MillerExtractContract.ExpectedExtractContractVersion"/>, and hash algorithm
/// <see cref="MillerExtractContract.ExpectedHashAlgorithm"/>). Current julie-extract artifacts carry all of these as keys in the single
/// <c>artifact_metadata</c> table; a missing table fails fast on a non-julie / corrupt DB. Throws
/// <see cref="IncompatibleExtractException"/> with an actionable message on any mismatch.
/// </summary>
internal static class JulieSchemaGate
{
    // SQLite "no such table: X" maps to SqliteErrorCode 1 (SQLITE_ERROR) — distinguished by message text.
    private const int SqliteGenericError = 1;

    /// <summary>
    /// Verify the DB on <paramref name="connection"/> is a compatible julie-extract artifact. The connection
    /// must be open. Throws <see cref="IncompatibleExtractException"/> if the schema/contract/hash contract
    /// differs or a required table/key is missing.
    /// </summary>
    public static void Verify(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        long schemaVersion = ReadSchemaVersion(connection);
        if (schemaVersion != MillerExtractContract.ExpectedSchemaVersion)
            throw new IncompatibleExtractException(ExtractVersionMismatch.BuildMessage(
                kind: "schema",
                actual: schemaVersion.ToString(CultureInfo.InvariantCulture),
                expected: MillerExtractContract.ExpectedSchemaVersion.ToString(CultureInfo.InvariantCulture),
                isNewer: schemaVersion > MillerExtractContract.ExpectedSchemaVersion,
                schemaVersion,
                contractVersionForMessage: null));

        long contractVersion = ReadContractVersion(connection);
        if (contractVersion != MillerExtractContract.ExpectedExtractContractVersion)
            throw new IncompatibleExtractException(ExtractVersionMismatch.BuildMessage(
                kind: "extract_contract_version",
                actual: contractVersion.ToString(CultureInfo.InvariantCulture),
                expected: MillerExtractContract.ExpectedExtractContractVersion.ToString(CultureInfo.InvariantCulture),
                isNewer: contractVersion > MillerExtractContract.ExpectedExtractContractVersion,
                schemaVersion,
                contractVersionForMessage: contractVersion));

        string hashAlgorithm = ReadRequiredMetadataValue(
            connection, "hash_algorithm", $"'{MillerExtractContract.ExpectedHashAlgorithm}'");
        if (!StringComparer.Ordinal.Equals(hashAlgorithm, MillerExtractContract.ExpectedHashAlgorithm))
            throw new IncompatibleExtractException(
                $"DB has hash_algorithm value '{hashAlgorithm}', expected '{MillerExtractContract.ExpectedHashAlgorithm}'; " +
                $"it is not a julie-extract artifact compatible with this Miller build. Re-run restore + `scan` " +
                $"with the pinned julie-extract (v{MillerExtractContract.PinnedJulieExtractVersion}).");
    }

    // Current julie-extract artifacts store the schema version as a metadata KEY (sqlite_schema_version), not a
    // schema_version-table MAX. A non-integer value → typed error naming it.
    private static long ReadSchemaVersion(SqliteConnection connection)
    {
        string text = ReadRequiredMetadataValue(
            connection, "sqlite_schema_version",
            MillerExtractContract.ExpectedSqliteSchemaVersion.ToString(CultureInfo.InvariantCulture));
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            throw new IncompatibleExtractException(
                $"DB has a non-integer sqlite_schema_version value '{text}'; it is not a valid julie-extract artifact.");
        return value;
    }

    // All metadata values are stored as TEXT. A missing row
    // (no result) is incompatible (older / corrupt extract), reported against the key name.
    private static long ReadContractVersion(SqliteConnection connection)
    {
        string text = ReadRequiredMetadataValue(
            connection,
            "extract_contract_version",
            MillerExtractContract.ExpectedExtractContractVersion.ToString(CultureInfo.InvariantCulture));
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            throw new IncompatibleExtractException(
                $"DB has a non-integer extract_contract_version value '{text}'; it is not a valid " +
                $"julie-extract artifact.");

        return value;
    }

    private static string ReadRequiredMetadataValue(SqliteConnection connection, string key, string expected)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM artifact_metadata WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        object? result;
        try
        {
            result = cmd.ExecuteScalar();
        }
        catch (SqliteException ex) when (IsMissingTable(ex, "artifact_metadata"))
        {
            throw MissingTable("artifact_metadata", ex);
        }

        if (result is null || result is DBNull)
            throw new IncompatibleExtractException(
                $"DB is missing the '{key}' key in artifact_metadata; expected {expected} " +
                $"metadata from a compatible julie-extract artifact. Re-run restore + `scan` " +
                $"with the pinned julie-extract (v{MillerExtractContract.PinnedJulieExtractVersion}).");

        return result.ToString() ?? string.Empty;
    }

    private static bool IsMissingTable(SqliteException ex, string table) =>
        ex.SqliteErrorCode == SqliteGenericError &&
        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) &&
        ex.Message.Contains(table, StringComparison.Ordinal);

    private static IncompatibleExtractException MissingTable(string table, SqliteException inner) =>
        new($"DB has no '{table}' table; it is not a compatible julie-extract artifact. " +
            $"Re-run restore + `scan` with the pinned julie-extract.", inner);
}
