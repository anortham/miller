using Miller.Core.Search;
using Miller.Core.Tokenization;
using Miller.Indexing;

namespace Miller.Tests.Search;

/// <summary>
/// The label-free recall cross-eval harness (the Phase-5 eval plan in
/// docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md). It samples symbols from a corpus,
/// derives four query classes from each name, and measures <c>recall@5</c> + <c>MRR</c> for an in-memory
/// BM25 baseline vs the on-disk FTS5 candidate — so a run proves the candidate is a strict RECALL SUPERSET
/// (interior substring recall rises) with ZERO ranking regression (the word arm reproduces the baseline
/// exactly).
///
/// <para>Pure + deterministic: identity is the stable julie <c>symbol_id</c> (never a per-index DocId), the
/// sample is a seeded shuffle over a SymbolId-ordered frame, and every query-derivation choice is a fixed
/// function of the name. The same harness drives the fast-suite methodology test (tiny crafted corpus) and the
/// Scale real-corpus eval — only the corpus and the two backends differ.</para>
/// </summary>
internal static class SearchRecallEval
{
    /// <summary>The four query classes derived from a symbol name (eval plan).</summary>
    public enum QueryClass
    {
        /// <summary>The whole identifier as typed — both backends tokenize it identically, so recall is high.</summary>
        Exact,

        /// <summary>One interior CamelCase/snake component (a word token) — must not regress.</summary>
        Camel,

        /// <summary>The last component (a word token) — must not regress.</summary>
        Lasttok,

        /// <summary>A boundary-crossing collapsed substring no word token equals — the recall the candidate adds.</summary>
        Interior,
    }

    /// <summary>Recall@5 / MRR for one class, plus the realized sample size and skip count.</summary>
    public sealed record ClassStat(
        QueryClass Class, int N, int Skipped,
        double BaselineRecall, double CandidateRecall,
        double BaselineMrr, double CandidateMrr);

    /// <summary>Word-arm ranking-parity tally: how many word-class queries compared, and any divergence.</summary>
    public sealed record ParityStat(int Compared, int Violations, string? FirstViolation);

    /// <summary>The whole-eval report — per-class recall/MRR + the parity tally, scoped to the sampled frame.</summary>
    public sealed record Report(
        int FrameSize, int Sampled, int Seed,
        IReadOnlyList<ClassStat> Classes, ParityStat Parity)
    {
        public ClassStat Class(QueryClass c) => Classes.Single(s => s.Class == c);
    }

    public sealed record Options(
        int Seed = 1234,
        int SampleSize = 120,
        int PopularityCap = 20,
        Func<IndexedSymbol, bool>? FrameFilter = null);

    // The recall window (recall@5) and a generous candidate fetch so the word-arm parity comparison sees the
    // full ranked list (both backends truncate the SAME ranking identically, so parity holds even when cut).
    private const int RecallK = 5;
    private const int FetchLimit = 64;

    /// <summary>
    /// Run the eval: sample <paramref name="opts"/>.SampleSize symbols from the frame, derive each class's query,
    /// and score both backends. <paramref name="baseline"/> and <paramref name="candidate"/> MUST be built from
    /// the SAME <paramref name="corpus"/> snapshot (identical DocId↔symbol_id maps) — the caller asserts that.
    /// </summary>
    public static Report Evaluate(
        IReadOnlyList<IndexedSymbol> corpus,
        ISymbolLookupIndex baseline,
        ISymbolLookupIndex candidate,
        Options? options = null)
    {
        var opts = options ?? new Options();
        Func<IndexedSymbol, bool> frameFilter = opts.FrameFilter ?? DefaultFrame;

        var frame = corpus.Where(frameFilter)
            .OrderBy(s => s.SymbolId, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<IndexedSymbol> sample = SeededSample(frame, opts.Seed, opts.SampleSize);

        // Popularity of an interior fragment = how many collapsed names CONTAIN it (corpus density, not the
        // index): a fragment shared by many names buries the seed past rank 5 through volume, so it is skipped.
        var allCollapsed = corpus.Select(s => CollapseName.Of(s.Name))
            .Where(c => c.Length > 0).ToList();
        int PopularityOf(string fragment)
        {
            int n = 0;
            foreach (string c in allCollapsed)
                if (c.Contains(fragment, StringComparison.Ordinal)) n++;
            return n;
        }

        var acc = new Dictionary<QueryClass, Accumulator>
        {
            [QueryClass.Exact] = new(), [QueryClass.Camel] = new(),
            [QueryClass.Lasttok] = new(), [QueryClass.Interior] = new(),
        };
        int parityCompared = 0, parityViolations = 0;
        string? firstViolation = null;

        foreach (IndexedSymbol seed in sample)
        {
            var queries = new (QueryClass Class, string? Query)[]
            {
                (QueryClass.Exact, SearchEvalQueries.TryExact(seed.Name)),
                (QueryClass.Camel, SearchEvalQueries.TryCamel(seed.Name)),
                (QueryClass.Lasttok, SearchEvalQueries.TryLasttok(seed.Name)),
                (QueryClass.Interior, SearchEvalQueries.TryInterior(seed.Name, PopularityOf, opts.PopularityCap)),
            };

            foreach ((QueryClass cls, string? query) in queries)
            {
                Accumulator a = acc[cls];
                if (query is null) { a.Skipped++; continue; }

                IReadOnlyList<SearchHit> bHits = baseline.Search(query, FetchLimit, SearchMode.Or);
                IReadOnlyList<SearchHit> cHits = candidate.Search(query, FetchLimit, SearchMode.Or);

                a.N++;
                a.BaselineRecall += RecallMetrics.RecallAt(bHits, seed.SymbolId, baseline, RecallK);
                a.CandidateRecall += RecallMetrics.RecallAt(cHits, seed.SymbolId, candidate, RecallK);
                a.BaselineRr += RecallMetrics.ReciprocalRank(bHits, seed.SymbolId, baseline);
                a.CandidateRr += RecallMetrics.ReciprocalRank(cHits, seed.SymbolId, candidate);

                // Word-arm ranking parity (Exact/Camel/Lasttok only): the candidate's word hits (score > 0,
                // excluding the score-0 trigram-only tail) must reproduce the baseline's full ranked list.
                if (cls != QueryClass.Interior)
                {
                    parityCompared++;
                    string? violation = WordArmParityViolation(query, bHits, cHits, baseline, candidate);
                    if (violation is not null)
                    {
                        parityViolations++;
                        firstViolation ??= violation;
                    }
                }
            }
        }

        var classes = acc.Select(kv => kv.Value.ToStat(kv.Key)).OrderBy(s => s.Class).ToList();
        return new Report(frame.Count, sample.Count, opts.Seed, classes,
            new ParityStat(parityCompared, parityViolations, firstViolation));
    }

    // The candidate's word hits (score > 0) must equal the baseline's hits in DocId order AND score (to 1e-9);
    // the score-0 trigram-only tail is additive recall, never part of the parity claim. Returns a description of
    // the FIRST divergence, or null when the word arms agree.
    private static string? WordArmParityViolation(
        string query,
        IReadOnlyList<SearchHit> baselineHits,
        IReadOnlyList<SearchHit> candidateHits,
        ISymbolLookupIndex baseline,
        ISymbolLookupIndex candidate)
    {
        var candidateWord = candidateHits.Where(h => h.Score > 0.0).ToList();
        if (candidateWord.Count != baselineHits.Count)
            return $"\"{query}\": word-hit count {candidateWord.Count} != baseline {baselineHits.Count}";

        for (int i = 0; i < baselineHits.Count; i++)
        {
            string bId = baseline.Resolve(baselineHits[i].Document.DocId).SymbolId;
            string cId = candidate.Resolve(candidateWord[i].Document.DocId).SymbolId;
            if (!string.Equals(bId, cId, StringComparison.Ordinal))
                return $"\"{query}\": rank {i} symbol {cId} != baseline {bId}";
            if (Math.Abs(baselineHits[i].Score - candidateWord[i].Score) > 1e-9)
                return $"\"{query}\": rank {i} score {candidateWord[i].Score:R} != baseline {baselineHits[i].Score:R}";
        }
        return null;
    }

    // Deterministic seeded shuffle over the (already SymbolId-ordered) frame, then take the first SampleSize.
    private static IReadOnlyList<IndexedSymbol> SeededSample(IReadOnlyList<IndexedSymbol> frame, int seed, int sampleSize)
    {
        var rng = new Random(seed);
        return frame.OrderBy(_ => rng.Next()).Take(Math.Min(sampleSize, frame.Count)).ToList();
    }

    /// <summary>
    /// The default sampling frame: real code identifiers, restricted to one language family so a green run
    /// certifies that language. Excludes <c>import</c>/<c>module</c> kinds (whose names collide pathologically)
    /// and non-identifier names (markdown headings, names with spaces). The WHOLE corpus is still indexed in both
    /// backends — only the set queries are DRAWN FROM is filtered.
    /// </summary>
    public static bool DefaultFrame(IndexedSymbol s) =>
        string.Equals(s.Language, "csharp", StringComparison.Ordinal) &&
        !string.Equals(s.Kind, "import", StringComparison.Ordinal) &&
        !string.Equals(s.Kind, "module", StringComparison.Ordinal) &&
        IsIdentifierName(s.Name);

    private static bool IsIdentifierName(string name)
    {
        if (name.Length < 2) return false;
        foreach (char c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '.' && c != '-')
                return false;
        return true;
    }

    private sealed class Accumulator
    {
        public int N;
        public int Skipped;
        public double BaselineRecall;
        public double CandidateRecall;
        public double BaselineRr;
        public double CandidateRr;

        public ClassStat ToStat(QueryClass cls) => new(
            cls, N, Skipped,
            N == 0 ? 0.0 : BaselineRecall / N,
            N == 0 ? 0.0 : CandidateRecall / N,
            N == 0 ? 0.0 : BaselineRr / N,
            N == 0 ? 0.0 : CandidateRr / N);
    }
}

/// <summary>
/// Pure query-class derivation from a symbol name (no I/O, no corpus state except the interior popularity probe
/// the caller injects). Each <c>Try*</c> returns the query string or <c>null</c> when the class does not apply to
/// the name (a degenerate or duplicative query is SKIPPED, not forced). Deterministic: a name always yields the
/// same query.
/// </summary>
internal static class SearchEvalQueries
{
    private const int MinComponentLength = 3;
    private const int InteriorMin = 4;
    private const int InteriorMax = 6;

    /// <summary>Exact = the raw identifier, trimmed. Both backends tokenize it identically (snake/camel safe) and
    /// it triggers the 1.5x exact-name boost. Skipped when shorter than 2 chars.</summary>
    public static string? TryExact(string name)
    {
        string q = name.Trim();
        return q.Length >= 2 ? q : null;
    }

    /// <summary>Camel = a deterministic INTERIOR component (a real word token), distinct from the last component
    /// so the camel and lasttok classes stay independent. Skipped when the name has no usable component.</summary>
    public static string? TryCamel(string name)
    {
        var comps = Components(name);
        if (comps.Count == 0) return null;

        string? last = LastComponent(comps);
        // Prefer the first qualifying component; fall back to the longest. Never reuse the lasttok pick.
        string? pick = comps.FirstOrDefault(c => Qualifies(c) && !ReferenceEqualsValue(c, last));
        pick ??= comps.Where(c => Qualifies(c) && !ReferenceEqualsValue(c, last))
            .OrderByDescending(c => c.Length).FirstOrDefault();
        return pick;
    }

    /// <summary>Lasttok = the last component, when it is a usable word token. Skipped for single-word names and
    /// all-digit / too-short tails.</summary>
    public static string? TryLasttok(string name)
    {
        var comps = Components(name);
        if (comps.Count == 0) return null;
        string? last = LastComponent(comps);
        return last is not null && Qualifies(last) ? last : null;
    }

    /// <summary>
    /// Interior = an all-letter, boundary-crossing window of the COLLAPSED name (start &gt; 0) that equals NO word
    /// token of the name — so the baseline word arm cannot reach it but the candidate's trigram arm can. Skipped
    /// when no such window exists or the window is too popular across the corpus (<paramref name="popularityOf"/>
    /// &gt; <paramref name="popularityCap"/>). All-letters guarantees the query tokenizes to a single token (no
    /// digit-boundary split that could leak into unrelated symbols).
    /// </summary>
    public static string? TryInterior(string name, Func<string, int> popularityOf, int popularityCap)
    {
        string collapsed = CollapseName.Of(name);
        if (collapsed.Length < InteriorMin + 2) return null;

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var buf = new List<string>(8);
        CodeTokenizer.Tokenize(name, buf);
        foreach (string t in buf) tokens.Add(t);

        for (int len = InteriorMax; len >= InteriorMin; len--)
        {
            // start > 0 keeps the window strictly interior (never the leading prefix).
            for (int start = 1; start + len <= collapsed.Length; start++)
            {
                string window = collapsed.Substring(start, len);
                if (!IsAllAsciiLetters(window)) continue;
                if (tokens.Contains(window)) continue;            // reachable by the word arm => not interior
                if (popularityOf(window) > popularityCap) continue; // too common => measures density, not the index
                return window;
            }
        }
        return null;
    }

    // The CamelCase/snake components of a name = its CodeTokenizer tokens minus the whole collapsed form, in
    // first-seen order. For a single-word name (no split) this is empty.
    private static List<string> Components(string name)
    {
        string whole = CollapseName.Of(name);
        var tokens = new List<string>(8);
        CodeTokenizer.Tokenize(name, tokens);

        var comps = new List<string>(tokens.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string t in tokens)
            if (!string.Equals(t, whole, StringComparison.Ordinal) && seen.Add(t))
                comps.Add(t);
        return comps;
    }

    private static string? LastComponent(List<string> comps) => comps.Count == 0 ? null : comps[^1];

    private static bool Qualifies(string component) =>
        component.Length >= MinComponentLength && !IsAllDigits(component);

    private static bool ReferenceEqualsValue(string a, string? b) =>
        b is not null && string.Equals(a, b, StringComparison.Ordinal);

    private static bool IsAllDigits(string s)
    {
        foreach (char c in s) if (!char.IsAsciiDigit(c)) return false;
        return s.Length > 0;
    }

    private static bool IsAllAsciiLetters(string s)
    {
        foreach (char c in s) if (!char.IsAsciiLetter(c)) return false;
        return s.Length > 0;
    }
}

/// <summary>Pure information-retrieval metrics over a ranked <see cref="SearchHit"/> list, keyed on the stable
/// julie <c>symbol_id</c> (resolved from each hit's DocId), never a cross-index DocId.</summary>
internal static class RecallMetrics
{
    /// <summary>1.0 if the seed symbol's id appears among the top-<paramref name="k"/> hits' symbol ids, else 0.0.</summary>
    public static double RecallAt(IReadOnlyList<SearchHit> hits, string seedSymbolId, ISymbolLookupIndex index, int k)
    {
        int n = Math.Min(k, hits.Count);
        for (int i = 0; i < n; i++)
            if (string.Equals(index.Resolve(hits[i].Document.DocId).SymbolId, seedSymbolId, StringComparison.Ordinal))
                return 1.0;
        return 0.0;
    }

    /// <summary>1/(1-based rank) of the seed symbol in the ranked list, or 0.0 when it is absent.</summary>
    public static double ReciprocalRank(IReadOnlyList<SearchHit> hits, string seedSymbolId, ISymbolLookupIndex index)
    {
        for (int i = 0; i < hits.Count; i++)
            if (string.Equals(index.Resolve(hits[i].Document.DocId).SymbolId, seedSymbolId, StringComparison.Ordinal))
                return 1.0 / (i + 1);
        return 0.0;
    }
}
