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

        using var command = connection.CreateCommand();
        // v1 columns. By-name reads (D6) decouple SELECT order from the GetX ordinals: a future column
        // add/reorder can never silently shift a value into the wrong field again.
        command.CommandText = """
            SELECT symbol_id, name, signature, kind, language, path,
                   start_line, end_line, parent_symbol_id, is_test
            FROM symbols
            WHERE name IS NOT NULL
            ORDER BY path, start_line, symbol_id;
            """;

        var results = new List<IndexedSymbol>();
        int docId = 0;
        using var reader = command.ExecuteReader();

        // Resolve ordinals once (cheap, cached) — not per-row over ~565k startup rows.
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
            bool isTest = reader.GetBoolean(oIsTest); // typed v1 column; replaces the metadata JSON-parse hack (D4)

            results.Add(new IndexedSymbol(
                DocId: docId++,
                SymbolId: symbolId,
                Name: name,
                Signature: signature,
                Kind: kind,
                Language: language,
                FilePath: path,
                StartLine: startLine,
                EndLine: endLine,
                ParentId: parentId,
                IsTest: isTest));
        }

        return results;
    }
}
