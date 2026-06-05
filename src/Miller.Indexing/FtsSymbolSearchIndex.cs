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
/// <para>The resident symbol metadata (one <see cref="IndexedSymbol"/> per row, ordered to reproduce the
/// in-memory DocId ordinals) and corpus stats are loaded once at <see cref="Open"/>. Each query opens a
/// short-lived read-only connection for its FTS MATCH lookups, so the reader holds no file handle between
/// queries — atomic <c>search.db</c> replacement by the writer never races a held reader, and concurrent
/// searches need no lock.</para>
/// </summary>
public sealed class FtsSymbolSearchIndex : ISymbolLookupIndex
{
    private readonly string _connectionString;
    private readonly SymbolLookupTables _tables;
    private readonly double _avgdl;

    private FtsSymbolSearchIndex(string connectionString, SymbolLookupTables tables, double avgdl, long revision)
    {
        _connectionString = connectionString;
        _tables = tables;
        _avgdl = avgdl;
        Revision = revision;
    }

    /// <summary>The julie-extract revision this artifact was built from (freshness key for Phase 3 routing).</summary>
    public long Revision { get; }

    public int DocumentCount => _tables.DocumentCount;

    public IReadOnlySet<string> KnownExtensions => _tables.KnownExtensions;

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

        (long revision, double avgdl) = ReadMeta(connection, absPath);
        IReadOnlyList<IndexedSymbol> symbols = ReadResidentSymbols(connection);
        ValidateFtsTables(connection);

        return new FtsSymbolSearchIndex(connectionString, SymbolLookupTables.Build(symbols), avgdl, revision);
    }

    private static (long Revision, double Avgdl) ReadMeta(SqliteConnection connection, string absPath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT revision, avgdl, schema_version FROM meta LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"search.db at '{absPath}' has no meta row.");

        long revision = reader.GetInt64(0);
        double avgdl = reader.GetDouble(1);
        int schemaVersion = reader.GetInt32(2);
        if (schemaVersion != SearchIndexWriter.SchemaVersion)
            throw new InvalidOperationException(
                $"search.db at '{absPath}' has schema_version {schemaVersion}; " +
                $"this build expects {SearchIndexWriter.SchemaVersion}. Rebuild the search index.");

        return (revision, avgdl);
    }

    // ORDER BY path, start_line, symbol_id — byte-for-byte the SqliteSymbolReader order, so the 0-based
    // ordinal we assign as DocId equals the in-memory projection's DocId (ranking-parity requirement).
    private static IReadOnlyList<IndexedSymbol> ReadResidentSymbols(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT symbol_id, name, signature, kind, language, path,
                   start_line, end_line, parent_symbol_id, is_test
            FROM search_symbols
            ORDER BY path, start_line, symbol_id;
            """;

        var results = new List<IndexedSymbol>();
        using var reader = cmd.ExecuteReader();
        int docId = 0;
        while (reader.Read())
        {
            results.Add(new IndexedSymbol(
                DocId: docId++,
                SymbolId: reader.GetString(0),
                Name: reader.GetString(1),
                Signature: reader.IsDBNull(2) ? null : reader.GetString(2),
                Kind: reader.GetString(3),
                Language: reader.GetString(4),
                FilePath: reader.GetString(5),
                StartLine: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                EndLine: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                ParentId: reader.IsDBNull(8) ? null : reader.GetString(8),
                IsTest: !reader.IsDBNull(9) && reader.GetInt64(9) != 0));
        }
        return results;
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
        if (string.IsNullOrWhiteSpace(query) || limit <= 0 || _tables.DocumentCount == 0)
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
            int n = _tables.DocumentCount;
            string normalizedQuery = query.Trim().ToLowerInvariant();
            int requiredTerms = distinctTerms.Count;

            // Per-term document frequency. COUNT(*) over a single-term MATCH equals the in-memory postings
            // length and needs no fts5vocab vtable (which a read-only DB couldn't create).
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string term in distinctTerms)
                df[term] = CountDocsMatching(connection, QuoteFts(term));

            string orMatch = string.Join(" OR ", distinctTerms.Select(QuoteFts));
            List<string> candidateIds = WordCandidates(connection, orMatch);

            var tokens = new List<string>(16);
            foreach (string symbolId in candidateIds)
            {
                IndexedSymbol? symbol = _tables.FindBySymbolId(symbolId);
                if (symbol is null) continue;   // defensive: symbols_fts and search_symbols are written together

                tokens.Clear();
                string text = string.IsNullOrEmpty(symbol.Signature) ? symbol.Name : symbol.Name + " " + symbol.Signature;
                CodeTokenizer.Tokenize(text, tokens);
                int docLen = tokens.Count;

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
            foreach (string symbolId in TrigramCandidates(connection, QuoteFts(collapsedQuery), TrigramWindow))
            {
                IndexedSymbol? symbol = _tables.FindBySymbolId(symbolId);
                if (symbol is null || wordMatched.Contains(symbol.DocId)) continue;
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

    private static List<string> WordCandidates(SqliteConnection connection, string match)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT symbol_id FROM symbols_fts WHERE body MATCH $q;";
        cmd.Parameters.AddWithValue("$q", match);
        return ReadSymbolIds(cmd);
    }

    private static List<string> TrigramCandidates(SqliteConnection connection, string match, int limit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT symbol_id FROM symbols_trigram WHERE symbols_trigram MATCH $q LIMIT $lim;";
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$lim", limit);
        return ReadSymbolIds(cmd);
    }

    private static List<string> ReadSymbolIds(SqliteCommand cmd)
    {
        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
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

    public IReadOnlyList<IndexedSymbol> FindByName(string name) => _tables.FindByName(name);

    public IndexedSymbol? FindBySymbolId(string symbolId) => _tables.FindBySymbolId(symbolId);

    public IReadOnlyList<IndexedSymbol> FindByFilePath(string filePath) => _tables.FindByFilePath(filePath);

    public IReadOnlyList<IndexedSymbol> FindByFilePathFragment(string query, int limit) =>
        _tables.FindByFilePathFragment(query, limit);

    public bool IsIndexedFilePath(string path) => _tables.IsIndexedFilePath(path);

    public string? ResolveIndexedFilePath(string target) => _tables.ResolveIndexedFilePath(target);

    public IndexedSymbol Resolve(int docId) => _tables.Resolve(docId);
}
