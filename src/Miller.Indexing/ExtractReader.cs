using System.Text;
using Microsoft.Data.Sqlite;

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
        command.CommandText = """
            SELECT doc_comment, visibility, code_context,
                   body_start_byte, body_end_byte, body_start_line, body_end_line
            FROM symbols WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", symbolId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new SymbolDetail(
            DocComment: reader.IsDBNull(0) ? null : reader.GetString(0),
            Visibility: reader.IsDBNull(1) ? null : reader.GetString(1),
            CodeContext: reader.IsDBNull(2) ? null : reader.GetString(2),
            BodyStartByte: reader.IsDBNull(3) ? null : reader.GetInt32(3),
            BodyEndByte: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            BodyStartLine: reader.IsDBNull(5) ? null : reader.GetInt32(5),
            BodyEndLine: reader.IsDBNull(6) ? null : reader.GetInt32(6));
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
        command.CommandText = """
            SELECT name, kind, file_path, start_line, containing_symbol_id
            FROM identifiers WHERE name = $name
            ORDER BY file_path, start_line, id;
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
        command.CommandText = """
            SELECT name, kind, file_path, start_line, containing_symbol_id
            FROM identifiers WHERE containing_symbol_id = $cid AND kind = 'call'
            ORDER BY file_path, start_line, id;
            """;
        command.Parameters.AddWithValue("$cid", containingSymbolId);
        return ReadRefs(command);
    }

    private static IReadOnlyList<SymbolRef> ReadRefs(SqliteCommand command)
    {
        var results = new List<SymbolRef>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SymbolRef(
                Name: reader.GetString(0),
                Kind: reader.GetString(1),
                FilePath: reader.GetString(2),
                StartLine: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                ContainingSymbolId: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return results;
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
    /// The <c>workspace_id</c> recorded in <c>external_extract_metadata</c>, or null if the key is absent.
    /// Used by startup to populate <c>WorkspaceContext.WorkspaceId</c> for telemetry scoping.
    /// </summary>
    public static string? ReadWorkspaceId(string dbPath)
    {
        using var connection = Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM external_extract_metadata WHERE key = 'workspace_id';";
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

    private static SqliteConnection Open(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        string absDbPath = Path.GetFullPath(dbPath);
        if (!File.Exists(absDbPath))
            throw new FileNotFoundException(
                $"julie extract DB not found at '{absDbPath}'.", absDbPath);

        var connectionString =
            new SqliteConnectionStringBuilder { DataSource = absDbPath, Mode = SqliteOpenMode.ReadOnly }
                .ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
