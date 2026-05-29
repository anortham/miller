using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// The D5 compatibility gate. Runs on an open read-only connection BEFORE any read to confirm the DB is a
/// compatible julie extract (schema <see cref="MillerExtractContract.ExpectedSchemaVersion"/> /
/// contract <see cref="MillerExtractContract.ExpectedExtractContractVersion"/>). Gating on the tables'
/// existence also fails fast on a non-julie / corrupt DB. Throws <see cref="IncompatibleExtractException"/>
/// with an actionable message on any mismatch.
/// </summary>
internal static class JulieSchemaGate
{
    // SQLite "no such table: X" maps to SqliteErrorCode 1 (SQLITE_ERROR) — distinguished by message text.
    private const int SqliteGenericError = 1;

    /// <summary>
    /// Verify the DB on <paramref name="connection"/> is a compatible julie extract. The connection must be
    /// open. Throws <see cref="IncompatibleExtractException"/> if the schema/contract version differs or a
    /// required table/key is missing.
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
    }

    // julie's own liveness query. COALESCE handles an empty (but present) schema_version table.
    private static long ReadSchemaVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        try
        {
            object? result = cmd.ExecuteScalar();
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex) when (IsMissingTable(ex, "schema_version"))
        {
            throw MissingTable("schema_version", ex);
        }
    }

    // All metadata values are stored as TEXT; the contract value is the string '1' today. A missing row
    // (no result) is incompatible (older / corrupt extract), reported against the key name.
    private static long ReadContractVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT value FROM external_extract_metadata WHERE key = 'extract_contract_version';";
        object? result;
        try
        {
            result = cmd.ExecuteScalar();
        }
        catch (SqliteException ex) when (IsMissingTable(ex, "external_extract_metadata"))
        {
            throw MissingTable("external_extract_metadata", ex);
        }

        if (result is null || result is DBNull)
            throw new IncompatibleExtractException(
                "DB is missing the 'extract_contract_version' key in external_extract_metadata; it is not a " +
                $"v7.12.2 julie extract. Re-run restore + `extract scan` with the pinned julie-server " +
                $"(v{MillerExtractContract.PinnedJulieServerVersion}).");

        string text = result.ToString() ?? string.Empty;
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            throw new IncompatibleExtractException(
                $"DB has a non-integer extract_contract_version value '{text}'; it is not a valid " +
                $"v{MillerExtractContract.PinnedJulieServerVersion} julie extract.");

        return value;
    }

    private static bool IsMissingTable(SqliteException ex, string table) =>
        ex.SqliteErrorCode == SqliteGenericError &&
        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) &&
        ex.Message.Contains(table, StringComparison.Ordinal);

    private static IncompatibleExtractException MissingTable(string table, SqliteException inner) =>
        new($"DB has no '{table}' table; it is not a v{MillerExtractContract.PinnedJulieServerVersion} julie " +
            $"extract. Re-run restore + `extract scan` with the pinned julie-server.", inner);
}
