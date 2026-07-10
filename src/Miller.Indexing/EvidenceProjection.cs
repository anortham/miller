using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// SQL fragments for the test-role/currency evidence columns, tolerant of gate-passing artifacts that
/// predate them. The schema gate checks artifact_metadata versions, never table/column presence, so a
/// few focused/large test artifacts (and older 2.9–2.11 extracts) legitimately omit the optional
/// evidence tables or the newly additive role columns. Every artifact reader that selects evidence
/// (<see cref="SqliteSymbolReader"/>, <see cref="SymbolExportReader"/>) must build its query through
/// this projection so such artifacts degrade to unknown/defaulted evidence instead of throwing
/// SqliteException mid-read; released current artifacts have every source below.
/// </summary>
internal readonly record struct EvidenceProjection(
    string DiagnosticPathsCte,
    string FilesJoin,
    string DiagnosticsJoin,
    string TestContainer,
    string TestLifecycle,
    string FileStatus,
    string HasFileEvidence,
    string HasParseDiagnostics)
{
    public static EvidenceProjection From(SqliteConnection connection)
    {
        bool hasFiles = TableExists(connection, "files");
        bool hasDiagnostics = TableExists(connection, "parse_diagnostics");
        return new EvidenceProjection(
            DiagnosticPathsCte: hasDiagnostics
                ? """
                  WITH diagnostic_paths AS (
                      SELECT path, 1 AS has_parse_diagnostics
                      FROM parse_diagnostics
                      GROUP BY path
                  )
                  """
                : string.Empty,
            FilesJoin: hasFiles ? "LEFT JOIN files AS f ON f.path = s.path" : string.Empty,
            DiagnosticsJoin: hasDiagnostics ? "LEFT JOIN diagnostic_paths AS d ON d.path = s.path" : string.Empty,
            TestContainer: ColumnExists(connection, "symbols", "test_container") ? "s.test_container" : "0",
            TestLifecycle: ColumnExists(connection, "symbols", "test_lifecycle") ? "s.test_lifecycle" : "0",
            FileStatus: hasFiles ? "f.status" : "NULL",
            HasFileEvidence: hasFiles ? "CASE WHEN f.path IS NULL THEN 0 ELSE 1 END" : "0",
            HasParseDiagnostics: hasDiagnostics ? "COALESCE(d.has_parse_diagnostics, 0)" : "0");
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", column);
        return command.ExecuteScalar() is not null;
    }
}
