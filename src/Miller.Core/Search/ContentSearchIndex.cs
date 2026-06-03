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

    /// <summary>Context lines kept on each side of the best-matching line in a snippet.</summary>
    private const int WindowRadius = 2;

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
            if (!docs.TryAdd(document.DocId, new DocEntry(document.Path, SplitLines(document.Text))))
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

        var scores = new Dictionary<int, double>();
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
            }
        }

        if (scores.Count == 0)
            return Array.Empty<ContentSearchHit>();

        var ranked = scores
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key)
            .Take(limit);

        var hits = new List<ContentSearchHit>();
        foreach (var (docId, score) in ranked)
        {
            DocEntry entry = _docs[docId];
            (int line, string snippet) = BestLineAndSnippet(entry.Lines, seen);
            hits.Add(new ContentSearchHit(entry.Path, score, line, snippet));
        }
        return hits;
    }

    // The line with the most query-term hits (earliest on a tie), plus a ±WindowRadius context
    // window of raw lines joined by '\n'. Returns a 1-based line number. distinctTerms is the set of
    // query tokens; a line is scored by how many of its tokens are in that set.
    private static (int Line, string Snippet) BestLineAndSnippet(string[] lines, HashSet<string> distinctTerms)
    {
        int bestLine = 0;
        int bestHits = -1;
        var tokens = new List<string>(32);

        for (int i = 0; i < lines.Length; i++)
        {
            tokens.Clear();
            CodeTokenizer.Tokenize(lines[i], tokens);
            int hitCount = 0;
            foreach (string token in tokens)
                if (distinctTerms.Contains(token))
                    hitCount++;

            if (hitCount > bestHits)
            {
                bestHits = hitCount;
                bestLine = i;
            }
        }

        int start = Math.Max(0, bestLine - WindowRadius);
        int end = Math.Min(lines.Length - 1, bestLine + WindowRadius);
        string snippet = string.Join('\n', lines[start..(end + 1)]);
        return (bestLine + 1, snippet);
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

    private readonly record struct DocEntry(string Path, string[] Lines);
}
