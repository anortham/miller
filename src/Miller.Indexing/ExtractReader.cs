using System.Text;
using Microsoft.Data.Sqlite;
using Miller.Core.Editing;

namespace Miller.Indexing;

/// <summary>
/// The M2 on-demand detail reader over a julie extract DB. Where <see cref="SqliteSymbolReader"/> does the
/// single bulk startup pass that builds the in-memory index, <see cref="ExtractReader"/> answers the
/// lower-volume per-<c>inspect</c> queries: symbol detail (doc/visibility/body spans), name-based references
/// (the <c>identifiers</c> table), and the body slice out of <c>files.content</c>. It opens
/// <c>Mode=ReadOnly</c> like the startup reader, never writes, and parameterizes every query.
///
/// The schema gate already ran at startup (<see cref="SqliteSymbolReader.Read"/>), so these reads do not
/// re-gate — they just re-open the read-only connection. All offsets follow julie's contract: byte offsets
/// are absolute UTF-8 byte indices into the file content; lines are 1-based.
/// </summary>
public sealed class ExtractReader
{
    /// <summary>
    /// Read one symbol's detail by opaque id, or null if no such symbol exists.
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    public static SymbolDetail? ReadDetail(string dbPath, string symbolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        using var connection = Open(dbPath);

        using var command = connection.CreateCommand();
        // v1: keyed by symbol_id (was id). code_context is GONE from v1 symbols (it lives only on identifiers),
        // so it is no longer selected (reconciliation #11). By-name reads (D6) decouple value from SELECT order.
        command.CommandText = """
            SELECT doc_comment, visibility,
                   body_start_byte, body_end_byte, body_start_line, body_end_line
            FROM symbols WHERE symbol_id = $id;
            """;
        command.Parameters.AddWithValue("$id", symbolId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        int oDocComment = reader.GetOrdinal("doc_comment");
        int oVisibility = reader.GetOrdinal("visibility");
        int oBodyStartByte = reader.GetOrdinal("body_start_byte");
        int oBodyEndByte = reader.GetOrdinal("body_end_byte");
        int oBodyStartLine = reader.GetOrdinal("body_start_line");
        int oBodyEndLine = reader.GetOrdinal("body_end_line");

        return new SymbolDetail(
            DocComment: reader.IsDBNull(oDocComment) ? null : reader.GetString(oDocComment),
            Visibility: reader.IsDBNull(oVisibility) ? null : reader.GetString(oVisibility),
            BodyStartByte: reader.IsDBNull(oBodyStartByte) ? null : reader.GetInt32(oBodyStartByte),
            BodyEndByte: reader.IsDBNull(oBodyEndByte) ? null : reader.GetInt32(oBodyEndByte),
            BodyStartLine: reader.IsDBNull(oBodyStartLine) ? null : reader.GetInt32(oBodyStartLine),
            BodyEndLine: reader.IsDBNull(oBodyEndLine) ? null : reader.GetInt32(oBodyEndLine));
    }

    /// <summary>
    /// Read the byte-span facts an M6 edit operation needs about one symbol, or null if no such symbol exists.
    /// Returns the WHOLE-symbol span (<c>[start_byte, end_byte)</c>) plus the BODY span
    /// (<c>[body_start_byte, body_end_byte)</c>) — so the Server derives the signature span as
    /// <c>[start_byte, body_start_byte)</c> and the body span directly (verified-fact 1). A symbol with no body
    /// (e.g. a field) carries NULL <c>body_start_byte</c>/<c>body_end_byte</c>; those are preserved as null here
    /// so the planner rejects body/signature ops with a clean message rather than splicing garbage. The whole-span
    /// <c>start_byte</c>/<c>end_byte</c> are NOT NULL for a real extract; a defensive 0 is used if julie ever
    /// emits NULL (a degenerate span the planner then rejects as out-of-range).
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    public static SymbolEditSpan? ReadEditSpan(string dbPath, string symbolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        using var connection = Open(dbPath);

        using var command = connection.CreateCommand();
        // v1: keyed by symbol_id (was id). By-name reads (D6).
        command.CommandText = """
            SELECT start_byte, end_byte, body_start_byte, body_end_byte, start_line, name
            FROM symbols WHERE symbol_id = $id;
            """;
        command.Parameters.AddWithValue("$id", symbolId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        int oStartByte = reader.GetOrdinal("start_byte");
        int oEndByte = reader.GetOrdinal("end_byte");
        int oBodyStartByte = reader.GetOrdinal("body_start_byte");
        int oBodyEndByte = reader.GetOrdinal("body_end_byte");
        int oStartLine = reader.GetOrdinal("start_line");
        int oName = reader.GetOrdinal("name");

        return new SymbolEditSpan(
            StartByte: reader.IsDBNull(oStartByte) ? 0 : reader.GetInt32(oStartByte),
            EndByte: reader.IsDBNull(oEndByte) ? 0 : reader.GetInt32(oEndByte),
            BodyStartByte: reader.IsDBNull(oBodyStartByte) ? null : reader.GetInt32(oBodyStartByte),
            BodyEndByte: reader.IsDBNull(oBodyEndByte) ? null : reader.GetInt32(oBodyEndByte),
            StartLine: reader.IsDBNull(oStartLine) ? 0 : reader.GetInt32(oStartLine),
            Name: reader.GetString(oName));
    }

    /// <summary>
    /// All identifier rows whose <c>name</c> equals <paramref name="name"/> (the name-based reference list).
    /// Ordered deterministically by file then line then id. Empty if none.
    /// </summary>
    public static IReadOnlyList<SymbolRef> ReadReferences(string dbPath, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var connection = Open(dbPath);

        using var command = connection.CreateCommand();
        // v1: identifiers.file_path → path, id → identifier_id (ORDER BY). By-name reads (D6).
        command.CommandText = """
            SELECT name, kind, path, start_line, containing_symbol_id
            FROM identifiers WHERE name = $name
            ORDER BY path, start_line, identifier_id;
            """;
        command.Parameters.AddWithValue("$name", name);
        return ReadRefs(command);
    }

    /// <summary>
    /// The one-hop callees of a symbol: identifier rows whose <c>containing_symbol_id</c> is
    /// <paramref name="containingSymbolId"/> AND <c>kind = 'call'</c>. Empty if none.
    /// </summary>
    public static IReadOnlyList<SymbolRef> ReadCallees(string dbPath, string containingSymbolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containingSymbolId);
        using var connection = Open(dbPath);

        using var command = connection.CreateCommand();
        // v1: identifiers.file_path → path, id → identifier_id (ORDER BY). By-name reads (D6).
        command.CommandText = """
            SELECT name, kind, path, start_line, containing_symbol_id
            FROM identifiers WHERE containing_symbol_id = $cid AND kind = 'call'
            ORDER BY path, start_line, identifier_id;
            """;
        command.Parameters.AddWithValue("$cid", containingSymbolId);
        return ReadRefs(command);
    }

    private static IReadOnlyList<SymbolRef> ReadRefs(SqliteCommand command)
    {
        var results = new List<SymbolRef>();
        using var reader = command.ExecuteReader();
        int oName = reader.GetOrdinal("name");
        int oKind = reader.GetOrdinal("kind");
        int oPath = reader.GetOrdinal("path");
        int oStartLine = reader.GetOrdinal("start_line");
        int oContaining = reader.GetOrdinal("containing_symbol_id");
        while (reader.Read())
        {
            results.Add(new SymbolRef(
                Name: reader.GetString(oName),
                Kind: reader.GetString(oKind),
                FilePath: reader.GetString(oPath),
                StartLine: reader.IsDBNull(oStartLine) ? 0 : reader.GetInt32(oStartLine),
                ContainingSymbolId: reader.IsDBNull(oContaining) ? null : reader.GetString(oContaining)));
        }
        return results;
    }

    /// <summary>
    /// Every exact per-occurrence byte token in <c>identifiers</c> whose <c>name</c> equals
    /// <paramref name="name"/> — the rename input (verified-fact 2). Each <see cref="IdentifierSite"/> carries
    /// the absolute UTF-8 byte span (<c>[start_byte, end_byte)</c>) of that occurrence, so the Server splices the
    /// exact token at every site (no fuzzy whole-word matching). Ordered by <c>file_path</c> then
    /// <c>start_byte</c> for a deterministic, file-grouped rename preview. Matching is NAME-based (so a homonym
    /// is included — see <see cref="IdentifierSite"/>) because <c>target_symbol_id</c> is NULL at extract. Rows
    /// whose <c>start_byte</c>/<c>end_byte</c> are NULL are skipped (no usable token to rewrite). Empty if none.
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    public static IReadOnlyList<IdentifierSite> ReadIdentifierSites(string dbPath, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var connection = Open(dbPath);

        using var command = connection.CreateCommand();
        // v1: identifiers.file_path → path. By-name reads (D6).
        command.CommandText = """
            SELECT path, start_byte, end_byte, start_line
            FROM identifiers WHERE name = $name
            ORDER BY path, start_byte;
            """;
        command.Parameters.AddWithValue("$name", name);

        var results = new List<IdentifierSite>();
        using var reader = command.ExecuteReader();
        int oPath = reader.GetOrdinal("path");
        int oStartByte = reader.GetOrdinal("start_byte");
        int oEndByte = reader.GetOrdinal("end_byte");
        int oStartLine = reader.GetOrdinal("start_line");
        while (reader.Read())
        {
            // A NULL byte span has no token to rewrite — skip it rather than emit a degenerate (0,0) edit.
            if (reader.IsDBNull(oStartByte) || reader.IsDBNull(oEndByte))
                continue;

            results.Add(new IdentifierSite(
                FilePath: reader.GetString(oPath),
                StartByte: reader.GetInt32(oStartByte),
                EndByte: reader.GetInt32(oEndByte),
                StartLine: reader.IsDBNull(oStartLine) ? 0 : reader.GetInt32(oStartLine)));
        }
        return results;
    }

    /// <summary>
    /// The full indexed text of <paramref name="filePath"/> from <c>files.content</c> — julie's snapshot of the
    /// file as of the last extract — or null if the file is absent (or its content column is NULL). This is the
    /// M6 freshness gate's "indexed side": the gate SHA256s this against the current disk text and refuses a
    /// stale edit. Returned verbatim (julie stores UTF-8 TEXT; the SQLite driver decodes it losslessly), so the
    /// accent/multibyte bytes survive the round-trip and the gate does not false-positive on encoding drift.
    /// </summary>
    /// <exception cref="FileNotFoundException">The DB file does not exist.</exception>
    public static string? ReadIndexedFileText(string dbPath, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return ReadFileContent(dbPath, filePath);
    }

    /// <summary>
    /// Slice a symbol's body text out of <c>files.content</c>. Prefers the absolute UTF-8 byte span
    /// [<paramref name="startByte"/>, <paramref name="endByte"/>); if either byte offset is NULL, falls back
    /// to a 1-based line slice [<paramref name="startLine"/>, <paramref name="endLine"/>]. Returns null when
    /// no usable span is supplied, the file/content is absent or empty, or the span is degenerate.
    /// </summary>
    public static string? ReadBody(
        string dbPath, string filePath,
        int? startByte, int? endByte, int? startLine, int? endLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // No usable span at all → nothing to read (caller renders a "body unavailable" note).
        bool hasByteSpan = startByte is not null && endByte is not null;
        bool hasLineSpan = startLine is not null && endLine is not null;
        if (!hasByteSpan && !hasLineSpan)
            return null;

        string? content = ReadFileContent(dbPath, filePath);
        if (string.IsNullOrEmpty(content))
            return null;

        if (hasByteSpan)
        {
            string? sliced = SliceByBytes(content, startByte!.Value, endByte!.Value);
            if (sliced is not null)
                return sliced;
            // A byte span that fell outside the (possibly stale) content degrades to the line slice.
        }

        if (hasLineSpan)
            return SliceByLines(content, startLine!.Value, endLine!.Value);

        return null;
    }

    /// <summary>
    /// The canonical <c>root_path</c> the artifact was extracted from, recorded in <c>artifact_metadata</c>
    /// (v1's single metadata surface), or null if the key is absent. Startup derives the stable workspace id
    /// from this (SHA-256 of the canonical root) and uses it as the artifact-identity check (reconciliation #14):
    /// v1 has no <c>workspace_id</c> metadata key, so identity is the root path, not a stored id.
    /// </summary>
    public static string? ReadRootPath(string dbPath)
    {
        using var connection = Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM artifact_metadata WHERE key = 'root_path';";
        var value = command.ExecuteScalar();
        return value is string s ? s : null;
    }

    private static string? ReadFileContent(string dbPath, string filePath)
    {
        using var connection = Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM files WHERE path = $path;";
        command.Parameters.AddWithValue("$path", filePath);
        var value = command.ExecuteScalar();
        return value is string s ? s : null;
    }

    // julie byte offsets are absolute UTF-8 byte indices; C# strings are UTF-16. Encode, slice on bytes,
    // decode. Returns null on a degenerate or out-of-range span so the caller can degrade gracefully.
    private static string? SliceByBytes(string content, int startByte, int endByte)
    {
        if (startByte < 0 || endByte <= startByte)
            return null;
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        if (startByte >= bytes.Length)
            return null;
        int end = Math.Min(endByte, bytes.Length);
        if (end <= startByte)
            return null;
        return Encoding.UTF8.GetString(bytes, startByte, end - startByte);
    }

    // 1-based inclusive line slice. Splits on '\n' keeping the original newlines between joined lines.
    private static string? SliceByLines(string content, int startLine, int endLine)
    {
        if (startLine < 1 || endLine < startLine)
            return null;
        string[] lines = content.Split('\n');
        if (startLine > lines.Length)
            return null;
        int from = startLine - 1;
        int toInclusive = Math.Min(endLine, lines.Length) - 1;
        if (toInclusive < from)
            return null;
        return string.Join('\n', lines[from..(toInclusive + 1)]);
    }

    // Share the single D4 read discipline used by SqliteSymbolReader + FreshnessReader: file-exists check +
    // WAL-sidecar writable-dir probe + Mode=ReadOnly + SQLITE_READONLY→InvalidOperationException mapping. This
    // keeps every read path consistent and surfaces a clear, actionable error (a missing file or a non-writable
    // DB directory) instead of a cryptic SQLITE_READONLY (code 8) mid-stream (finding-2).
    private static SqliteConnection Open(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        return SqliteReadOnlyAccess.Open(dbPath);
    }
}
