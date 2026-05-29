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
    /// ordered list. DocId is the 0-based ordinal of the SELECT order (file_path, start_line, id).
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The DB's directory is not writable (WAL sidecar trap).</exception>
    /// <exception cref="IncompatibleExtractException">The DB is not a compatible v7.12.2 julie extract.</exception>
    public static IReadOnlyList<IndexedSymbol> Read(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        string absDbPath = Path.GetFullPath(dbPath);
        if (!File.Exists(absDbPath))
            throw new FileNotFoundException(
                $"julie extract DB not found at '{absDbPath}'. Run `julie-server extract ... scan` first " +
                "(see scripts/restore-julie-server.sh to obtain the binary).", absDbPath);

        string? dir = Path.GetDirectoryName(absDbPath);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException($"Cannot determine the directory of DB path '{absDbPath}'.");
        EnsureDirectoryWritable(dir, absDbPath);

        var connectionString =
            new SqliteConnectionStringBuilder { DataSource = absDbPath, Mode = SqliteOpenMode.ReadOnly }
                .ToString();

        using var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 8 /* SQLITE_READONLY */)
        {
            throw new InvalidOperationException(
                $"Cannot open '{absDbPath}' read-only: the DB directory '{dir}' must be writable for the WAL " +
                "wal-index sidecar. Move the extract under a Miller-owned writable directory.", ex);
        }

        JulieSchemaGate.Verify(connection);

        using var command = connection.CreateCommand();
        // Deterministic DocId ordering. SELECT column order is LOCKED to the GetX ordinals below.
        command.CommandText = """
            SELECT id, name, signature, kind, language, file_path, start_line, parent_id, metadata
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
            string? parentId = reader.IsDBNull(7) ? null : reader.GetString(7); // parent_id   nullable
            string? metadata = reader.IsDBNull(8) ? null : reader.GetString(8); // metadata    nullable (JSON)

            results.Add(new IndexedSymbol(
                DocId: docId++,
                SymbolId: symbolId,
                Name: name,
                Signature: signature,
                Kind: kind,
                Language: language,
                FilePath: filePath,
                StartLine: startLine,
                ParentId: parentId,
                IsTest: ParseIsTest(metadata)));
        }

        return results;
    }

    /// <summary>
    /// Read the cross-language test signal from julie's <c>symbols.metadata</c> JSON (decision-4). julie's
    /// <c>test_detection.rs</c> writes <c>"is_test": true</c> into the metadata of test symbols across all 34
    /// languages, ONLY when true (compact serde JSON). We parse just the <c>is_test</c> boolean.
    ///
    /// Perf: ~90% of symbols are not tests and carry no <c>is_test</c> key, so we skip JSON parsing entirely
    /// unless a cheap ordinal substring check matches — over ~565k startup rows that avoids ~half a million
    /// parses. A malformed/absent/false value is <c>false</c> (the signal is advisory, never throws).
    /// </summary>
    private static bool ParseIsTest(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)
            || !metadataJson.Contains("\"is_test\"", StringComparison.Ordinal))
            return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("is_test", out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch (System.Text.Json.JsonException)
        {
            // julie writes well-formed JSON; tolerate a corrupt/hand-mangled value as "not a test".
            return false;
        }
    }

    // Probe writability by creating + deleting a temp file in the DB directory. A pure FileMode check is
    // insufficient (ACLs, read-only mounts); the create+delete round-trip is the honest test.
    private static void EnsureDirectoryWritable(string dir, string absDbPath)
    {
        string probe = Path.Combine(dir, ".miller-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"The directory '{dir}' of julie extract DB '{absDbPath}' is not writable. A Mode=ReadOnly " +
                "reader of a WAL DB still needs to write the wal-index sidecar there; move the extract under a " +
                "Miller-owned writable directory.", ex);
        }
    }
}
