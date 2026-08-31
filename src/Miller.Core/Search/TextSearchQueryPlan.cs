using System.Collections.Frozen;
using Miller.Core.Tokenization;

namespace Miller.Core.Search;

public sealed class TextSearchQueryPlan
{
    private static readonly FrozenSet<string> CoverageStopWords = new[]
    {
        "a", "an", "and", "are", "as", "at", "by", "does", "for", "from", "in", "is", "it", "of", "on",
        "or", "that", "the", "this", "to", "where", "with",
    }.ToFrozenSet(StringComparer.Ordinal);

    private TextSearchQueryPlan(
        string[] queryTokens,
        string[] distinctTerms,
        string[] coverageTerms,
        bool requiresTokenPhrase,
        int requiredCoverage,
        int requiredLineCoverage)
    {
        QueryTokens = queryTokens;
        DistinctTerms = distinctTerms;
        CoverageTerms = coverageTerms;
        RequiresTokenPhrase = requiresTokenPhrase;
        RequiredCoverage = requiredCoverage;
        RequiredLineCoverage = requiredLineCoverage;
    }

    public IReadOnlyList<string> QueryTokens { get; }

    public IReadOnlyList<string> DistinctTerms { get; }

    public IReadOnlyList<string> CoverageTerms { get; }

    public bool RequiresTokenPhrase { get; }

    public int RequiredCoverage { get; }

    public int RequiredLineCoverage { get; }

    public static TextSearchQueryPlan? Create(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var queryTokens = new List<string>(8);
        CodeTokenizer.TokenizeQuery(query, queryTokens);
        if (queryTokens.Count == 0)
            return null;

        var distinctTerms = new List<string>(queryTokens.Count);
        var seenTerms = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in queryTokens)
            if (seenTerms.Add(token))
                distinctTerms.Add(token);

        string[] coverageTerms = CoverageTermsFor(distinctTerms);
        bool requiresTokenPhrase = QueryRequiresTokenPhrase(query);
        int requiredCoverage = requiresTokenPhrase
            ? coverageTerms.Length
            : RequiredCoverageTermCount(coverageTerms.Length);
        int requiredLineCoverage = requiresTokenPhrase
            ? requiredCoverage
            : Math.Min(requiredCoverage, 4);

        return new TextSearchQueryPlan(
            queryTokens.ToArray(),
            distinctTerms.ToArray(),
            coverageTerms,
            requiresTokenPhrase,
            requiredCoverage,
            requiredLineCoverage);
    }

    private static string[] CoverageTermsFor(IReadOnlyList<string> distinctTerms)
    {
        var terms = new List<string>(distinctTerms.Count);
        foreach (string term in distinctTerms)
            if (term.Length > 2 && !CoverageStopWords.Contains(term))
                terms.Add(term);

        return terms.Count == 0 ? distinctTerms.ToArray() : terms.ToArray();
    }

    private static int RequiredCoverageTermCount(int termCount)
    {
        if (termCount <= 1)
            return termCount;
        if (termCount <= 5)
            return termCount;
        return Math.Max(2, (int)Math.Ceiling(termCount * 0.6));
    }

    private static bool QueryRequiresTokenPhrase(string query) =>
        query.Any(static c => c == '_' || c == ':' || c == '/' || c == '\\');
}
