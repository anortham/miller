using Microsoft.Data.Sqlite;
using Miller.Core.Search;

namespace Miller.Indexing;

/// <summary>
/// Small read-only context helper over <c>content.db</c>. Unlike <see cref="FtsTextContentSearchIndex"/>, this
/// does not search text; it reads chunks already attached to a containing symbol so context assembly can include
/// nearby source evidence without guessing through FTS terms.
/// </summary>
public static class ContentCorpusContextReader
{
    public static IReadOnlyList<TextContentSearchHit> ReadContainingSymbolChunks(
        string contentDbPath,
        IReadOnlyList<IndexedSymbol> symbols,
        bool excludeTests,
        int limitPerSymbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Count == 0 || limitPerSymbol <= 0 || !File.Exists(contentDbPath))
            return Array.Empty<TextContentSearchHit>();

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = contentDbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();

        var results = new List<TextContentSearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IndexedSymbol symbol in symbols)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_id, chunk_id, content_kind, path, url, display_path, language,
                       line_start, line_end, byte_start, byte_end, raw_text, is_test,
                       source_bytes, containing_symbol_id, containing_symbol_name
                FROM content_chunks
                WHERE content_kind = $kind
                  AND ($exclude_tests = 0 OR is_test = 0)
                  AND (containing_symbol_id = $symbol_id
                       OR (containing_symbol_id IS NULL AND containing_symbol_name = $symbol_name))
                ORDER BY display_path, line_start, chunk_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$kind", TextContentKind.WorkspaceSource);
            command.Parameters.AddWithValue("$exclude_tests", excludeTests ? 1 : 0);
            command.Parameters.AddWithValue("$symbol_id", symbol.SymbolId);
            command.Parameters.AddWithValue("$symbol_name", symbol.Name);
            command.Parameters.AddWithValue("$limit", limitPerSymbol);

            using var reader = command.ExecuteReader();
            int oSourceId = reader.GetOrdinal("source_id");
            int oChunkId = reader.GetOrdinal("chunk_id");
            int oContentKind = reader.GetOrdinal("content_kind");
            int oPath = reader.GetOrdinal("path");
            int oUrl = reader.GetOrdinal("url");
            int oDisplayPath = reader.GetOrdinal("display_path");
            int oLanguage = reader.GetOrdinal("language");
            int oLineStart = reader.GetOrdinal("line_start");
            int oLineEnd = reader.GetOrdinal("line_end");
            int oByteStart = reader.GetOrdinal("byte_start");
            int oByteEnd = reader.GetOrdinal("byte_end");
            int oRawText = reader.GetOrdinal("raw_text");
            int oSourceBytes = reader.GetOrdinal("source_bytes");
            int oContainingSymbolId = reader.GetOrdinal("containing_symbol_id");
            int oContainingSymbolName = reader.GetOrdinal("containing_symbol_name");
            while (reader.Read())
            {
                string sourceId = reader.GetString(oSourceId);
                string chunkId = reader.GetString(oChunkId);
                if (!seen.Add(sourceId + ":" + chunkId))
                    continue;

                int lineStart = reader.GetInt32(oLineStart);
                string rawText = reader.GetString(oRawText);
                results.Add(new TextContentSearchHit(
                    sourceId,
                    chunkId,
                    reader.GetString(oContentKind),
                    reader.IsDBNull(oPath) ? null : reader.GetString(oPath),
                    reader.IsDBNull(oUrl) ? null : reader.GetString(oUrl),
                    reader.GetString(oDisplayPath),
                    reader.GetString(oLanguage),
                    Score: 0.0,
                    Line: lineStart,
                    LineStart: lineStart,
                    LineEnd: reader.GetInt32(oLineEnd),
                    ByteStart: reader.GetInt64(oByteStart),
                    ByteEnd: reader.GetInt64(oByteEnd),
                    Snippet: FirstLine(rawText),
                    SourceBytes: reader.GetInt64(oSourceBytes),
                    ContainingSymbolId: reader.IsDBNull(oContainingSymbolId) ? null : reader.GetString(oContainingSymbolId),
                    ContainingSymbolName: reader.IsDBNull(oContainingSymbolName) ? null : reader.GetString(oContainingSymbolName)));
            }
        }

        return results;
    }

    private static string FirstLine(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return "";

        ReadOnlySpan<char> span = rawText.AsSpan().Trim();
        int newline = span.IndexOf('\n');
        if (newline >= 0)
            span = span[..newline].TrimEnd('\r');
        return span.ToString();
    }
}
