using Microsoft.Data.Sqlite;

namespace Miller.Indexing;

/// <summary>
/// The D4 read layer. Opens a julie extract DB <c>Mode=ReadOnly</c>, runs <see cref="JulieSchemaGate"/>,
/// and projects each <c>symbols</c> row to an <see cref="IndexedSymbol"/> with a deterministic 0-based
/// <see cref="IndexedSymbol.DocId"/> ordinal.
///
/// WAL trap (D4): a <c>Mode=ReadOnly</c> reader of a WAL DB still needs to write the wal-index sidecar into
/// the DB's directory. A read-only directory makes Open()/first read throw SQLITE_READONLY (error code 8)
/// mid-stream. We do NOT default to <c>immutable=1</c> (it silently drops uncheckpointed -wal rows under a
/// live julie writer). Instead the reader probes the directory's writability up front and surfaces a clear
/// <see cref="InvalidOperationException"/> — Miller controls these directories, so a non-writable one is a
/// configuration error, not a runtime surprise.
///
/// Sync by design: this is a single startup pass and Microsoft.Data.Sqlite's async is synchronous internally.
/// </summary>
public static class SqliteSymbolReader
{
    private const int ParameterChunkSize = 500;

    /// <summary>
    /// Read all named symbols from the julie extract at <paramref name="dbPath"/> into a deterministically
    /// ordered list. DocId is the 0-based ordinal of the SELECT order (path, start_line, symbol_id).
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    /// <exception cref="IncompatibleExtractException">The DB is not a compatible v1 julie-extract artifact.</exception>
    public static IReadOnlyList<IndexedSymbol> Read(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        // Shared D4 read discipline (file-exists + writable-dir probe + Mode=ReadOnly + SQLITE_READONLY map).
        using var connection = SqliteReadOnlyAccess.Open(dbPath);

        JulieSchemaGate.Verify(connection);

        EvidenceProjection evidence = EvidenceProjection.From(connection);

        using var command = connection.CreateCommand();
        // v1 columns. By-name reads (D6) decouple SELECT order from the GetX ordinals: a future column
        // add/reorder can never silently shift a value into the wrong field again.
        command.CommandText = $"""
            {evidence.DiagnosticPathsCte}
            SELECT ROW_NUMBER() OVER (ORDER BY s.path, s.start_line, s.symbol_id) - 1 AS doc_id,
                   s.symbol_id, s.name, s.signature, s.kind, s.language, s.path,
                   s.start_line, s.end_line, s.parent_symbol_id, s.is_test,
                   {evidence.TestContainer} AS test_container,
                   {evidence.TestLifecycle} AS test_lifecycle,
                   {evidence.FileStatus} AS file_status,
                   {evidence.HasFileEvidence} AS has_file_evidence,
                   {evidence.HasParseDiagnostics} AS has_parse_diagnostics
            FROM symbols AS s
            {evidence.FilesJoin}
            {evidence.DiagnosticsJoin}
            WHERE s.name IS NOT NULL
            ORDER BY s.path, s.start_line, s.symbol_id;
            """;

        var results = new List<IndexedSymbol>();
        using var reader = command.ExecuteReader();
        ReadRows(reader, results);
        return results;
    }

    /// <summary>
    /// Read only named symbols in the supplied workspace-relative <paramref name="paths"/>. DocId is still the
    /// global deterministic row ordinal from the full reader's order, but callers that maintain their own stable
    /// sidecar identities may rewrite it before indexing.
    /// </summary>
    public static IReadOnlyList<IndexedSymbol> ReadForPaths(string dbPath, IReadOnlyCollection<string> paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(paths);

        var distinctPaths = paths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (distinctPaths.Length == 0)
            return Array.Empty<IndexedSymbol>();

        using var connection = SqliteReadOnlyAccess.Open(dbPath);
        JulieSchemaGate.Verify(connection);

        EvidenceProjection evidence = EvidenceProjection.From(connection);
        string orderedCtePrefix = evidence.DiagnosticsJoin.Length == 0
            ? "WITH"
            : evidence.DiagnosticPathsCte + ",";

        var results = new List<IndexedSymbol>();
        for (int offset = 0; offset < distinctPaths.Length; offset += ParameterChunkSize)
        {
            int count = Math.Min(ParameterChunkSize, distinctPaths.Length - offset);
            using var command = connection.CreateCommand();
            string placeholders = AddPathParameters(command, distinctPaths, offset, count);
            command.CommandText = $"""
                {orderedCtePrefix}
                ordered AS (
                    SELECT ROW_NUMBER() OVER (ORDER BY s.path, s.start_line, s.symbol_id) - 1 AS doc_id,
                           s.symbol_id, s.name, s.signature, s.kind, s.language, s.path,
                           s.start_line, s.end_line, s.parent_symbol_id, s.is_test,
                           {evidence.TestContainer} AS test_container,
                           {evidence.TestLifecycle} AS test_lifecycle,
                           {evidence.FileStatus} AS file_status,
                           {evidence.HasFileEvidence} AS has_file_evidence,
                           {evidence.HasParseDiagnostics} AS has_parse_diagnostics
                    FROM symbols AS s
                    {evidence.FilesJoin}
                    {evidence.DiagnosticsJoin}
                    WHERE s.name IS NOT NULL
                )
                SELECT doc_id, symbol_id, name, signature, kind, language, path,
                       start_line, end_line, parent_symbol_id, is_test,
                       test_container, test_lifecycle, file_status,
                       has_file_evidence, has_parse_diagnostics
                FROM ordered
                WHERE path IN ({placeholders})
                ORDER BY path, start_line, symbol_id;
                """;
            using var reader = command.ExecuteReader();
            ReadRows(reader, results);
        }

        results.Sort(static (a, b) => a.DocId.CompareTo(b.DocId));
        return results;
    }

    private static void ReadRows(SqliteDataReader reader, List<IndexedSymbol> results)
    {
        // Resolve ordinals once (cheap, cached) — not per-row over ~565k startup rows.
        int oDocId = reader.GetOrdinal("doc_id");
        int oSymbolId = reader.GetOrdinal("symbol_id");
        int oName = reader.GetOrdinal("name");
        int oSignature = reader.GetOrdinal("signature");
        int oKind = reader.GetOrdinal("kind");
        int oLanguage = reader.GetOrdinal("language");
        int oPath = reader.GetOrdinal("path");
        int oStartLine = reader.GetOrdinal("start_line");
        int oEndLine = reader.GetOrdinal("end_line");
        int oParent = reader.GetOrdinal("parent_symbol_id");
        int oIsTest = reader.GetOrdinal("is_test");
        int oTestContainer = reader.GetOrdinal("test_container");
        int oTestLifecycle = reader.GetOrdinal("test_lifecycle");
        int oFileStatus = reader.GetOrdinal("file_status");
        int oHasFileEvidence = reader.GetOrdinal("has_file_evidence");
        int oHasParseDiagnostics = reader.GetOrdinal("has_parse_diagnostics");

        while (reader.Read())
        {
            string symbolId = reader.GetString(oSymbolId);
            string name = reader.GetString(oName);
            string? signature = reader.IsDBNull(oSignature) ? null : reader.GetString(oSignature);
            string kind = reader.GetString(oKind);
            string language = reader.GetString(oLanguage);
            string path = reader.GetString(oPath);
            int startLine = reader.IsDBNull(oStartLine) ? 0 : reader.GetInt32(oStartLine); // v1 NOT NULL; guard defensive
            int endLine = reader.IsDBNull(oEndLine) ? 0 : reader.GetInt32(oEndLine);       // v1 NOT NULL; guard defensive
            string? parentId = reader.IsDBNull(oParent) ? null : reader.GetString(oParent);
            var testEvidence = TestRoleEvidence.FromArtifactFacts(
                isTest: reader.GetBoolean(oIsTest),
                isContainer: reader.GetBoolean(oTestContainer),
                isLifecycle: reader.GetBoolean(oTestLifecycle),
                fileStatus: reader.IsDBNull(oFileStatus) ? null : reader.GetString(oFileStatus),
                hasParseDiagnostics: reader.GetBoolean(oHasParseDiagnostics),
                hasFileEvidence: reader.GetBoolean(oHasFileEvidence));

            results.Add(new IndexedSymbol(
                DocId: reader.GetInt32(oDocId),
                SymbolId: symbolId,
                Name: name,
                Signature: signature,
                Kind: kind,
                Language: language,
                FilePath: path,
                StartLine: startLine,
                EndLine: endLine,
                ParentId: parentId,
                IsTest: testEvidence.IsTest,
                TestContainer: testEvidence.IsContainer,
                TestLifecycle: testEvidence.IsLifecycle,
                TestEvidenceStatus: testEvidence.Status,
                TestEvidenceReason: testEvidence.Reason));
        }
    }

    private static string AddPathParameters(SqliteCommand command, IReadOnlyList<string> paths, int offset, int count)
    {
        var placeholders = new string[count];
        for (int i = 0; i < count; i++)
        {
            string name = "$p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            placeholders[i] = name;
            command.Parameters.AddWithValue(name, paths[offset + i]);
        }
        return string.Join(", ", placeholders);
    }

    /// <summary>
    /// A few focused/large test artifacts model the schema gate's minimal historical shape and intentionally omit
    /// optional evidence tables or newly additive role columns. Preserve the reader's legacy compatibility by
    /// projecting their evidence as unknown/defaulted; released current artifacts have every source below.
    /// </summary>
    private readonly record struct EvidenceProjection(
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
}
