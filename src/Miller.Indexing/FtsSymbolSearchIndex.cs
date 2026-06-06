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
/// <c>DocId</c> is derived from <c>search_symbols.rowid - 1</c>; <see cref="SearchIndexWriter"/> inserts rows
/// in the same deterministic order as <see cref="SqliteSymbolReader"/>, preserving parity without loading every
/// row. Each operation opens a short-lived read-only connection, so atomic <c>search.db</c> replacement by the
/// writer never races a held reader, and concurrent searches need no lock.</para>
/// </summary>
public sealed class FtsSymbolSearchIndex : ISymbolLookupIndex
{
    private readonly string _connectionString;
    private readonly double _avgdl;
    private readonly int _documentCount;
    private readonly Lazy<IReadOnlyList<string>> _paths;
    private readonly Lazy<IReadOnlySet<string>> _knownExtensions;

    private FtsSymbolSearchIndex(string connectionString, double avgdl, long revision, int documentCount)
    {
        _connectionString = connectionString;
        _avgdl = avgdl;
        Revision = revision;
        _documentCount = documentCount;
        _paths = new Lazy<IReadOnlyList<string>>(LoadPaths, LazyThreadSafetyMode.ExecutionAndPublication);
        _knownExtensions = new Lazy<IReadOnlySet<string>>(
            () => BuildKnownExtensions(_paths.Value),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The julie-extract revision this artifact was built from (freshness key for Phase 3 routing).</summary>
    public long Revision { get; }

    public int DocumentCount => _documentCount;

    public IReadOnlySet<string> KnownExtensions => _knownExtensions.Value;

    /// <summary>
    /// Open the <c>search.db</c> at <paramref name="searchDbPath"/>, validate its schema version, and load
    /// the resident symbol snapshot + corpus stats. Throws if the file is missing or schema-incompatible
    /// (the caller — Phase 3 — self-heals to the in-memory projection on failure).
    /// </summary>
    public static FtsSymbolSearchIndex Open(string searchDbPath)
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

        (long revision, int documentCount, double avgdl) = ReadMeta(connection, absPath);
        ValidateFtsTables(connection);

        return new FtsSymbolSearchIndex(connectionString, avgdl, revision, documentCount);
    }

    private static (long Revision, int DocumentCount, double Avgdl) ReadMeta(SqliteConnection connection, string absPath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT revision, doc_count, avgdl, schema_version FROM meta LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"search.db at '{absPath}' has no meta row.");

        long revision = reader.GetInt64(0);
        int documentCount = checked((int)reader.GetInt64(1));
        double avgdl = reader.GetDouble(2);
        int schemaVersion = reader.GetInt32(3);
        if (schemaVersion != SearchIndexWriter.SchemaVersion)
            throw new InvalidOperationException(
                $"search.db at '{absPath}' has schema_version {schemaVersion}; " +
                $"this build expects {SearchIndexWriter.SchemaVersion}. Rebuild the search index.");

        return (revision, documentCount, avgdl);
    }

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
        connection.Open();

        // ---- Word arm: every doc matching ANY query term (uncapped — exactly the set the in-memory index
        // would score), re-scored with the shared BM25 math. AND-filtered in C# after accumulation.
        var wordHits = new List<SearchHit>();
        var wordMatched = new HashSet<int>();   // DocIds the word arm produced (so the trigram arm only adds extras)
        if (distinctTerms.Count > 0)
        {
            int n = _documentCount;
            string normalizedQuery = query.Trim().ToLowerInvariant();
            int requiredTerms = distinctTerms.Count;

            // Per-term document frequency. COUNT(*) over a single-term MATCH equals the in-memory postings
            // length and needs no fts5vocab vtable (which a read-only DB couldn't create).
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string term in distinctTerms)
                df[term] = CountDocsMatching(connection, QuoteFts(term));

            string orMatch = string.Join(" OR ", distinctTerms.Select(QuoteFts));
            IReadOnlyList<DiskSymbol> candidates = WordCandidates(connection, orMatch);

            var tokens = new List<string>(16);
            foreach (DiskSymbol candidate in candidates)
            {
                IndexedSymbol symbol = candidate.Symbol;

                tokens.Clear();
                string text = string.IsNullOrEmpty(symbol.Signature) ? symbol.Name : symbol.Name + " " + symbol.Signature;
                CodeTokenizer.Tokenize(text, tokens);
                int docLen = candidate.DocLen;

                double score = 0.0;
                int matched = 0;
                foreach (string term in distinctTerms)
                {
                    int tf = CountOccurrences(tokens, term);
                    if (tf == 0) continue;
                    score += Bm25.TermScore(Bm25.Idf(n, df[term]), tf, docLen, _avgdl);
                    matched++;
                }

                if (matched == 0) continue;
                if (mode == SearchMode.And && matched < requiredTerms) continue;   // AND: every distinct term must hit

                score = Bm25.ApplyExactNameAdjustments(
                    score,
                    symbol.Name,
                    symbol.Kind,
                    normalizedQuery);

                wordHits.Add(new SearchHit(symbol.ToSearchableDocument(), score));
                wordMatched.Add(symbol.DocId);
            }

            // Deterministic ordering: score DESC, then DocId ASC — identical to MillerSearchIndex.
            wordHits.Sort(static (a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : a.Document.DocId.CompareTo(b.Document.DocId);
            });
        }

        // ---- Trigram arm (OR only): additive interior-substring recall over the collapsed name, windowed
        // and floored BELOW every word hit so it never perturbs word-arm ranking parity. Excluded under AND.
        List<SearchHit>? trigramOnly = null;
        if (wantTrigram)
        {
            foreach (DiskSymbol candidate in TrigramCandidates(connection, QuoteFts(collapsedQuery), TrigramWindow))
            {
                IndexedSymbol symbol = candidate.Symbol;
                if (wordMatched.Contains(symbol.DocId)) continue;
                (trigramOnly ??= new List<SearchHit>()).Add(new SearchHit(symbol.ToSearchableDocument(), 0.0));
            }
            trigramOnly?.Sort(static (a, b) => a.Document.DocId.CompareTo(b.Document.DocId));
        }

        if (wordHits.Count == 0 && trigramOnly is null)
            return Array.Empty<SearchHit>();

        var results = new List<SearchHit>(wordHits.Count + (trigramOnly?.Count ?? 0));
        results.AddRange(wordHits);
        if (trigramOnly is not null)
            results.AddRange(trigramOnly);
        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);
        return results;
    }

    /// <summary>How many trigram-arm candidates to window in (interior-substring recall is purely additive).</summary>
    private const int TrigramWindow = 200;

    private static int CountDocsMatching(SqliteConnection connection, string match)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM symbols_fts WHERE body MATCH $q;";
        cmd.Parameters.AddWithValue("$q", match);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<DiskSymbol> WordCandidates(SqliteConnection connection, string match)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("""
            FROM symbols_fts
            JOIN search_symbols s ON s.symbol_id = symbols_fts.symbol_id
            WHERE body MATCH $q
            """);
        cmd.Parameters.AddWithValue("$q", match);
        return ReadDiskSymbols(cmd);
    }

    private static IReadOnlyList<DiskSymbol> TrigramCandidates(SqliteConnection connection, string match, int limit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("""
            FROM symbols_trigram
            JOIN search_symbols s ON s.symbol_id = symbols_trigram.symbol_id
            WHERE symbols_trigram MATCH $q
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

    private static int CountOccurrences(List<string> tokens, string term)
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
        cmd.CommandText = SymbolSelect("FROM search_symbols s WHERE s.name = $name ORDER BY s.rowid;");
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

    public IReadOnlyList<IndexedSymbol> FindChildren(string parentId)
    {
        ArgumentNullException.ThrowIfNull(parentId);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("FROM search_symbols s WHERE s.parent_symbol_id = $parent ORDER BY s.rowid;");
        cmd.Parameters.AddWithValue("$parent", parentId);
        return ReadIndexedSymbols(cmd);
    }

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("FROM search_symbols s WHERE s.path = $path ORDER BY s.rowid;");
        cmd.Parameters.AddWithValue("$path", filePath);
        return ReadIndexedSymbols(cmd);
    }

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit < 1)
            return Array.Empty<IndexedSymbol>();

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

        var results = new List<IndexedSymbol>(limit);
        var pathsWithRemainder = new List<IReadOnlyList<IndexedSymbol>>();
        foreach ((string path, _) in rankedPaths)
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
        if ((uint)docId >= (uint)_documentCount)
            throw new ArgumentOutOfRangeException(nameof(docId), docId,
                $"DocId must be in [0, {_documentCount}).");

        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SymbolSelect("FROM search_symbols s WHERE s.rowid = $rowid;");
        cmd.Parameters.AddWithValue("$rowid", docId + 1L);
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
        SELECT CAST(s.rowid - 1 AS INTEGER) AS doc_id,
               s.symbol_id, s.name, s.signature, s.kind, s.language, s.path,
               s.start_line, s.end_line, s.parent_symbol_id, s.is_test, s.doc_len
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
            IsTest: !reader.IsDBNull(10) && reader.GetInt64(10) != 0);
        int docLen = reader.IsDBNull(11) ? 0 : reader.GetInt32(11);
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
