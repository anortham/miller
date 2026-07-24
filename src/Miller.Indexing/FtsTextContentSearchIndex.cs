using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;

namespace Miller.Indexing;

public sealed class FtsTextContentSearchIndex : ITextContentSearchIndex, ISemanticContentLookup
{
    private const int SnippetRadius = 2;
    private const int WidenedCandidateLimit = 5000;
    private const double TokenPhraseBoost = 2.5;

    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, TextChunk> _chunksById;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ContentSymbolSpan>> _spansBySourceId;
    private readonly int _documentCount;
    private readonly double _avgdl;

    private FtsTextContentSearchIndex(
        string connectionString,
        IReadOnlyList<TextChunk> chunks,
        IReadOnlyDictionary<string, IReadOnlyList<ContentSymbolSpan>> spansBySourceId,
        long revision)
    {
        _connectionString = connectionString;
        _chunksById = chunks.ToDictionary(static c => c.ChunkId, StringComparer.Ordinal);
        _spansBySourceId = spansBySourceId;
        _documentCount = chunks.Count;
        _avgdl = chunks.Count == 0 ? 0.0 : chunks.Average(static c => c.DocLen);
        Revision = revision;
    }

    public int DocumentCount => _chunksById.Count;

    public long Revision { get; }

    public static FtsTextContentSearchIndex Open(string contentDbPath, long expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);

        string absPath = Path.GetFullPath(contentDbPath);
        if (!File.Exists(absPath))
            throw new FileNotFoundException($"content.db not found at '{absPath}'. Rebuild the content corpus.", absPath);

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        ContentMeta meta = ReadMeta(connection, absPath);
        if (meta.SchemaVersion != ContentCorpusSchema.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"content.db at '{absPath}' has schema_version {meta.SchemaVersion}; " +
                $"this build expects {ContentCorpusSchema.SchemaVersion}. Rebuild the content corpus.");
        }

        if (meta.WorkspaceRevision != expectedRevision)
        {
            string actualRevision = meta.WorkspaceRevision?.ToString(CultureInfo.InvariantCulture) ?? "none";
            throw new InvalidOperationException(
                $"content.db at '{absPath}' is stale: revision {actualRevision}, expected {expectedRevision}. " +
                "Refresh or rebuild the content corpus.");
        }

        EnsureSchema(connection, absPath);
        return new FtsTextContentSearchIndex(
            connectionString,
            ReadChunks(connection, absPath),
            ReadSymbolSpans(connection, absPath),
            meta.WorkspaceRevision.GetValueOrDefault());
    }

    public static FtsTextContentSearchIndex OpenUnversioned(string contentDbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDbPath);

        string absPath = Path.GetFullPath(contentDbPath);
        if (!File.Exists(absPath))
            throw new FileNotFoundException($"content.db not found at '{absPath}'. Import content first.", absPath);

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        ContentMeta meta = ReadMeta(connection, absPath);
        if (meta.SchemaVersion != ContentCorpusSchema.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"content.db at '{absPath}' has schema_version {meta.SchemaVersion}; " +
                $"this build expects {ContentCorpusSchema.SchemaVersion}.");
        }

        EnsureSchema(connection, absPath);
        return new FtsTextContentSearchIndex(
            connectionString,
            ReadChunks(connection, absPath),
            ReadSymbolSpans(connection, absPath),
            meta.WorkspaceRevision.GetValueOrDefault());
    }

    public IReadOnlyList<TextContentSearchHit> Search(
        string query,
        string contentKind,
        int limit = 10,
        bool excludeTests = false) =>
        Search(query, new[] { contentKind }, limit, excludeTests);

    public IReadOnlyList<TextContentSearchHit> Search(
        string query,
        IReadOnlyCollection<string> contentKinds,
        int limit = 10,
        bool excludeTests = false)
    {
        if (contentKinds.Count == 0 || limit <= 0 || _chunksById.Count == 0)
            return Array.Empty<TextContentSearchHit>();

        var allowedKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string kind in contentKinds)
            if (!string.IsNullOrWhiteSpace(kind))
                allowedKinds.Add(kind);
        if (allowedKinds.Count == 0)
            return Array.Empty<TextContentSearchHit>();

        TextSearchQueryPlan? plan = TextSearchQueryPlan.Create(query);
        if (plan is null)
            return Array.Empty<TextContentSearchHit>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string term in plan.DistinctTerms)
            documentFrequency[term] = CountChunksMatching(connection, QuoteFts(term));

        string strictMatch = JoinFtsTerms(plan.CoverageTerms, " AND ");
        IReadOnlyList<string> candidateIds = ChunkCandidates(connection, strictMatch);
        var hits = new List<TextContentSearchHit>();
        var tokens = new List<string>(64);
        var seenCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        var coverageTermSet = plan.CoverageTerms.ToHashSet(StringComparer.Ordinal);

        AddHits(candidateIds);
        if (hits.Count < limit)
        {
            string widenedMatch = JoinFtsTerms(plan.CoverageTerms, " OR ");
            AddHits(ChunkCandidates(connection, widenedMatch, WidenedCandidateLimit));
        }

        hits.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            int byPath = string.CompareOrdinal(a.DisplayPath, b.DisplayPath);
            if (byPath != 0) return byPath;
            int byLine = a.Line.CompareTo(b.Line);
            return byLine != 0 ? byLine : string.CompareOrdinal(a.ChunkId, b.ChunkId);
        });

        if (hits.Count > limit)
            hits.RemoveRange(limit, hits.Count - limit);
        return hits;

        void AddHits(IReadOnlyList<string> ids)
        {
            foreach (string chunkId in ids)
            {
                if (!seenCandidateIds.Add(chunkId))
                    continue;
                if (!_chunksById.TryGetValue(chunkId, out TextChunk? chunk))
                    continue;
                if (!allowedKinds.Contains(chunk.ContentKind))
                    continue;
                if (excludeTests && chunk.IsTest)
                    continue;

                tokens.Clear();
                CodeTokenizer.Tokenize(chunk.RawText, tokens);
                double score = 0.0;
                int matchedCoverage = 0;
                foreach (string term in plan.DistinctTerms)
                {
                    int tf = CountOccurrences(tokens, term);
                    if (tf == 0)
                        continue;
                    if (coverageTermSet.Contains(term))
                        matchedCoverage++;
                    score += Bm25.TermScore(
                        Bm25.Idf(_documentCount, documentFrequency[term]),
                        tf,
                        chunk.DocLen,
                        _avgdl);
                }

                if (matchedCoverage < plan.RequiredCoverage || score <= 0.0)
                    continue;

                bool hasTokenPhrase = ContainsTokenPhrase(chunk, plan.QueryTokens);
                if (plan.RequiresTokenPhrase && !hasTokenPhrase)
                    continue;

                BestLine bestLine = BestLineAndSnippet(chunk, coverageTermSet, plan.QueryTokens);
                if (bestLine.DistinctTermCount < plan.RequiredLineCoverage)
                    continue;

                if (hasTokenPhrase)
                    score *= TokenPhraseBoost;

                ContentSymbolSpan? symbol = BestContainingSymbol(chunk.SourceId, bestLine.Line);
                hits.Add(new TextContentSearchHit(
                    chunk.SourceId,
                    chunk.ChunkId,
                    chunk.ContentKind,
                    chunk.Path,
                    chunk.Url,
                    chunk.DisplayPath,
                    chunk.Language,
                    score,
                    bestLine.Line,
                    chunk.LineStart,
                    chunk.LineEnd,
                    chunk.ByteStart,
                    chunk.ByteEnd,
                    bestLine.Snippet,
                    chunk.SourceBytes,
                    symbol?.SymbolId ?? chunk.ContainingSymbolId,
                    symbol?.Name ?? chunk.ContainingSymbolName));
            }
        }
    }

    public IReadOnlyList<TextContentSearchHit> Materialize(
        IReadOnlyCollection<string> chunkIds,
        IReadOnlyCollection<string> contentKinds,
        bool excludeTests = false)
    {
        if (chunkIds.Count == 0 || contentKinds.Count == 0 || _chunksById.Count == 0)
            return Array.Empty<TextContentSearchHit>();

        var allowedKinds = new HashSet<string>(contentKinds.Where(static kind => !string.IsNullOrWhiteSpace(kind)),
            StringComparer.Ordinal);
        if (allowedKinds.Count == 0)
            return Array.Empty<TextContentSearchHit>();

        var hits = new List<TextContentSearchHit>(chunkIds.Count);
        var seenChunkIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string chunkId in chunkIds)
        {
            if (!seenChunkIds.Add(chunkId) || !_chunksById.TryGetValue(chunkId, out TextChunk? chunk))
                continue;
            if (!allowedKinds.Contains(chunk.ContentKind) || excludeTests && chunk.IsTest)
                continue;

            ContentSymbolSpan? symbol = BestContainingSymbol(chunk.SourceId, chunk.LineStart);
            hits.Add(new TextContentSearchHit(
                chunk.SourceId,
                chunk.ChunkId,
                chunk.ContentKind,
                chunk.Path,
                chunk.Url,
                chunk.DisplayPath,
                chunk.Language,
                Score: 0.0,
                chunk.LineStart,
                chunk.LineStart,
                chunk.LineEnd,
                chunk.ByteStart,
                chunk.ByteEnd,
                SemanticSnippet(chunk.RawText),
                chunk.SourceBytes,
                symbol?.SymbolId ?? chunk.ContainingSymbolId,
                symbol?.Name ?? chunk.ContainingSymbolName));
        }

        return hits;
    }

    private static ContentMeta ReadMeta(SqliteConnection connection, string absPath)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT schema_version, workspace_revision FROM content_meta LIMIT 2;";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw MalformedMeta(absPath, "no meta row");

            int schemaVersion = checked((int)ReadInt64(reader, 0, absPath, "schema_version"));
            long? workspaceRevision = reader.IsDBNull(1) ? null : ReadInt64(reader, 1, absPath, "workspace_revision");
            if (reader.Read())
                throw MalformedMeta(absPath, "multiple meta rows");
            return new ContentMeta(schemaVersion, workspaceRevision);
        }
        catch (SqliteException ex)
        {
            throw MalformedMeta(absPath, ex.Message, ex);
        }
        catch (InvalidCastException ex)
        {
            throw MalformedMeta(absPath, ex.Message, ex);
        }
        catch (OverflowException ex)
        {
            throw MalformedMeta(absPath, ex.Message, ex);
        }
    }

    private static void EnsureSchema(SqliteConnection connection, string absPath)
    {
        EnsureTable(connection, absPath, "content_sources");
        EnsureTable(connection, absPath, "content_chunks");
        EnsureTable(connection, absPath, "content_symbol_spans");
        EnsureTable(connection, absPath, "content_fts");
        EnsureTable(connection, absPath, "content_meta");
        EnsureColumns(connection, absPath, "content_chunks",
            "chunk_id", "source_id", "content_kind", "path", "url", "display_path", "language",
            "line_start", "line_end", "byte_start", "byte_end", "raw_text", "doc_len", "is_test",
            "source_bytes", "containing_symbol_id", "containing_symbol_name");
        EnsureColumns(connection, absPath, "content_symbol_spans",
            "source_id", "symbol_id", "symbol_name", "path", "start_line", "end_line");
    }

    private static IReadOnlyList<TextChunk> ReadChunks(SqliteConnection connection, string absPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunk_id, source_id, content_kind, path, url, display_path, language,
                   line_start, line_end, byte_start, byte_end, raw_text, doc_len, is_test,
                   source_bytes, containing_symbol_id, containing_symbol_name
            FROM content_chunks
            ORDER BY display_path, line_start, chunk_id;
            """;
        using var reader = command.ExecuteReader();
        var chunks = new List<TextChunk>();
        while (reader.Read())
        {
            int docLen = checked((int)ReadInt64(reader, 12, absPath, "doc_len"));
            if (docLen < 0)
                throw MalformedChunk(absPath, "doc_len is negative");

            chunks.Add(new TextChunk(
                RequiredString(reader, 0, absPath, "chunk_id"),
                RequiredString(reader, 1, absPath, "source_id"),
                RequiredString(reader, 2, absPath, "content_kind"),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                RequiredString(reader, 5, absPath, "display_path"),
                RequiredString(reader, 6, absPath, "language"),
                checked((int)ReadInt64(reader, 7, absPath, "line_start")),
                checked((int)ReadInt64(reader, 8, absPath, "line_end")),
                ReadInt64(reader, 9, absPath, "byte_start"),
                ReadInt64(reader, 10, absPath, "byte_end"),
                RequiredString(reader, 11, absPath, "raw_text"),
                docLen,
                ReadInt64(reader, 13, absPath, "is_test") != 0,
                ReadInt64(reader, 14, absPath, "source_bytes"),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16)));
        }

        return chunks;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ContentSymbolSpan>> ReadSymbolSpans(
        SqliteConnection connection,
        string absPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_id, symbol_id, symbol_name, start_line, end_line
            FROM content_symbol_spans
            ORDER BY source_id, start_line, end_line, symbol_id;
            """;
        using var reader = command.ExecuteReader();
        var bySource = new Dictionary<string, List<ContentSymbolSpan>>(StringComparer.Ordinal);
        while (reader.Read())
        {
            string sourceId = RequiredString(reader, 0, absPath, "source_id");
            if (!bySource.TryGetValue(sourceId, out List<ContentSymbolSpan>? spans))
            {
                spans = new List<ContentSymbolSpan>();
                bySource[sourceId] = spans;
            }

            spans.Add(new ContentSymbolSpan(
                RequiredString(reader, 1, absPath, "symbol_id"),
                RequiredString(reader, 2, absPath, "symbol_name"),
                checked((int)ReadInt64(reader, 3, absPath, "start_line")),
                checked((int)ReadInt64(reader, 4, absPath, "end_line"))));
        }

        return bySource.ToDictionary(
            static kv => kv.Key,
            static kv => (IReadOnlyList<ContentSymbolSpan>)kv.Value,
            StringComparer.Ordinal);
    }

    private static int CountChunksMatching(SqliteConnection connection, string match)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM content_fts WHERE body MATCH $q;";
        command.Parameters.AddWithValue("$q", match);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> ChunkCandidates(SqliteConnection connection, string match, int? limit = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = limit is null
            ? "SELECT chunk_id FROM content_fts WHERE body MATCH $q;"
            : "SELECT chunk_id FROM content_fts WHERE body MATCH $q ORDER BY rank LIMIT $limit;";
        command.Parameters.AddWithValue("$q", match);
        if (limit is not null)
            command.Parameters.AddWithValue("$limit", limit.Value);
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static BestLine BestLineAndSnippet(
        TextChunk chunk,
        HashSet<string> queryTerms,
        IReadOnlyList<string> queryTokens)
    {
        string[] lines = SplitLines(chunk.RawText);
        int bestIndex = 0;
        bool bestHasPhrase = false;
        int bestMatches = -1;
        int bestTokenHits = -1;
        var tokens = new List<string>(32);
        var lineTerms = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            tokens.Clear();
            lineTerms.Clear();
            CodeTokenizer.Tokenize(lines[i], tokens);
            int tokenHits = 0;
            foreach (string token in tokens)
            {
                if (queryTerms.Contains(token))
                {
                    lineTerms.Add(token);
                    tokenHits++;
                }
            }

            bool hasPhrase = queryTokens.Count > 1 && ContainsTokenPhrase(tokens, queryTokens);
            if (hasPhrase && !bestHasPhrase ||
                hasPhrase == bestHasPhrase &&
                (lineTerms.Count > bestMatches || (lineTerms.Count == bestMatches && tokenHits > bestTokenHits)))
            {
                bestHasPhrase = hasPhrase;
                bestMatches = lineTerms.Count;
                bestTokenHits = tokenHits;
                bestIndex = i;
            }
        }

        int start = Math.Max(0, bestIndex - SnippetRadius);
        int end = Math.Min(lines.Length - 1, bestIndex + SnippetRadius);
        string snippet = string.Join('\n', lines[start..(end + 1)]);
        return new BestLine(chunk.LineStart + bestIndex, snippet, bestMatches);
    }

    private static bool ContainsTokenPhrase(TextChunk chunk, IReadOnlyList<string> queryTokens)
    {
        if (queryTokens.Count < 2)
            return false;

        var tokens = new List<string>(32);
        foreach (string line in SplitLines(chunk.RawText))
        {
            tokens.Clear();
            CodeTokenizer.Tokenize(line, tokens);
            if (tokens.Count < queryTokens.Count)
                continue;
            if (ContainsTokenPhrase(tokens, queryTokens))
                return true;
        }

        return false;
    }

    private static bool ContainsTokenPhrase(IReadOnlyList<string> lineTokens, IReadOnlyList<string> queryTokens)
    {
        for (int start = 0; start <= lineTokens.Count - queryTokens.Count; start++)
        {
            bool matches = true;
            for (int offset = 0; offset < queryTokens.Count; offset++)
            {
                if (!string.Equals(lineTokens[start + offset], queryTokens[offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return true;
        }

        return false;
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string SemanticSnippet(string text)
    {
        string[] lines = SplitLines(text);
        int count = Math.Min(lines.Length, (SnippetRadius * 2) + 1);
        return string.Join('\n', lines[..count]);
    }

    private static string JoinFtsTerms(IReadOnlyList<string> terms, string separator)
    {
        var quoted = new string[terms.Count];
        for (int i = 0; i < terms.Count; i++)
            quoted[i] = QuoteFts(terms[i]);
        return string.Join(separator, quoted);
    }

    private ContentSymbolSpan? BestContainingSymbol(string sourceId, int line)
    {
        if (!_spansBySourceId.TryGetValue(sourceId, out IReadOnlyList<ContentSymbolSpan>? spans))
            return null;

        ContentSymbolSpan? best = null;
        int bestWidth = int.MaxValue;
        foreach (ContentSymbolSpan span in spans)
        {
            if (line < span.StartLine || line > span.EndLine)
                continue;

            int width = span.EndLine - span.StartLine;
            if (best is null || width < bestWidth)
            {
                best = span;
                bestWidth = width;
            }
        }

        return best;
    }

    private static int CountOccurrences(List<string> tokens, string term)
    {
        int count = 0;
        foreach (string token in tokens)
            if (string.Equals(token, term, StringComparison.Ordinal))
                count++;
        return count;
    }

    private static string QuoteFts(string term) => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void EnsureTable(SqliteConnection connection, string absPath, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table', 'view') AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        if (command.ExecuteScalar() is null)
            throw new InvalidOperationException($"content.db at '{absPath}' is missing required table '{tableName}'. Rebuild the content corpus.");
    }

    private static void EnsureColumns(SqliteConnection connection, string absPath, string tableName, params string[] requiredColumns)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            columns.Add(reader.GetString(1));

        foreach (string column in requiredColumns)
        {
            if (!columns.Contains(column))
                throw new InvalidOperationException(
                    $"content.db at '{absPath}' table '{tableName}' is missing required column '{column}'. " +
                    "Rebuild the content corpus.");
        }
    }

    private static string RequiredString(SqliteDataReader reader, int ordinal, string absPath, string column)
    {
        if (reader.IsDBNull(ordinal))
            throw MalformedChunk(absPath, $"{column} is null");
        return reader.GetString(ordinal);
    }

    private static long ReadInt64(SqliteDataReader reader, int ordinal, string absPath, string column)
    {
        if (reader.IsDBNull(ordinal))
            throw MalformedMeta(absPath, $"{column} is null");
        return reader.GetInt64(ordinal);
    }

    private static InvalidOperationException MalformedMeta(string absPath, string detail, Exception? inner = null) =>
        new($"content.db at '{absPath}' has malformed meta: {detail}. Rebuild the content corpus.", inner);

    private static InvalidOperationException MalformedChunk(string absPath, string detail) =>
        new($"content.db at '{absPath}' has malformed content_chunks data: {detail}. Rebuild the content corpus.");

    private sealed record ContentMeta(int SchemaVersion, long? WorkspaceRevision);

    private sealed record BestLine(int Line, string Snippet, int DistinctTermCount);

    private sealed record ContentSymbolSpan(string SymbolId, string Name, int StartLine, int EndLine);

    private sealed record TextChunk(
        string ChunkId,
        string SourceId,
        string ContentKind,
        string? Path,
        string? Url,
        string DisplayPath,
        string Language,
        int LineStart,
        int LineEnd,
        long ByteStart,
        long ByteEnd,
        string RawText,
        int DocLen,
        bool IsTest,
        long SourceBytes,
        string? ContainingSymbolId,
        string? ContainingSymbolName);
}
