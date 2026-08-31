using System.Collections.Frozen;
using Miller.Core.Tokenization;

namespace Miller.Core.Search;

/// <summary>
/// In-memory, BM25-ranked inverted index over <see cref="SearchableDocument"/>s. Pure logic with
/// zero I/O: <see cref="Miller.Indexing"/> owns the SQLite read and the row→document mapping; this
/// type owns tokenization, postings, length normalization, and ranking.
///
/// Build cost is paid once via <see cref="Build"/>; the result is immutable and the postings map is
/// a <see cref="FrozenDictionary{TKey,TValue}"/> for the fastest steady-state term lookup. Searches
/// are allocation-light and deterministic (Decision D2 tie-break: score DESC, then DocId ASC).
/// </summary>
public sealed class MillerSearchIndex
{
    private readonly FrozenDictionary<string, Posting[]> _postings;
    private readonly FrozenDictionary<int, int> _docLen;            // DocId -> total emitted tokens (pre-dedup)
    private readonly FrozenDictionary<int, SearchableDocument> _documents;
    private readonly double _avgdl;

    private MillerSearchIndex(
        FrozenDictionary<string, Posting[]> postings,
        FrozenDictionary<int, int> docLen,
        FrozenDictionary<int, SearchableDocument> documents,
        double avgdl)
    {
        _postings = postings;
        _docLen = docLen;
        _documents = documents;
        _avgdl = avgdl;
    }

    /// <summary>Total number of indexed documents.</summary>
    public int DocumentCount => _documents.Count;

    /// <summary>Number of distinct terms in the index.</summary>
    public int TermCount => _postings.Count;

    /// <summary>
    /// Build an index from <paramref name="documents"/>. Index text per document is
    /// <c>Name + (Signature is null/empty ? "" : " " + Signature)</c>. <c>docLen</c> counts every
    /// emitted token (full words + components + duplicates) BEFORE de-dup; postings count term
    /// frequency per document and are stored sorted by DocId ascending.
    /// </summary>
    /// <exception cref="ArgumentException">Two documents share the same <see cref="SearchableDocument.DocId"/>.</exception>
    public static MillerSearchIndex Build(IReadOnlyList<SearchableDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var documentsById = new Dictionary<int, SearchableDocument>(documents.Count);
        var docLen = new Dictionary<int, int>(documents.Count);
        // term -> (docId -> tf). Inner dict accumulates occurrences so we emit one Posting per (term,doc).
        var builder = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        var tokens = new List<string>(16);

        long totalLen = 0;
        foreach (var doc in documents)
        {
            if (!documentsById.TryAdd(doc.DocId, doc))
                throw new ArgumentException(
                    $"Duplicate DocId {doc.DocId}; document ids must be unique.", nameof(documents));

            string text = string.IsNullOrEmpty(doc.Signature) ? doc.Name : doc.Name + " " + doc.Signature;

            tokens.Clear();
            CodeTokenizer.Tokenize(text, tokens);

            docLen[doc.DocId] = tokens.Count;   // counts full words + components + duplicates
            totalLen += tokens.Count;

            foreach (var term in tokens)
            {
                if (!builder.TryGetValue(term, out var perDoc))
                {
                    perDoc = new Dictionary<int, int>();
                    builder[term] = perDoc;
                }
                perDoc.TryGetValue(doc.DocId, out int tf);
                perDoc[doc.DocId] = tf + 1;
            }
        }

        var postings = builder.ToFrozenDictionary(
            kv => kv.Key,
            kv =>
            {
                var arr = new Posting[kv.Value.Count];
                int idx = 0;
                foreach (var (id, tf) in kv.Value) arr[idx++] = new Posting(id, tf);
                Array.Sort(arr, static (a, b) => a.DocId.CompareTo(b.DocId)); // postings sorted by DocId asc
                return arr;
            },
            StringComparer.Ordinal);

        double avgdl = documents.Count == 0 ? 0.0 : (double)totalLen / documents.Count;

        return new MillerSearchIndex(
            postings,
            docLen.ToFrozenDictionary(),
            documentsById.ToFrozenDictionary(),
            avgdl);
    }

    /// <summary>
    /// Search the index. The query is tokenized with <see cref="CodeTokenizer"/>; per-document
    /// scores accumulate over the distinct query terms a document matches (Decision D2).
    /// <see cref="SearchMode.And"/> excludes documents that do not match every distinct query term.
    /// An exact match of the trimmed lowercased query against a document's <c>Name</c> applies the
    /// shared exact-name adjustments in <see cref="Bm25"/>: concrete exact matches are boosted, while
    /// low-signal import/module exact matches remain visible but are penalized. Results are ordered by
    /// score DESC then DocId ASC (deterministic), truncated to <paramref name="limit"/>. An
    /// empty/whitespace/tokenless query, or no matches, yields an empty list.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit = 10, SearchMode mode = SearchMode.Or)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0 || _documents.Count == 0)
            return Array.Empty<SearchHit>();

        var queryTokens = new List<string>(8);
        CodeTokenizer.TokenizeQuery(query, queryTokens);
        if (queryTokens.Count == 0)
            return Array.Empty<SearchHit>();

        // Distinct query terms, preserving determinism. For each, resolve postings once.
        var distinctTerms = new List<string>(queryTokens.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in queryTokens)
            if (seen.Add(t)) distinctTerms.Add(t);

        int n = _documents.Count;
        var accum = new Dictionary<int, double>();       // docId -> summed BM25 score
        var matchedTermCount = new Dictionary<int, int>(); // docId -> # distinct query terms matched

        foreach (var term in distinctTerms)
        {
            if (!_postings.TryGetValue(term, out var plist))
                continue;

            int df = plist.Length;
            double idf = Bm25.Idf(n, df);

            foreach (var posting in plist)
            {
                int docLen = _docLen[posting.DocId];
                double termScore = Bm25.TermScore(idf, posting.Tf, docLen, _avgdl);

                accum.TryGetValue(posting.DocId, out double prior);
                accum[posting.DocId] = prior + termScore;

                matchedTermCount.TryGetValue(posting.DocId, out int mc);
                matchedTermCount[posting.DocId] = mc + 1;
            }
        }

        if (accum.Count == 0)
            return Array.Empty<SearchHit>();

        string normalizedQuery = query.Trim().ToLowerInvariant();
        int requiredTerms = distinctTerms.Count;

        var hits = new List<SearchHit>(accum.Count);
        foreach (var (docId, rawScore) in accum)
        {
            if (mode == SearchMode.And && matchedTermCount[docId] < requiredTerms)
                continue;   // AND: must match every distinct query term

            var document = _documents[docId];
            double score = Bm25.ApplyExactNameAdjustments(
                rawScore,
                document.Name,
                document.Kind,
                normalizedQuery);

            hits.Add(new SearchHit(document, score));
        }

        if (hits.Count == 0)
            return Array.Empty<SearchHit>();

        // Deterministic ordering: score DESC, then DocId ASC.
        hits.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Document.DocId.CompareTo(b.Document.DocId);
        });

        if (hits.Count > limit)
            hits.RemoveRange(limit, hits.Count - limit);

        return hits;
    }
}
