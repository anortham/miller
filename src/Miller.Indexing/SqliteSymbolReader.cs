using Microsoft.Data.Sqlite;
using Miller.Core.Contracts;

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
    /// ordered list. DocId is the 0-based ordinal of the SELECT order (file_path, start_line, id).
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    /// <exception cref="IncompatibleExtractException">The DB is not a compatible v7.13.0 julie extract.</exception>
    public static IReadOnlyList<IndexedSymbol> Read(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        // Shared D4 read discipline (file-exists + writable-dir probe + Mode=ReadOnly + SQLITE_READONLY map).
        using var connection = SqliteReadOnlyAccess.Open(dbPath);

        JulieSchemaGate.Verify(connection);

        using var command = connection.CreateCommand();
        // Deterministic DocId ordering. SELECT column order is LOCKED to the GetX ordinals below.
        command.CommandText = """
            SELECT id, name, signature, kind, language, file_path, start_line, end_line, parent_id, metadata
            FROM symbols
            WHERE name IS NOT NULL
            ORDER BY file_path, start_line, id;
            """;

        var results = new List<IndexedSymbol>();
        int docId = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string symbolId = reader.GetString(0);                              // id          NOT NULL
            string name = reader.GetString(1);                                  // name        NOT NULL
            string? signature = reader.IsDBNull(2) ? null : reader.GetString(2);// signature   nullable
            string kind = reader.GetString(3);                                  // kind        NOT NULL
            string language = reader.GetString(4);                              // language    NOT NULL
            string filePath = reader.GetString(5);                              // file_path   NOT NULL
            int startLine = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);        // start_line  nullable -> 0
            int endLine = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);          // end_line    nullable -> 0
            string? parentId = reader.IsDBNull(8) ? null : reader.GetString(8); // parent_id   nullable
            string? metadata = reader.IsDBNull(9) ? null : reader.GetString(9); // metadata    nullable (JSON)

            var (isTest, testRole) = ParseTestSignals(metadata);

            results.Add(new IndexedSymbol(
                DocId: docId++,
                SymbolId: symbolId,
                Name: name,
                Signature: signature,
                Kind: kind,
                Language: language,
                FilePath: filePath,
                StartLine: startLine,
                EndLine: endLine,
                ParentId: parentId,
                IsTest: isTest,
                TestRole: testRole));
        }

        return results;
    }

    /// <summary>
    /// Read the cross-language test signals from julie's <c>symbols.metadata</c> JSON (decision-4; M4 Task 9).
    /// julie's <c>test_detection.rs</c> writes <c>"is_test": true</c> (only when true) AND, when known, a
    /// <c>"test_role"</c> string (e.g. <c>unit</c>/<c>integration</c>/<c>e2e</c>) into the metadata of test symbols
    /// across all 34+ languages (compact serde JSON). We parse both in ONE pass and surface the boolean plus the
    /// optional <see cref="TestRole"/>.
    ///
    /// <para>Perf: ~90% of symbols are not tests and carry NEITHER key, so we skip JSON parsing entirely unless a
    /// cheap ordinal substring check matches one of them — over ~565k startup rows that avoids ~half a million
    /// parses. A malformed/absent value yields <c>(false, null)</c> (the signals are advisory, never throw).</para>
    /// </summary>
    private static (bool IsTest, TestRole? TestRole) ParseTestSignals(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)
            || (!metadataJson.Contains("\"is_test\"", StringComparison.Ordinal)
                && !metadataJson.Contains("\"test_role\"", StringComparison.Ordinal)))
            return (false, null);

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return (false, null);

            bool isTest = root.TryGetProperty("is_test", out var isTestEl)
                && isTestEl.ValueKind == System.Text.Json.JsonValueKind.True;

            TestRole? testRole = null;
            if (root.TryGetProperty("test_role", out var roleEl)
                && roleEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                string? role = roleEl.GetString();
                if (!string.IsNullOrWhiteSpace(role))
                    testRole = new TestRole(role);
            }

            return (isTest, testRole);
        }
        catch (System.Text.Json.JsonException)
        {
            // julie writes well-formed JSON; tolerate a corrupt/hand-mangled value as "not a test".
            return (false, null);
        }
    }

}
