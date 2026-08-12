using System.Globalization;
using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;

namespace Miller.Indexing;

public sealed class FtsTextContentSearchIndex : ITextContentSearchIndex, ISemanticContentLookup
{
    private const int SnippetRadius = 2;
    private const int WidenedCandidateLimit = 5000;
    private const int RawTextBatchSize = 400;
    private const double TokenPhraseBoost = 2.5;

    private readonly string _connectionString;
    private readonly int _documentCount;
    private readonly object _statisticsGate = new();
    private readonly Action<FtsTextSearchQueryObservation>? _queryObserver;
    private double? _averageDocumentLength;

    private FtsTextContentSearchIndex(
        string connectionString,
        int documentCount,
        long revision,
        Action<FtsTextSearchQueryObservation>? queryObserver)
    {
        _connectionString = connectionString;
        _documentCount = documentCount;
        Revision = revision;
        _queryObserver = queryObserver;
    }

    public int DocumentCount => _documentCount;

    public long Revision { get; }

    public static FtsTextContentSearchIndex Open(string contentDbPath, long expectedRevision) =>
        Open(contentDbPath, expectedRevision, queryObserver: null);

    internal static FtsTextContentSearchIndex Open(
        string contentDbPath,
        long expectedRevision,
        Action<FtsTextSearchQueryObservation>? queryObserver)
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

        long metadataStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        ContentMeta meta = ReadMeta(connection, absPath);
        ObserveOpen(FtsTextSearchQueryFamily.OpenMetadata, rows: 1, metadataStarted);
        if (meta.SchemaVersion != ContentCorpusSchema.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"content.db at '{absPath}' has schema_version {meta.SchemaVersion}; " +
                $"this build expects {ContentCorpusSchema.SchemaVersion}. Rebuild the content corpus.");
        }

        if (meta.WorkspaceRevision is null)
        {
            throw new InvalidOperationException(
                $"content.db at '{absPath}' contains imports only; run workspace refresh " +
                "to build workspace text before source, docs, or config search.");
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
            meta.ChunkCount,
            meta.WorkspaceRevision.GetValueOrDefault(),
            queryObserver);
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

        long metadataStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        ContentMeta meta = ReadMeta(connection, absPath);
        ObserveOpen(FtsTextSearchQueryFamily.OpenMetadata, rows: 1, metadataStarted);
        if (meta.SchemaVersion != ContentCorpusSchema.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"content.db at '{absPath}' has schema_version {meta.SchemaVersion}; " +
                $"this build expects {ContentCorpusSchema.SchemaVersion}.");
        }

        EnsureSchema(connection, absPath);
        return new FtsTextContentSearchIndex(
            connectionString,
            meta.ChunkCount,
            meta.WorkspaceRevision.GetValueOrDefault(),
            queryObserver: null);
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
        if (contentKinds.Count == 0 || limit <= 0 || _documentCount == 0)
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
        long connectionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        connection.Open();
        Observe(FtsTextSearchQueryFamily.ConnectionOpen, rows: 0, connectionStarted);
        double averageDocumentLength = AverageDocumentLength(connection);

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string term in plan.DistinctTerms)
        {
            long frequencyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            int frequency = CountChunksMatching(connection, QuoteFts(term));
            documentFrequency[term] = frequency;
            Observe(FtsTextSearchQueryFamily.DocumentFrequency, frequency, frequencyStarted);
        }

        string strictMatch = JoinFtsTerms(plan.CoverageTerms, " AND ");
        long strictStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        IReadOnlyList<TextCandidate> candidates = ChunkCandidates(
            connection,
            strictMatch,
            WidenedCandidateLimit,
            connection.DataSource);
        Observe(FtsTextSearchQueryFamily.StrictCandidates, candidates.Count, strictStarted);
        var pendingHits = new List<PendingTextHit>();
        var seenCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        var coverageTermSet = plan.CoverageTerms.ToHashSet(StringComparer.Ordinal);

        AddHits(candidates);
        if (pendingHits.Count < limit)
        {
            string widenedMatch = JoinFtsTerms(plan.CoverageTerms, " OR ");
            long widenedStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            IReadOnlyList<TextCandidate> widenedCandidates = ChunkCandidates(
                connection,
                widenedMatch,
                WidenedCandidateLimit,
                connection.DataSource);
            Observe(FtsTextSearchQueryFamily.WidenedCandidates, widenedCandidates.Count, widenedStarted);
            AddHits(widenedCandidates);
        }

        long orderingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        pendingHits.Sort(static (a, b) =>
        {
            int byScore = b.Hit.Score.CompareTo(a.Hit.Score);
            if (byScore != 0) return byScore;
            int byPath = string.CompareOrdinal(a.Hit.DisplayPath, b.Hit.DisplayPath);
            if (byPath != 0) return byPath;
            int byLine = a.Hit.Line.CompareTo(b.Hit.Line);
            return byLine != 0 ? byLine : string.CompareOrdinal(a.Hit.ChunkId, b.Hit.ChunkId);
        });

        if (pendingHits.Count > limit)
            pendingHits.RemoveRange(limit, pendingHits.Count - limit);
        TimeSpan orderingElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(orderingStarted);

        long spansStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        IReadOnlyDictionary<string, IReadOnlyList<ContentSymbolSpan>> spansBySourceId = ReadSymbolSpans(
            connection,
            pendingHits.Select(static hit => hit.Hit.SourceId).Distinct(StringComparer.Ordinal).ToArray(),
            connection.DataSource);
        Observe(
            FtsTextSearchQueryFamily.SymbolSpanHydration,
            spansBySourceId.Sum(static entry => entry.Value.Count),
            spansStarted);
        var hits = new List<TextContentSearchHit>(pendingHits.Count);
        long symbolStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var mappedSymbols = new ContentSymbolSpan?[pendingHits.Count];
        for (int i = 0; i < pendingHits.Count; i++)
        {
            PendingTextHit pending = pendingHits[i];
            mappedSymbols[i] = BestContainingSymbol(
                spansBySourceId,
                pending.Hit.SourceId,
                pending.Hit.Line);
        }
        Observe(FtsTextSearchQueryFamily.SymbolMapping, pendingHits.Count, symbolStarted);
        long resultStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < pendingHits.Count; i++)
            hits.Add(MaterializeHit(pendingHits[i], mappedSymbols[i]));
        Observe(FtsTextSearchQueryFamily.ResultConstruction, hits.Count, resultStarted);
        Observe(FtsTextSearchQueryFamily.FinalOrdering, hits.Count, orderingElapsed);
        return hits;

        void AddHits(IReadOnlyList<TextCandidate> candidates)
        {
            long filteringStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            TextCandidate[] eligibleCandidates = candidates
                .Where(candidate =>
                {
                    if (!seenCandidateIds.Add(candidate.ChunkId))
                        return false;
                    return allowedKinds.Contains(candidate.ContentKind)
                        && (!excludeTests || !candidate.IsTest);
                })
                .ToArray();
            Observe(FtsTextSearchQueryFamily.CandidateFiltering, eligibleCandidates.Length, filteringStarted);

            long narrowScoringStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var scoredCandidates = new List<ScoredTextCandidate>(eligibleCandidates.Length);
            foreach (TextCandidate candidate in eligibleCandidates)
            {
                double score = 0.0;
                int matchedCoverage = 0;
                foreach (string term in plan.DistinctTerms)
                {
                    int tf = FtsSymbolSearchIndex.CountTokenOccurrences(candidate.TokenBody, term);
                    if (tf == 0)
                        continue;
                    if (coverageTermSet.Contains(term))
                        matchedCoverage++;
                    score += Bm25.TermScore(
                        Bm25.Idf(_documentCount, documentFrequency[term]),
                        tf,
                        candidate.DocLen,
                        averageDocumentLength);
                }

                if (matchedCoverage >= plan.RequiredCoverage && score > 0.0)
                    scoredCandidates.Add(new ScoredTextCandidate(candidate.ChunkId, score));
            }
            Observe(
                FtsTextSearchQueryFamily.NarrowTokenScoring,
                eligibleCandidates.Length,
                narrowScoringStarted);

            foreach (ScoredTextCandidate[] batch in scoredCandidates.Chunk(RawTextBatchSize))
            {
                long hydrationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                IReadOnlyDictionary<string, TextChunk> chunksById = ReadChunksById(
                    connection,
                    batch.Select(static candidate => candidate.ChunkId).ToArray(),
                    connection.DataSource);
                Observe(FtsTextSearchQueryFamily.FullHydration, chunksById.Count, hydrationStarted);
                long scoringStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                int hitCountBefore = pendingHits.Count;
                var subphases = new ScoringSubphases();
                foreach (ScoredTextCandidate candidate in batch)
                    AddHit(candidate, chunksById, ref subphases);
                TimeSpan scoringElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(scoringStarted);
                Observe(
                    FtsTextSearchQueryFamily.RawTextAnalysis,
                    subphases.RawTextRows,
                    subphases.RawTextElapsed);
                Observe(FtsTextSearchQueryFamily.Scoring, pendingHits.Count - hitCountBefore, scoringElapsed);
            }
        }

        void AddHit(
            ScoredTextCandidate candidate,
            IReadOnlyDictionary<string, TextChunk> chunksById,
            ref ScoringSubphases subphases)
        {
            if (!chunksById.TryGetValue(candidate.ChunkId, out TextChunk? chunk))
                return;

            long rawTextStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            RawTextAnalysis analysis = AnalyzeRawText(chunk, coverageTermSet, plan.QueryTokens);
            subphases.CompleteRawText(rawTextStarted);
            if (plan.RequiresTokenPhrase && !analysis.HasTokenPhrase)
                return;

            BestLine bestLine = analysis.BestLine;
            if (bestLine.DistinctTermCount < plan.RequiredLineCoverage)
                return;

            double score = analysis.HasTokenPhrase ? candidate.Score * TokenPhraseBoost : candidate.Score;
            pendingHits.Add(new PendingTextHit(new TextContentSearchHit(
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
                chunk.ContainingSymbolId,
                chunk.ContainingSymbolName,
                chunk.ContentHash)));
        }

        static TextContentSearchHit MaterializeHit(PendingTextHit pending, ContentSymbolSpan? symbol)
            => symbol is null
                ? pending.Hit
                : pending.Hit with
                {
                    ContainingSymbolId = symbol.SymbolId,
                    ContainingSymbolName = symbol.Name,
                };
    }

    private void Observe(FtsTextSearchQueryFamily family, int rows, long startedAt)
        => Observe(family, rows, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));

    private void Observe(FtsTextSearchQueryFamily family, int rows, TimeSpan elapsed)
    {
        var observation = new FtsTextSearchQueryObservation(family, rows, elapsed);
        FtsTextSearchQueryTelemetryCollector.Current?.Record(observation);
        _queryObserver?.Invoke(observation);
    }

    private static void ObserveOpen(FtsTextSearchQueryFamily family, int rows, long startedAt)
    {
        var observation = new FtsTextSearchQueryObservation(
            family,
            rows,
            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
        FtsTextSearchQueryTelemetryCollector.Current?.Record(observation);
    }

    public IReadOnlyList<TextContentSearchHit> Materialize(
        IReadOnlyCollection<string> chunkIds,
        IReadOnlyCollection<string> contentKinds,
        bool excludeTests = false)
    {
        if (chunkIds.Count == 0 || contentKinds.Count == 0 || _documentCount == 0)
            return Array.Empty<TextContentSearchHit>();

        var allowedKinds = new HashSet<string>(contentKinds.Where(static kind => !string.IsNullOrWhiteSpace(kind)),
            StringComparer.Ordinal);
        if (allowedKinds.Count == 0)
            return Array.Empty<TextContentSearchHit>();

        string[] requestedIds = chunkIds.Distinct(StringComparer.Ordinal).ToArray();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        IReadOnlyDictionary<string, TextChunk> chunksById = ReadChunksById(
            connection,
            requestedIds,
            connection.DataSource,
            allowedKinds,
            excludeTests);
        TextChunk[] eligibleChunks = requestedIds
            .Select(chunkId => chunksById.GetValueOrDefault(chunkId))
            .Where(static chunk => chunk is not null)
            .Select(static chunk => chunk!)
            .ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<ContentSymbolSpan>> spansBySourceId = ReadSymbolSpans(
            connection,
            eligibleChunks.Select(static chunk => chunk.SourceId).Distinct(StringComparer.Ordinal).ToArray(),
            connection.DataSource);
        var hits = new List<TextContentSearchHit>(eligibleChunks.Length);
        foreach (TextChunk chunk in eligibleChunks)
        {
            ContentSymbolSpan? symbol = BestContainingSymbol(spansBySourceId, chunk.SourceId, chunk.LineStart);
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
                symbol?.Name ?? chunk.ContainingSymbolName,
                chunk.ContentHash));
        }

        return hits;
    }

    private static ContentMeta ReadMeta(SqliteConnection connection, string absPath)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT schema_version, workspace_revision, chunk_count FROM content_meta LIMIT 2;";
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw MalformedMeta(absPath, "no meta row");

            int schemaVersion = checked((int)ReadInt64(reader, 0, absPath, "schema_version"));
            long? workspaceRevision = reader.IsDBNull(1) ? null : ReadInt64(reader, 1, absPath, "workspace_revision");
            int chunkCount = checked((int)ReadInt64(reader, 2, absPath, "chunk_count"));
            if (chunkCount < 0)
                throw MalformedMeta(absPath, "chunk_count is negative");
            if (reader.Read())
                throw MalformedMeta(absPath, "multiple meta rows");
            return new ContentMeta(schemaVersion, workspaceRevision, chunkCount);
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

    private static IReadOnlyDictionary<string, TextChunk> ReadChunksById(
        SqliteConnection connection,
        IReadOnlyList<string> chunkIds,
        string absPath,
        IReadOnlySet<string>? allowedKinds = null,
        bool excludeTests = false)
    {
        var chunks = new Dictionary<string, TextChunk>(StringComparer.Ordinal);
        foreach (string[] batch in chunkIds.Chunk(400))
        {
            using SqliteCommand command = connection.CreateCommand();
            var parameters = new string[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                parameters[i] = "$chunk" + i.ToString(CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(parameters[i], batch[i]);
            }
            string kindPredicate = string.Empty;
            if (allowedKinds is not null)
            {
                string[] kinds = allowedKinds.Order(StringComparer.Ordinal).ToArray();
                var kindParameters = new string[kinds.Length];
                for (int i = 0; i < kinds.Length; i++)
                {
                    kindParameters[i] = "$kind" + i.ToString(CultureInfo.InvariantCulture);
                    command.Parameters.AddWithValue(kindParameters[i], kinds[i]);
                }
                kindPredicate = $" AND c.content_kind IN ({string.Join(", ", kindParameters)})";
            }
            string testPredicate = excludeTests ? " AND c.is_test = 0" : string.Empty;
            command.CommandText = $"""
                SELECT c.chunk_id, c.source_id, c.content_kind, c.path, c.url, c.display_path, c.language,
                       c.line_start, c.line_end, c.byte_start, c.byte_end, c.raw_text, c.doc_len, c.is_test,
                       c.source_bytes, c.containing_symbol_id, c.containing_symbol_name, s.content_hash
                FROM content_chunks c
                JOIN content_sources s ON s.source_id = c.source_id
                WHERE c.chunk_id IN ({string.Join(", ", parameters)}){kindPredicate}{testPredicate}
                ORDER BY c.chunk_id;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                int docLen = checked((int)ReadInt64(reader, 12, absPath, "doc_len"));
                if (docLen < 0)
                    throw MalformedChunk(absPath, "doc_len is negative");

                var chunk = new TextChunk(
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
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    RequiredString(reader, 17, absPath, "content_hash"));
                chunks[chunk.ChunkId] = chunk;
            }
        }
        return chunks;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ContentSymbolSpan>> ReadSymbolSpans(
        SqliteConnection connection,
        IReadOnlyList<string> sourceIds,
        string absPath)
    {
        var bySource = new Dictionary<string, List<ContentSymbolSpan>>(StringComparer.Ordinal);
        foreach (string[] batch in sourceIds.Chunk(RawTextBatchSize))
        {
            using SqliteCommand command = connection.CreateCommand();
            var parameters = new string[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                parameters[i] = "$source" + i.ToString(CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(parameters[i], batch[i]);
            }
            command.CommandText = $"""
                SELECT source_id, symbol_id, symbol_name, start_line, end_line
                FROM content_symbol_spans
                WHERE source_id IN ({string.Join(", ", parameters)})
                ORDER BY source_id, start_line, end_line, symbol_id;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string sourceId = RequiredString(reader, 0, absPath, "source_id");
                if (!bySource.TryGetValue(sourceId, out List<ContentSymbolSpan>? spans))
                {
                    spans = [];
                    bySource[sourceId] = spans;
                }

                spans.Add(new ContentSymbolSpan(
                    RequiredString(reader, 1, absPath, "symbol_id"),
                    RequiredString(reader, 2, absPath, "symbol_name"),
                    checked((int)ReadInt64(reader, 3, absPath, "start_line")),
                    checked((int)ReadInt64(reader, 4, absPath, "end_line"))));
            }
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

    private static IReadOnlyList<TextCandidate> ChunkCandidates(
        SqliteConnection connection,
        string match,
        int limit,
        string absPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.chunk_id, f.body, c.content_kind, c.doc_len, c.is_test
            FROM content_fts f
            JOIN content_chunks c ON c.chunk_id = f.chunk_id
            WHERE f.body MATCH $q
            ORDER BY f.rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$q", match);
        command.Parameters.AddWithValue("$limit", limit);
        var candidates = new List<TextCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            int docLen = checked((int)ReadInt64(reader, 3, absPath, "doc_len"));
            if (docLen < 0)
                throw MalformedChunk(absPath, "doc_len is negative");
            candidates.Add(new TextCandidate(
                RequiredString(reader, 0, absPath, "chunk_id"),
                RequiredString(reader, 1, absPath, "body"),
                RequiredString(reader, 2, absPath, "content_kind"),
                docLen,
                ReadInt64(reader, 4, absPath, "is_test") != 0));
        }
        return candidates;
    }

    private double AverageDocumentLength(SqliteConnection connection)
    {
        lock (_statisticsGate)
        {
            if (_averageDocumentLength is { } lockedCached)
                return lockedCached;

            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(AVG(doc_len), 0.0) FROM content_chunks;";
            double average = Convert.ToDouble(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            Observe(FtsTextSearchQueryFamily.AverageDocumentLength, rows: 1, startedAt);
            _averageDocumentLength = average;
            return average;
        }
    }

    private static RawTextAnalysis AnalyzeRawText(
        TextChunk chunk,
        HashSet<string> queryTerms,
        IReadOnlyList<string> queryTokens)
    {
        string[] lines = SplitLines(chunk.RawText);
        int bestIndex = 0;
        bool bestHasPhrase = false;
        int bestMatches = -1;
        int bestTokenHits = -1;
        bool hasTokenPhrase = false;
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
            hasTokenPhrase |= hasPhrase;
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
        return new RawTextAnalysis(
            hasTokenPhrase,
            new BestLine(chunk.LineStart + bestIndex, snippet, bestMatches));
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

    private static ContentSymbolSpan? BestContainingSymbol(
        IReadOnlyDictionary<string, IReadOnlyList<ContentSymbolSpan>> spansBySourceId,
        string sourceId,
        int line)
    {
        if (!spansBySourceId.TryGetValue(sourceId, out IReadOnlyList<ContentSymbolSpan>? spans))
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

    private sealed record ContentMeta(int SchemaVersion, long? WorkspaceRevision, int ChunkCount);

    private struct ScoringSubphases
    {
        internal long RawTextElapsedTicks;
        internal int RawTextRows;

        internal readonly TimeSpan RawTextElapsed => TimeSpan.FromTicks(RawTextElapsedTicks);

        internal void CompleteRawText(long startedAt)
        {
            RawTextElapsedTicks += System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).Ticks;
            RawTextRows++;
        }
    }

    private sealed record BestLine(int Line, string Snippet, int DistinctTermCount);

    private sealed record RawTextAnalysis(bool HasTokenPhrase, BestLine BestLine);

    private readonly record struct TextCandidate(
        string ChunkId,
        string TokenBody,
        string ContentKind,
        int DocLen,
        bool IsTest);

    private readonly record struct ScoredTextCandidate(string ChunkId, double Score);

    private sealed record PendingTextHit(TextContentSearchHit Hit);

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
        string? ContainingSymbolName,
        string ContentHash);
}

internal enum FtsTextSearchQueryFamily
{
    OpenMetadata,
    OpenChunkMetadata,
    OpenSymbolSpans,
    ConnectionOpen,
    AverageDocumentLength,
    DocumentFrequency,
    StrictCandidates,
    WidenedCandidates,
    CandidateFiltering,
    NarrowTokenScoring,
    FullHydration,
    SymbolSpanHydration,
    RawTextAnalysis,
    SymbolMapping,
    ResultConstruction,
    Scoring,
    FinalOrdering,
}

internal readonly record struct FtsTextSearchQueryObservation(
    FtsTextSearchQueryFamily Family,
    int Rows,
    TimeSpan Elapsed);

internal readonly record struct FtsTextSearchQueryFamilyMeasurement(
    long CallCount,
    long ElapsedTicks,
    long ReturnedRowCount);

internal sealed record FtsTextSearchQueryMeasurementSnapshot(
    FtsTextSearchQueryFamilyMeasurement OpenMetadata,
    FtsTextSearchQueryFamilyMeasurement OpenChunkMetadata,
    FtsTextSearchQueryFamilyMeasurement OpenSymbolSpans,
    FtsTextSearchQueryFamilyMeasurement ConnectionOpen,
    FtsTextSearchQueryFamilyMeasurement AverageDocumentLength,
    FtsTextSearchQueryFamilyMeasurement DocumentFrequency,
    FtsTextSearchQueryFamilyMeasurement StrictCandidates,
    FtsTextSearchQueryFamilyMeasurement WidenedCandidates,
    FtsTextSearchQueryFamilyMeasurement CandidateFiltering,
    FtsTextSearchQueryFamilyMeasurement NarrowTokenScoring,
    FtsTextSearchQueryFamilyMeasurement FullHydration,
    FtsTextSearchQueryFamilyMeasurement SymbolSpanHydration,
    FtsTextSearchQueryFamilyMeasurement RawTextAnalysis,
    FtsTextSearchQueryFamilyMeasurement SymbolMapping,
    FtsTextSearchQueryFamilyMeasurement ResultConstruction,
    FtsTextSearchQueryFamilyMeasurement Scoring,
    FtsTextSearchQueryFamilyMeasurement FinalOrdering);

internal sealed class FtsTextSearchQueryTelemetryCollector
{
    private static readonly AsyncLocal<FtsTextSearchQueryTelemetryCollector?> CurrentCollector = new();
    private readonly long[] _calls = new long[Enum.GetValues<FtsTextSearchQueryFamily>().Length];
    private readonly long[] _elapsedTicks = new long[Enum.GetValues<FtsTextSearchQueryFamily>().Length];
    private readonly long[] _rows = new long[Enum.GetValues<FtsTextSearchQueryFamily>().Length];

    internal static FtsTextSearchQueryMeasurementSnapshot EmptySnapshot { get; } =
        new(
            default, default, default, default, default, default, default, default,
            default, default, default, default, default, default, default, default, default);

    internal static FtsTextSearchQueryTelemetryCollector? Current => CurrentCollector.Value;

    internal IDisposable Activate()
    {
        FtsTextSearchQueryTelemetryCollector? previous = CurrentCollector.Value;
        CurrentCollector.Value = this;
        return new Activation(previous);
    }

    internal void Record(FtsTextSearchQueryObservation observation)
    {
        int index = (int)observation.Family;
        Interlocked.Increment(ref _calls[index]);
        Interlocked.Add(ref _elapsedTicks[index], observation.Elapsed.Ticks);
        Interlocked.Add(ref _rows[index], observation.Rows);
    }

    internal FtsTextSearchQueryMeasurementSnapshot Snapshot() =>
        new(
            Family(FtsTextSearchQueryFamily.OpenMetadata),
            Family(FtsTextSearchQueryFamily.OpenChunkMetadata),
            Family(FtsTextSearchQueryFamily.OpenSymbolSpans),
            Family(FtsTextSearchQueryFamily.ConnectionOpen),
            Family(FtsTextSearchQueryFamily.AverageDocumentLength),
            Family(FtsTextSearchQueryFamily.DocumentFrequency),
            Family(FtsTextSearchQueryFamily.StrictCandidates),
            Family(FtsTextSearchQueryFamily.WidenedCandidates),
            Family(FtsTextSearchQueryFamily.CandidateFiltering),
            Family(FtsTextSearchQueryFamily.NarrowTokenScoring),
            Family(FtsTextSearchQueryFamily.FullHydration),
            Family(FtsTextSearchQueryFamily.SymbolSpanHydration),
            Family(FtsTextSearchQueryFamily.RawTextAnalysis),
            Family(FtsTextSearchQueryFamily.SymbolMapping),
            Family(FtsTextSearchQueryFamily.ResultConstruction),
            Family(FtsTextSearchQueryFamily.Scoring),
            Family(FtsTextSearchQueryFamily.FinalOrdering));

    private FtsTextSearchQueryFamilyMeasurement Family(FtsTextSearchQueryFamily family)
    {
        int index = (int)family;
        return new FtsTextSearchQueryFamilyMeasurement(
            Interlocked.Read(ref _calls[index]),
            Interlocked.Read(ref _elapsedTicks[index]),
            Interlocked.Read(ref _rows[index]));
    }

    private sealed class Activation(FtsTextSearchQueryTelemetryCollector? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentCollector.Value = previous;
        }
    }
}
