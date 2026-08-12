using Microsoft.Data.Sqlite;
using Miller.Core.Search;
using Miller.Core.Tokenization;

namespace Miller.Indexing;

/// <summary>
/// Read-only <see cref="ISymbolLookupIndex"/> backed by the on-disk <c>search.db</c> artifact that
/// <see cref="SearchIndexWriter"/> builds. Recall comes from SQLite FTS5 (a word arm over the exact
/// <c>CodeTokenizer</c> token stream, plus a collapsed-trigram arm for interior substrings); RANKING
/// stays in Miller's C# — candidates are re-scored with the shared <see cref="Bm25"/> math so the word
/// arm reproduces the in-memory <see cref="SymbolSearchProjection"/>'s top-N exactly.
///
/// <para><see cref="Open"/> reads only the metadata and validates the FTS tables. Symbol metadata stays on
/// disk and is fetched on demand for FTS candidates, exact symbol/file lookups, and summary inspect resolution.
/// <c>DocId</c> is stored explicitly in <c>search_symbols</c>; SQLite row order is not part of the artifact
/// contract. Each operation opens a short-lived read-only connection, so atomic <c>search.db</c> replacement by
/// the writer never races a held reader, and concurrent searches need no lock.</para>
/// </summary>
public sealed class FtsSymbolSearchIndex : ISymbolLookupIndex
{
    private readonly string _connectionString;
    private readonly double _avgdl;
    private readonly int _documentCount;
    private readonly Lazy<IReadOnlyList<string>> _paths;
    private readonly Lazy<IReadOnlySet<string>> _knownExtensions;
    private readonly Action<FtsSearchQueryObservation>? _queryObserver;

    private FtsSymbolSearchIndex(
        string connectionString,
        double avgdl,
        long revision,
        int documentCount,
        string? artifactId,
        Action<FtsSearchQueryObservation>? queryObserver)
    {
        _connectionString = connectionString;
        _avgdl = avgdl;
        Revision = revision;
        ArtifactId = artifactId;
        _documentCount = documentCount;
        _paths = new Lazy<IReadOnlyList<string>>(LoadPaths, LazyThreadSafetyMode.ExecutionAndPublication);
        _knownExtensions = new Lazy<IReadOnlySet<string>>(
            () => BuildKnownExtensions(_paths.Value),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _queryObserver = queryObserver;
    }

    /// <summary>The julie-extract revision this artifact was built from (freshness key for Phase 3 routing).</summary>
    public long Revision { get; }

    /// <summary>
    /// The <c>artifact_metadata.artifact_id</c> of the symbols.db this was built from, or null for a sidecar
    /// written before artifact stamping. Revision alone cannot identify a generation: a full-rebuild promote
    /// restarts julie's counter, so a revision-1 workspace that rebuilds lands on revision 1 again.
    /// </summary>
    public string? ArtifactId { get; }

    public int DocumentCount => _documentCount;

    public IReadOnlySet<string> KnownExtensions => _knownExtensions.Value;

    /// <summary>
    /// Open the <c>search.db</c> at <paramref name="searchDbPath"/>, validate its schema version, and load
    /// the corpus stats. Throws if the file is missing or schema-incompatible; production routing surfaces that
    /// as a visible sidecar freshness/configuration error unless the sidecar is explicitly disabled.
    /// </summary>
    public static FtsSymbolSearchIndex Open(string searchDbPath) => Open(searchDbPath, queryObserver: null);

    internal static FtsSymbolSearchIndex Open(
        string searchDbPath,
        Action<FtsSearchQueryObservation>? queryObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchDbPath);

        string absPath = Path.GetFullPath(searchDbPath);
        if (!File.Exists(absPath))
            throw new FileNotFoundException($"search.db not found at '{absPath}'.", absPath);

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        (long revision, int documentCount, double avgdl, string? artifactId) = ReadMeta(connection, absPath);
        ValidateFtsTables(connection);

        return new FtsSymbolSearchIndex(connectionString, avgdl, revision, documentCount, artifactId, queryObserver);
    }

    /// <summary>The meta artifact stamp, or null when the column predates artifact stamping. Read separately so
    /// a schema-version mismatch reports as one rather than as malformed meta.</summary>
    private static string? TryReadArtifactId(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT artifact_id FROM meta LIMIT 1;";
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static (long Revision, int DocumentCount, double Avgdl, string? ArtifactId) ReadMeta(
        SqliteConnection connection, string absPath)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT revision, doc_count, avgdl, schema_version FROM meta LIMIT 2;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw MalformedMeta(absPath, "no meta row");

            long revision = ReadInt64(reader, 0, absPath, "revision");
            int documentCount = checked((int)ReadInt64(reader, 1, absPath, "doc_count"));
            double avgdl = ReadDouble(reader, 2, absPath, "avgdl");
            int schemaVersion = checked((int)ReadInt64(reader, 3, absPath, "schema_version"));

            if (reader.Read())
                throw MalformedMeta(absPath, "multiple meta rows");
            if (documentCount < 0)
                throw MalformedMeta(absPath, "doc_count is negative");
            if (avgdl < 0.0)
                throw MalformedMeta(absPath, "avgdl is negative");

            if (schemaVersion != SearchIndexWriter.SchemaVersion)
                throw new InvalidOperationException(
                    $"search.db at '{absPath}' has schema_version {schemaVersion}; " +
                    $"this build expects {SearchIndexWriter.SchemaVersion}. Rebuild the search index.");

            reader.Close();
            return (revision, documentCount, avgdl, TryReadArtifactId(connection));
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

    private static long ReadInt64(SqliteDataReader reader, int ordinal, string absPath, string column)
    {
        if (reader.IsDBNull(ordinal))
            throw MalformedMeta(absPath, $"{column} is null");
        return reader.GetInt64(ordinal);
    }

    private static double ReadDouble(SqliteDataReader reader, int ordinal, string absPath, string column)
    {
        if (reader.IsDBNull(ordinal))
            throw MalformedMeta(absPath, $"{column} is null");
        return reader.GetDouble(ordinal);
    }

    private static InvalidOperationException MalformedMeta(string absPath, string detail, Exception? inner = null) =>
        new($"search.db at '{absPath}' has malformed meta: {detail}. Rebuild the search index.", inner);

    private static void ValidateFtsTables(SqliteConnection connection)
    {
        ExecuteMatchSmokeQuery(connection, "SELECT symbol_id FROM symbols_fts WHERE body MATCH $q LIMIT 1;");
        ExecuteMatchSmokeQuery(connection, "SELECT symbol_id FROM symbols_trigram WHERE symbols_trigram MATCH $q LIMIT 1;");
    }

    private static void ExecuteMatchSmokeQuery(SqliteConnection connection, string commandText)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = commandText;
        cmd.Parameters.AddWithValue("$q", "\"__miller_sidecar_smoke__\"");
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) { }
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0 || _documentCount == 0)
            return Array.Empty<SearchHit>();

        // Distinct query terms in first-seen order — preserving the in-memory index's per-document
        // summation order so the floating-point scores are bit-identical.
        var queryTokens = new List<string>(8);
        CodeTokenizer.Tokenize(query, queryTokens);
        var distinctTerms = new List<string>(queryTokens.Count);
        var seenTerms = new HashSet<string>(StringComparer.Ordinal);
        foreach (string t in queryTokens)
            if (seenTerms.Add(t)) distinctTerms.Add(t);

        string collapsedQuery = CollapseName.Of(query);
        bool wantTrigram = mode == SearchMode.Or && collapsedQuery.Length >= 3;

        if (distinctTerms.Count == 0 && !wantTrigram)
            return Array.Empty<SearchHit>();

        using var connection = new SqliteConnection(_connectionString);
        long connectionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        connection.Open();
        Observe(FtsSearchQueryFamily.ConnectionOpen, rows: 0, connectionStarted);

        if (mode == SearchMode.And && distinctTerms.Count > 1)
        {
            string andMatch = string.Join(" AND ", distinctTerms.Select(QuoteFts));
            long probeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            bool hasIntersection = HasAndIntersection(connection, andMatch);
            Observe(FtsSearchQueryFamily.AndIntersectionProbe, hasIntersection ? 1 : 0, probeStarted);
            if (!hasIntersection)
                return Array.Empty<SearchHit>();
        }

        // ---- Word arm: every doc matching ANY query term (uncapped — exactly the set the in-memory index
        // would score), re-scored with the shared BM25 math. AND-filtered in C# after accumulation.
        var wordHits = new List<SearchHit>();
        var wordMatched = new HashSet<int>();   // DocIds the word arm produced (so the trigram arm only adds extras)
        if (distinctTerms.Count > 0)
        {
            int n = _documentCount;
            string normalizedQuery = query.Trim().ToLowerInvariant();
            int requiredTerms = distinctTerms.Count;

            string orMatch = string.Join(" OR ", distinctTerms.Select(QuoteFts));
            long wordCandidatesStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            IReadOnlyList<WordCandidate> candidates = WordCandidates(connection, orMatch);
            Observe(FtsSearchQueryFamily.WordCandidates, candidates.Count, wordCandidatesStarted);

            long wordScoringStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var df = new int[distinctTerms.Count];
            var scoredCandidates = new List<WordCandidateScore>(candidates.Count);
            foreach (WordCandidate candidate in candidates)
            {
                string[] tokens = candidate.Body.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                var termFrequencies = new int[distinctTerms.Count];
                int matched = 0;
                for (int i = 0; i < distinctTerms.Count; i++)
                {
                    int tf = CountOccurrences(tokens, distinctTerms[i]);
                    if (tf == 0) continue;
                    termFrequencies[i] = tf;
                    df[i]++;
                    matched++;
                }

                if (matched == 0) continue;
                scoredCandidates.Add(new WordCandidateScore(candidate, termFrequencies, matched));
            }

            var rankedCandidates = new List<RankedWordCandidate>(scoredCandidates.Count);
            foreach (WordCandidateScore candidate in scoredCandidates)
            {
                if (mode == SearchMode.And && candidate.Matched < requiredTerms) continue;   // AND: every distinct term must hit

                double score = 0.0;
                for (int i = 0; i < distinctTerms.Count; i++)
                {
                    int tf = candidate.TermFrequencies[i];
                    if (tf == 0) continue;
                    score += Bm25.TermScore(Bm25.Idf(n, df[i]), tf, candidate.Candidate.DocLen, _avgdl);
                }

                score = Bm25.ApplyExactNameAdjustments(
                    score,
                    candidate.Candidate.Name,
                    candidate.Candidate.Kind,
                    normalizedQuery);

                rankedCandidates.Add(new RankedWordCandidate(candidate.Candidate.DocId, score));
                wordMatched.Add(candidate.Candidate.DocId);
            }

            // Deterministic ordering: score DESC, then DocId ASC — identical to MillerSearchIndex.
            rankedCandidates.Sort(static (a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : a.DocId.CompareTo(b.DocId);
            });
            Observe(FtsSearchQueryFamily.WordScoring, rankedCandidates.Count, wordScoringStarted);

            int survivorCount = Math.Min(rankedCandidates.Count, limit);
            long wordHydrationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            IReadOnlyDictionary<int, IndexedSymbol> hydrated =
                HydrateWordSymbols(connection, rankedCandidates, survivorCount);
            for (int i = 0; i < survivorCount; i++)
            {
                RankedWordCandidate candidate = rankedCandidates[i];
                wordHits.Add(new SearchHit(hydrated[candidate.DocId].ToSearchableDocument(), candidate.Score));
            }
            Observe(FtsSearchQueryFamily.WordHydration, hydrated.Count, wordHydrationStarted);
        }

        // ---- Trigram arm (OR only): additive interior-substring recall over the collapsed name, windowed
        // and floored BELOW every word hit so it never perturbs word-arm ranking parity. Excluded under AND.
        List<SearchHit>? trigramOnly = null;
        if (wantTrigram && wordHits.Count < limit)
        {
            long trigramCandidatesStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            IReadOnlyList<DiskSymbol> trigramCandidates =
                TrigramCandidates(connection, QuoteFts(collapsedQuery), TrigramWindow);
            Observe(FtsSearchQueryFamily.TrigramCandidates, trigramCandidates.Count, trigramCandidatesStarted);

            long trigramScoringStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (DiskSymbol candidate in trigramCandidates)
            {
                IndexedSymbol symbol = candidate.Symbol;
                if (wordMatched.Contains(symbol.DocId)) continue;
                (trigramOnly ??= new List<SearchHit>()).Add(new SearchHit(symbol.ToSearchableDocument(), 0.0));
            }
            trigramOnly?.Sort(static (a, b) => a.Document.DocId.CompareTo(b.Document.DocId));
            Observe(FtsSearchQueryFamily.TrigramScoring, trigramOnly?.Count ?? 0, trigramScoringStarted);
        }

        long finalOrderingStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (wordHits.Count == 0 && trigramOnly is null)
        {
            Observe(FtsSearchQueryFamily.FinalOrdering, rows: 0, finalOrderingStarted);
            return Array.Empty<SearchHit>();
        }

        var results = new List<SearchHit>(wordHits.Count + (trigramOnly?.Count ?? 0));
        results.AddRange(wordHits);
        if (trigramOnly is not null)
            results.AddRange(trigramOnly);
        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);
        Observe(FtsSearchQueryFamily.FinalOrdering, results.Count, finalOrderingStarted);
        return results;
    }

    private void Observe(FtsSearchQueryFamily family, int rows, long startedAt)
    {
        var observation = new FtsSearchQueryObservation(
            family,
            rows,
            System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));
        FtsSearchQueryTelemetryCollector.Current?.Record(observation);
        _queryObserver?.Invoke(observation);
    }

    /// <summary>How many trigram-arm candidates to window in (interior-substring recall is purely additive).</summary>
    private const int TrigramWindow = 200;

    private sealed record WordCandidateScore(WordCandidate Candidate, int[] TermFrequencies, int Matched);

    private readonly record struct RankedWordCandidate(int DocId, double Score);

    private readonly record struct WordCandidate(int DocId, string Name, string Kind, int DocLen, string Body);

    private static IReadOnlyList<WordCandidate> WordCandidates(SqliteConnection connection, string match)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.doc_id, s.name, s.kind, s.doc_len, symbols_fts.body
            FROM symbols_fts
            JOIN search_symbols s ON s.symbol_id = symbols_fts.symbol_id
            WHERE body MATCH $q
            """;
        cmd.Parameters.AddWithValue("$q", match);
        var results = new List<WordCandidate>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new WordCandidate(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                reader.GetString(4)));
        }
        return results;
    }

    private const int HydrationParameterChunkSize = 500;

    private static IReadOnlyDictionary<int, IndexedSymbol> HydrateWordSymbols(
        SqliteConnection connection,
        IReadOnlyList<RankedWordCandidate> rankedCandidates,
        int count)
    {
        var hydrated = new Dictionary<int, IndexedSymbol>(count);
        for (int offset = 0; offset < count; offset += HydrationParameterChunkSize)
        {
            int chunkCount = Math.Min(HydrationParameterChunkSize, count - offset);
            using var cmd = connection.CreateCommand();
            var placeholders = new string[chunkCount];
            for (int i = 0; i < chunkCount; i++)
            {
                string parameterName = "$doc" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                placeholders[i] = parameterName;
                cmd.Parameters.AddWithValue(parameterName, rankedCandidates[offset + i].DocId);
            }
            cmd.CommandText = SymbolSelect(
                $"FROM search_symbols s WHERE s.doc_id IN ({string.Join(',', placeholders)});");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                IndexedSymbol symbol = ReadDiskSymbol(reader).Symbol;
                hydrated.Add(symbol.DocId, symbol);
            }
        }
        return hydrated;
    }

    private static bool HasAndIntersection(SqliteConnection connection, string match)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM symbols_fts WHERE body MATCH $q LIMIT 1;";
        cmd.Parameters.AddWithValue("$q", match);
        return cmd.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<DiskSymbol> TrigramCandidates(SqliteConnection connection, string match, int limit)
    {
        using var cmd = connection.CreateCommand();
        // The window must be cut by RELEVANCE, not doc_id: when more than `limit` symbols contain the trigram
        // set, a doc_id-ordered window arbitrarily drops the best interior-substring match. FTS5's built-in
        // bm25 rank (smaller = better) is a cheap proxy — a shorter collapsed name has higher trigram density
        // for the same matched phrase — with shortest-name then doc_id tie-breaks for determinism. Final
        // ordering of trigram-only hits stays doc_id ASC in Search(); only window MEMBERSHIP changes here.
        cmd.CommandText = SymbolSelect("""
            FROM symbols_trigram
            JOIN search_symbols s ON s.symbol_id = symbols_trigram.symbol_id
            WHERE symbols_trigram MATCH $q
            ORDER BY symbols_trigram.rank, length(s.name), s.doc_id
            LIMIT $lim
            """);
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$lim", limit);
        return ReadDiskSymbols(cmd);
    }

    private static IReadOnlyList<DiskSymbol> ReadDiskSymbols(SqliteCommand cmd)
    {
        var results = new List<DiskSymbol>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadDiskSymbol(reader));
        return results;
    }

    private static int CountOccurrences(IReadOnlyList<string> tokens, string term)
    {
        int count = 0;
        foreach (string t in tokens)
            if (string.Equals(t, term, StringComparison.Ordinal))
                count++;
        return count;
    }

    // FTS5 string literal: wrap in double quotes, doubling any embedded quote. CodeTokenizer tokens never
    // contain a double quote (not a word char), but the collapsed query is user text — quote defensively so
    // FTS reserved words (AND/OR/NOT/NEAR) and stray characters are treated as a literal term.
    private static string QuoteFts(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    public IReadOnlyList<IndexedSymbol> FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("FROM search_symbols s WHERE s.name = $name ORDER BY s.doc_id;");
        cmd.Parameters.AddWithValue("$name", name);
        return ReadIndexedSymbols(cmd);
    }

    public IndexedSymbol? FindBySymbolId(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("FROM search_symbols s WHERE s.symbol_id = $id;");
        cmd.Parameters.AddWithValue("$id", symbolId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDiskSymbol(reader).Symbol : null;
    }

    public IReadOnlyDictionary<string, IndexedSymbol> FindBySymbolIds(IEnumerable<string> symbolIds)
    {
        ArgumentNullException.ThrowIfNull(symbolIds);

        var requested = symbolIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requested.Length == 0)
            return new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);

        var loaded = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
        using var connection = OpenConnection();

        const int chunkSize = 500;
        for (int offset = 0; offset < requested.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, requested.Length - offset);
            using var cmd = connection.CreateCommand();
            var placeholders = new string[count];
            for (int i = 0; i < count; i++)
            {
                string parameterName = "$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                placeholders[i] = parameterName;
                cmd.Parameters.AddWithValue(parameterName, requested[offset + i]);
            }

            cmd.CommandText = SymbolSelect(
                "FROM search_symbols s WHERE s.symbol_id IN (" + string.Join(", ", placeholders) + ");");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                IndexedSymbol symbol = ReadDiskSymbol(reader).Symbol;
                loaded[symbol.SymbolId] = symbol;
            }
        }

        var ordered = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
        foreach (string id in requested)
            if (loaded.TryGetValue(id, out IndexedSymbol? symbol))
                ordered[id] = symbol;
        return ordered;
    }

    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId)
    {
        ArgumentNullException.ThrowIfNull(parentId);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("FROM search_symbols s WHERE s.parent_symbol_id = $parent ORDER BY s.doc_id;");
        cmd.Parameters.AddWithValue("$parent", parentId);
        return ReadIndexedSymbols(cmd);
    }

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("FROM search_symbols s WHERE s.path = $path ORDER BY s.doc_id;");
        cmd.Parameters.AddWithValue("$path", filePath);
        return ReadIndexedSymbols(cmd);
    }

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit < 1)
            return Array.Empty<IndexedSymbol>();

        IReadOnlyList<string> rankedPaths = FindFilePathsByFragment(query, int.MaxValue);

        var results = new List<IndexedSymbol>(limit);
        var pathsWithRemainder = new List<IReadOnlyList<IndexedSymbol>>();
        foreach (string path in rankedPaths)
        {
            IReadOnlyList<IndexedSymbol> symbols = FindByFilePath(path);
            if (symbols.Count == 0)
                continue;

            results.Add(symbols[0]);
            if (results.Count == limit)
                return results;
            if (symbols.Count > 1)
                pathsWithRemainder.Add(symbols);
        }

        foreach (IReadOnlyList<IndexedSymbol> symbols in pathsWithRemainder)
        {
            for (int i = 1; i < symbols.Count; i++)
            {
                results.Add(symbols[i]);
                if (results.Count == limit)
                    return results;
            }
        }

        return results;
    }

    public IReadOnlyList<string> FindFilePathsByFragment(string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit < 1)
            return Array.Empty<string>();

        string normalizedQuery = query.Trim().Replace('\\', '/');
        var rankedPaths = new List<(string Path, int Rank)>();
        foreach (string path in _paths.Value)
        {
            string fileName = LastPathSegment(path);
            int rank = RankPath(path, fileName, normalizedQuery);
            if (rank >= 0)
                rankedPaths.Add((path, rank));
        }

        rankedPaths.Sort(static (a, b) =>
        {
            int byRank = a.Rank.CompareTo(b.Rank);
            if (byRank != 0) return byRank;
            int byLength = a.Path.Length.CompareTo(b.Path.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Path, b.Path);
        });
        return rankedPaths
            .Take(limit)
            .Select(static candidate => candidate.Path)
            .ToArray();
    }

    public bool IsIndexedFilePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return _paths.Value.Contains(path, StringComparer.Ordinal);
    }

    public string? ResolveIndexedFilePath(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (IsIndexedFilePath(target))
            return target;

        string? match = null;
        foreach (string path in _paths.Value)
        {
            if (!string.Equals(LastPathSegment(path), target, StringComparison.Ordinal))
                continue;
            if (match is not null)
                return null;
            match = path;
        }
        return match;
    }

    public IndexedSymbol Resolve(int docId)
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("FROM search_symbols s WHERE s.doc_id = $doc;");
        cmd.Parameters.AddWithValue("$doc", docId);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return ReadDiskSymbol(reader).Symbol;

        throw new ArgumentOutOfRangeException(nameof(docId), docId,
            $"No symbol row exists for DocId {docId}.");
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private IReadOnlyList<string> LoadPaths()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT path FROM search_symbols ORDER BY path;";
        var paths = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    private static IReadOnlySet<string> BuildKnownExtensions(IReadOnlyList<string> paths)
    {
        var knownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string ext = Path.GetExtension(LastPathSegment(path));
            if (ext.Length > 1)
                knownExtensions.Add(ext);
        }
        return knownExtensions;
    }

    private static string SymbolSelect(string suffix) => """
        SELECT s.doc_id,
               s.symbol_id, s.name, s.signature, s.kind, s.language, s.path,
               s.start_line, s.end_line, s.parent_symbol_id, s.is_test,
               s.test_container, s.test_lifecycle, s.test_evidence_status, s.test_evidence_reason,
               s.doc_len
        """ + "\n" + suffix;

    private static IReadOnlyList<IndexedSymbol> ReadIndexedSymbols(SqliteCommand cmd)
    {
        var results = new List<IndexedSymbol>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadDiskSymbol(reader).Symbol);
        return results;
    }

    private static DiskSymbol ReadDiskSymbol(SqliteDataReader reader)
    {
        var symbol = new IndexedSymbol(
            DocId: reader.GetInt32(0),
            SymbolId: reader.GetString(1),
            Name: reader.GetString(2),
            Signature: reader.IsDBNull(3) ? null : reader.GetString(3),
            Kind: reader.GetString(4),
            Language: reader.GetString(5),
            FilePath: reader.GetString(6),
            StartLine: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
            EndLine: reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            ParentId: reader.IsDBNull(9) ? null : reader.GetString(9),
            IsTest: !reader.IsDBNull(10) && reader.GetInt64(10) != 0,
            TestContainer: reader.GetInt64(11) != 0,
            TestLifecycle: reader.GetInt64(12) != 0,
            TestEvidenceStatus: reader.GetString(13),
            TestEvidenceReason: reader.IsDBNull(14) ? null : reader.GetString(14));
        int docLen = reader.IsDBNull(15) ? 0 : reader.GetInt32(15);
        return new DiskSymbol(symbol, docLen);
    }

    private static int RankPath(string path, string fileName, string query)
    {
        if (string.Equals(path, query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(fileName, query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        return -1;
    }

    private static string LastPathSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private readonly record struct DiskSymbol(IndexedSymbol Symbol, int DocLen);
}

internal enum FtsSearchQueryFamily
{
    ConnectionOpen,
    AndIntersectionProbe,
    WordCandidates,
    WordHydration,
    WordScoring,
    TrigramCandidates,
    TrigramScoring,
    FinalOrdering,
}

internal readonly record struct FtsSearchQueryObservation(
    FtsSearchQueryFamily Family,
    int Rows,
    TimeSpan Elapsed);

internal readonly record struct FtsSearchQueryFamilyMeasurement(
    long CallCount,
    long ElapsedTicks,
    long ReturnedRowCount);

internal sealed record FtsSearchQueryMeasurementSnapshot(
    FtsSearchQueryFamilyMeasurement ConnectionOpen,
    FtsSearchQueryFamilyMeasurement AndIntersectionProbe,
    FtsSearchQueryFamilyMeasurement WordCandidates,
    FtsSearchQueryFamilyMeasurement WordHydration,
    FtsSearchQueryFamilyMeasurement WordScoring,
    FtsSearchQueryFamilyMeasurement TrigramCandidates,
    FtsSearchQueryFamilyMeasurement TrigramScoring,
    FtsSearchQueryFamilyMeasurement FinalOrdering);

internal sealed class FtsSearchQueryTelemetryCollector
{
    private static readonly AsyncLocal<FtsSearchQueryTelemetryCollector?> CurrentCollector = new();
    private readonly long[] _calls = new long[Enum.GetValues<FtsSearchQueryFamily>().Length];
    private readonly long[] _elapsedTicks = new long[Enum.GetValues<FtsSearchQueryFamily>().Length];
    private readonly long[] _rows = new long[Enum.GetValues<FtsSearchQueryFamily>().Length];

    internal static FtsSearchQueryMeasurementSnapshot EmptySnapshot { get; } =
        new(default, default, default, default, default, default, default, default);

    internal static FtsSearchQueryTelemetryCollector? Current => CurrentCollector.Value;

    internal IDisposable Activate()
    {
        FtsSearchQueryTelemetryCollector? previous = CurrentCollector.Value;
        CurrentCollector.Value = this;
        return new Activation(previous);
    }

    internal void Record(FtsSearchQueryObservation observation)
    {
        int index = (int)observation.Family;
        Interlocked.Increment(ref _calls[index]);
        Interlocked.Add(ref _elapsedTicks[index], observation.Elapsed.Ticks);
        Interlocked.Add(ref _rows[index], observation.Rows);
    }

    internal FtsSearchQueryMeasurementSnapshot Snapshot() =>
        new(
            Family(FtsSearchQueryFamily.ConnectionOpen),
            Family(FtsSearchQueryFamily.AndIntersectionProbe),
            Family(FtsSearchQueryFamily.WordCandidates),
            Family(FtsSearchQueryFamily.WordHydration),
            Family(FtsSearchQueryFamily.WordScoring),
            Family(FtsSearchQueryFamily.TrigramCandidates),
            Family(FtsSearchQueryFamily.TrigramScoring),
            Family(FtsSearchQueryFamily.FinalOrdering));

    private FtsSearchQueryFamilyMeasurement Family(FtsSearchQueryFamily family)
    {
        int index = (int)family;
        return new FtsSearchQueryFamilyMeasurement(
            Interlocked.Read(ref _calls[index]),
            Interlocked.Read(ref _elapsedTicks[index]),
            Interlocked.Read(ref _rows[index]));
    }

    private sealed class Activation(FtsSearchQueryTelemetryCollector? previous) : IDisposable
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
