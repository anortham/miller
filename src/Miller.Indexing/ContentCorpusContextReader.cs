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

        // Pooling=false: content.db is a rebuildable derived artifact whose file can be replaced wholesale;
        // a pooled handle would pin the unlinked old inode (same hazard as SqliteReadOnlyAccess).
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = contentDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        var results = new List<TextContentSearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (IndexedSymbol symbol in symbols)
        {
            var candidates = new List<TextContentSearchHit>();
            ReadSymbolArm(connection, symbol, excludeTests, limitPerSymbol, useNameFallback: false, candidates);
            ReadSymbolArm(connection, symbol, excludeTests, limitPerSymbol, useNameFallback: true, candidates);

            foreach (TextContentSearchHit hit in candidates
                .OrderBy(static hit => hit.DisplayPath, StringComparer.Ordinal)
                .ThenBy(static hit => hit.LineStart)
                .ThenBy(static hit => hit.ChunkId, StringComparer.Ordinal)
                .Take(limitPerSymbol))
            {
                if (seen.Add(hit.SourceId + ":" + hit.ChunkId))
                    results.Add(hit);
            }
        }

        return results;
    }

    private static void ReadSymbolArm(
        SqliteConnection connection,
        IndexedSymbol symbol,
        bool excludeTests,
        int limit,
        bool useNameFallback,
        List<TextContentSearchHit> results)
    {
        using var command = connection.CreateCommand();
        string testPredicate = excludeTests ? "AND is_test = 0" : "";
        string symbolPredicate = useNameFallback
            ? """
              AND containing_symbol_id IS NULL
              AND containing_symbol_name = $symbol_name
              """
            : "AND containing_symbol_id = $symbol_id";
        command.CommandText = $"""
            SELECT source_id, chunk_id, content_kind, path, url, display_path, language,
                   line_start, line_end, byte_start, byte_end, raw_text, is_test,
                   source_bytes, containing_symbol_id, containing_symbol_name
            FROM content_chunks
            WHERE content_kind = $kind
              {testPredicate}
              {symbolPredicate}
            ORDER BY display_path, line_start, chunk_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$kind", TextContentKind.WorkspaceSource);
        command.Parameters.AddWithValue("$limit", limit);
        if (useNameFallback)
            command.Parameters.AddWithValue("$symbol_name", symbol.Name);
        else
            command.Parameters.AddWithValue("$symbol_id", symbol.SymbolId);

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
            int lineStart = reader.GetInt32(oLineStart);
            string rawText = reader.GetString(oRawText);
            results.Add(new TextContentSearchHit(
                reader.GetString(oSourceId),
                reader.GetString(oChunkId),
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
