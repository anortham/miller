using System.Collections.Frozen;
using Miller.Core.Tokenization;

namespace Miller.Core.Search;

/// <summary>
/// The in-memory BM25 index for free-text content/docs search (phase 3). Distinct from
/// <see cref="MillerSearchIndex"/>, which scores only a symbol's <c>Name + Signature</c> with an
/// exact-name boost; this index scores the full tokenized file text and, for each hit, locates the
/// best-matching line and returns a context window around it. Same code-aware
/// <see cref="CodeTokenizer"/> and BM25 constants as symbol search, so tokenization is consistent.
///
/// Pure logic with zero I/O — the workspace-relative paths and verified file text arrive already
/// freshness-checked from the loader (Miller.Indexing). Document ids are caller-assigned and opaque.
/// </summary>
public sealed class ContentSearchIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const double TokenPhraseBoost = 2.5;

    /// <summary>Context lines kept on each side of the best-matching line in a snippet.</summary>
    private const int WindowRadius = 2;

    private static readonly FrozenSet<string> CoverageStopWords = new[]
    {
        "a", "an", "and", "are", "as", "at", "by", "does", "for", "from", "in", "is", "it", "of", "on",
        "or", "that", "the", "this", "to", "where", "with",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, Posting[]> _postings;
    private readonly FrozenDictionary<int, int> _docLengths;
    private readonly FrozenDictionary<int, DocEntry> _docs;
    private readonly double _avgdl;

    private ContentSearchIndex(
        FrozenDictionary<string, Posting[]> postings,
        FrozenDictionary<int, int> docLengths,
        FrozenDictionary<int, DocEntry> docs,
        double avgdl)
    {
        _postings = postings;
        _docLengths = docLengths;
        _docs = docs;
        _avgdl = avgdl;
    }

    /// <summary>Number of indexed content documents.</summary>
    public int DocumentCount => _docs.Count;

    /// <summary>
    /// Build the index from <paramref name="documents"/>. Index text is each document's full
    /// <see cref="ContentDocument.Text"/>; <c>docLen</c> counts every emitted token (words +
    /// camelCase components + duplicates). The raw lines are retained for snippet extraction.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> is null.</exception>
    /// <exception cref="ArgumentException">Two documents share the same <see cref="ContentDocument.DocId"/>.</exception>
    public static ContentSearchIndex Build(IReadOnlyList<ContentDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var builder = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        var docLengths = new Dictionary<int, int>(documents.Count);
        var docs = new Dictionary<int, DocEntry>(documents.Count);
        var tokens = new List<string>(128);
        long totalLength = 0;

        foreach (ContentDocument document in documents)
        {
            if (!docs.TryAdd(document.DocId, new DocEntry(document.Path, document.Language, SplitLines(document.Text))))
                throw new ArgumentException(
                    $"Duplicate DocId {document.DocId}; content document ids must be unique.", nameof(documents));

            tokens.Clear();
            CodeTokenizer.Tokenize(document.Text, tokens);
            docLengths[document.DocId] = tokens.Count;
            totalLength += tokens.Count;

            foreach (string term in tokens)
            {
                if (!builder.TryGetValue(term, out var perDoc))
                    builder[term] = perDoc = new Dictionary<int, int>();
                perDoc.TryGetValue(document.DocId, out int tf);
                perDoc[document.DocId] = tf + 1;
            }
        }

        var postings = builder.ToFrozenDictionary(
            static kv => kv.Key,
            static kv =>
            {
                var arr = new Posting[kv.Value.Count];
                int index = 0;
                foreach (var (docId, tf) in kv.Value)
                    arr[index++] = new Posting(docId, tf);
                Array.Sort(arr, static (a, b) => a.DocId.CompareTo(b.DocId));
                return arr;
            },
            StringComparer.Ordinal);

        double avgdl = documents.Count == 0 ? 0.0 : (double)totalLength / documents.Count;
        return new ContentSearchIndex(postings, docLengths.ToFrozenDictionary(), docs.ToFrozenDictionary(), avgdl);
    }

    /// <summary>
    /// Search the index. The query is tokenized with <see cref="CodeTokenizer"/>; per-document BM25
    /// scores accumulate over the distinct query terms a document matches. Results are ordered by
    /// score DESC then DocId ASC (deterministic), truncated to <paramref name="limit"/>. Each hit
    /// carries the 1-based best-matching line and a ±<see cref="WindowRadius"/>-line snippet window.
    /// An empty/whitespace/tokenless query, or no matches, yields an empty list.
    /// </summary>
    public IReadOnlyList<ContentSearchHit> Search(string query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0 || _docs.Count == 0)
            return Array.Empty<ContentSearchHit>();

        var queryTokens = new List<string>(8);
        CodeTokenizer.Tokenize(query, queryTokens);
        if (queryTokens.Count == 0)
            return Array.Empty<ContentSearchHit>();

        // Distinct query terms, preserving first-seen order for determinism.
        var distinct = new List<string>(queryTokens.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in queryTokens)
            if (seen.Add(token))
                distinct.Add(token);

        IReadOnlyList<string> coverageTerms = CoverageTerms(distinct);
        var coverageTermSet = coverageTerms.ToHashSet(StringComparer.Ordinal);
        bool requiresTokenPhrase = RequiresTokenPhrase(query);
        int requiredCoverage = requiresTokenPhrase
            ? coverageTerms.Count
            : RequiredCoverageTermCount(coverageTerms.Count);

        var scores = new Dictionary<int, double>();
        var matchedCoverageTermCount = new Dictionary<int, int>();
        foreach (string term in distinct)
        {
            if (!_postings.TryGetValue(term, out Posting[]? postings))
                continue;

            double idf = Math.Log(1.0 + (_docLengths.Count - postings.Length + 0.5) / (postings.Length + 0.5));
            foreach (Posting posting in postings)
            {
                int docLength = _docLengths[posting.DocId];
                double denominator = posting.Tf + K1 * (1 - B + B * docLength / _avgdl);
                double score = idf * posting.Tf * (K1 + 1) / denominator;
                scores.TryGetValue(posting.DocId, out double current);
                scores[posting.DocId] = current + score;

                if (coverageTermSet.Contains(term))
                {
                    matchedCoverageTermCount.TryGetValue(posting.DocId, out int matchedTerms);
                    matchedCoverageTermCount[posting.DocId] = matchedTerms + 1;
                }
            }
        }

        if (scores.Count == 0)
            return Array.Empty<ContentSearchHit>();

        var hits = new List<ScoredHit>();
        foreach (var (docId, rawScore) in scores)
        {
            matchedCoverageTermCount.TryGetValue(docId, out int matchedTerms);
            if (matchedTerms < requiredCoverage)
                continue;

            DocEntry entry = _docs[docId];
            bool hasTokenPhrase = ContainsTokenPhrase(entry.Lines, queryTokens);
            if (requiresTokenPhrase && !hasTokenPhrase)
                continue;

            double score = hasTokenPhrase ? rawScore * TokenPhraseBoost : rawScore;
            BestLineMatch bestLine = BestLineAndSnippet(entry.Lines, coverageTermSet, queryTokens);
            if (bestLine.DistinctTermCount < requiredCoverage)
                continue;

            hits.Add(new ScoredHit(docId,
                new ContentSearchHit(entry.Path, score, bestLine.Line, bestLine.Snippet, entry.Language)));
        }

        if (hits.Count == 0)
            return Array.Empty<ContentSearchHit>();

        hits.Sort(static (a, b) =>
        {
            int byScore = b.Hit.Score.CompareTo(a.Hit.Score);
            return byScore != 0 ? byScore : a.DocId.CompareTo(b.DocId);
        });

        if (hits.Count > limit)
            hits.RemoveRange(limit, hits.Count - limit);

        return hits.Select(static h => h.Hit).ToArray();
    }

    private static IReadOnlyList<string> CoverageTerms(IReadOnlyList<string> distinctTerms)
    {
        var terms = new List<string>(distinctTerms.Count);
        foreach (string term in distinctTerms)
            if (term.Length > 2 && !CoverageStopWords.Contains(term))
                terms.Add(term);

        return terms.Count == 0 ? distinctTerms : terms;
    }

    private static int RequiredCoverageTermCount(int termCount)
    {
        if (termCount <= 1)
            return termCount;
        if (termCount <= 5)
            return termCount;
        return Math.Max(2, (int)Math.Ceiling(termCount * 0.6));
    }

    private static bool RequiresTokenPhrase(string query) =>
        query.Any(static c => c == '_' || c == ':' || c == '/' || c == '\\');

    private static bool ContainsTokenPhrase(string[] lines, IReadOnlyList<string> queryTokens)
    {
        if (queryTokens.Count < 2)
            return false;

        var lineTokens = new List<string>(32);
        foreach (string line in lines)
        {
            lineTokens.Clear();
            CodeTokenizer.Tokenize(line, lineTokens);
            if (lineTokens.Count < queryTokens.Count)
                continue;

            if (ContainsTokenPhrase(lineTokens, queryTokens))
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

    // The line with the most query-term hits (earliest on a tie), plus a ±WindowRadius context
    // window of raw lines joined by '\n'. Returns a 1-based line number. A token-phrase line wins first;
    // otherwise a line is scored by distinct query terms, then repeated term hits.
    private static BestLineMatch BestLineAndSnippet(
        string[] lines,
        HashSet<string> coverageTerms,
        IReadOnlyList<string> queryTokens)
    {
        int bestLine = 0;
        bool bestHasPhrase = false;
        int bestHits = -1;
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
                if (coverageTerms.Contains(token))
                {
                    lineTerms.Add(token);
                    tokenHits++;
                }
            }

            bool hasPhrase = queryTokens.Count > 1 && ContainsTokenPhrase(tokens, queryTokens);
            if (hasPhrase && !bestHasPhrase ||
                hasPhrase == bestHasPhrase &&
                (lineTerms.Count > bestHits || (lineTerms.Count == bestHits && tokenHits > bestTokenHits)))
            {
                bestHasPhrase = hasPhrase;
                bestHits = lineTerms.Count;
                bestTokenHits = tokenHits;
                bestLine = i;
            }
        }

        int start = Math.Max(0, bestLine - WindowRadius);
        int end = Math.Min(lines.Length - 1, bestLine + WindowRadius);
        string snippet = string.Join('\n', lines[start..(end + 1)]);
        return new BestLineMatch(bestLine + 1, snippet, bestHits);
    }

    // Split on '\n' and drop a trailing '\r' so CRLF files do not leak carriage returns into snippets.
    private static string[] SplitLines(string text)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].EndsWith('\r'))
                lines[i] = lines[i][..^1];
        return lines;
    }

    private readonly record struct Posting(int DocId, int Tf);

    private readonly record struct DocEntry(string Path, string Language, string[] Lines);

    private readonly record struct ScoredHit(int DocId, ContentSearchHit Hit);

    private readonly record struct BestLineMatch(int Line, string Snippet, int DistinctTermCount);
}
