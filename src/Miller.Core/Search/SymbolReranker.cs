using Miller.Core.Tokenization;

namespace Miller.Core.Search;

/// <summary>One transparent contribution set for a reranked symbol candidate.</summary>
public sealed record SymbolRerankFeatures(
    double RawScore,
    double Exactness,
    double PhraseProximity,
    double SourceRole,
    double PathRole,
    double LanguageAffinity,
    double ContainerEvidence,
    double FinalScore);

/// <summary>Candidates plus score evidence inherited from multiple matching children.</summary>
public sealed record SymbolRerankInput(
    IReadOnlyList<SymbolCandidate> Candidates,
    IReadOnlyDictionary<string, double> ContainerEvidence);

/// <summary>A candidate paired with the feature contributions that produced its final score.</summary>
public sealed record SymbolRerankResult(
    SymbolCandidate Candidate,
    SymbolRerankFeatures Features);

/// <summary>Deterministic, I/O-free reranking over lexical symbol candidates.</summary>
public static class SymbolReranker
{
    /// <summary>Rerank candidates and retain a complete score explanation for evaluation.</summary>
    public static IReadOnlyList<SymbolRerankResult> Rank(
        string query,
        IReadOnlyList<SymbolCandidate> candidates,
        string? dominantLanguage = null,
        IReadOnlyDictionary<string, double>? containerEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        dominantLanguage ??= DominantLanguage(candidates);
        var results = new List<SymbolRerankResult>(candidates.Count);
        foreach (SymbolCandidate candidate in candidates)
        {
            double exactness = Exactness(query, candidate.Name);
            double phraseProximity = PhraseProximity(query, candidate);
            double sourceRole = SourceRole(candidate);
            double pathRole = PathRole(query, candidate.FilePath);
            double languageAffinity = LanguageAffinity(candidate.Language, dominantLanguage);
            double inheritedEvidence =
                containerEvidence?.GetValueOrDefault(candidate.SymbolId) ?? 0;
            double finalScore =
                candidate.Score +
                exactness +
                phraseProximity +
                sourceRole +
                pathRole +
                languageAffinity +
                inheritedEvidence;
            results.Add(new SymbolRerankResult(
                candidate,
                new SymbolRerankFeatures(
                    candidate.Score,
                    exactness,
                    phraseProximity,
                    sourceRole,
                    pathRole,
                    languageAffinity,
                    inheritedEvidence,
                    finalScore)));
        }

        results.Sort(static (left, right) =>
        {
            int byScore = right.Features.FinalScore.CompareTo(left.Features.FinalScore);
            return byScore != 0
                ? byScore
                : left.Candidate.DocId.CompareTo(right.Candidate.DocId);
        });
        return results;
    }

    /// <summary>
    /// Add an otherwise-unmatched parent when at least two lexical children independently point at it.
    /// The parent's raw score remains zero; the inherited contribution stays separately observable.
    /// </summary>
    public static SymbolRerankInput ExpandContainers(
        string query,
        IReadOnlyList<SymbolCandidate> candidates,
        Func<string, SymbolCandidate?> resolveParent)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(resolveParent);

        if (SearchRelaxation.DistinctTermCount(query) < 2)
        {
            return new SymbolRerankInput(
                candidates,
                new Dictionary<string, double>(StringComparer.Ordinal));
        }

        var expanded = new List<SymbolCandidate>(candidates);
        var evidence = new Dictionary<string, double>(StringComparer.Ordinal);
        var directIds = candidates
            .Select(static candidate => candidate.SymbolId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (IGrouping<string, SymbolCandidate> group in candidates
                     .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.ParentId))
                     .GroupBy(static candidate => candidate.ParentId!, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            if (directIds.Contains(group.Key))
                continue;

            SymbolCandidate[] children = group.ToArray();
            if (children.Length < 2 || resolveParent(group.Key) is not { } parent)
                continue;

            double strongestChild = children.Max(static child => child.Score);
            double siblingEvidence = Math.Min(6, (children.Length - 1) * 2);
            expanded.Add(parent with
            {
                Score = 0,
                Origin = SymbolCandidateOrigin.Container,
            });
            evidence[parent.SymbolId] = strongestChild + siblingEvidence;
        }

        return new SymbolRerankInput(expanded, evidence);
    }

    private static string? DominantLanguage(IReadOnlyList<SymbolCandidate> candidates) =>
        candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Language))
            .GroupBy(candidate => candidate.Language!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault();

    private static double Exactness(string query, string name)
    {
        string normalizedQuery = query.Trim().ToLowerInvariant();
        string normalizedName = name.Trim().ToLowerInvariant();
        if (normalizedQuery.Length == 0)
            return 0;
        if (normalizedName == normalizedQuery)
            return 4.0;
        if (QualifiedSuffixMatches(normalizedName, normalizedQuery))
            return 3.0;
        return Compact(normalizedName) == Compact(normalizedQuery) ? 2.0 : 0;
    }

    private static bool QualifiedSuffixMatches(string name, string query)
    {
        if (!name.EndsWith(query, StringComparison.Ordinal))
            return false;
        int prefixLength = name.Length - query.Length;
        return prefixLength > 0 && name[prefixLength - 1] is '.' or ':';
    }

    private static double PhraseProximity(string query, SymbolCandidate candidate)
    {
        var queryTokens = new List<string>(8);
        CodeTokenizer.Tokenize(query, queryTokens);
        string[] terms = queryTokens
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length < 2)
            return 0;

        var candidateTokens = new List<string>(24);
        CodeTokenizer.Tokenize(
            string.IsNullOrEmpty(candidate.Signature)
                ? candidate.Name
                : candidate.Name + " " + candidate.Signature,
            candidateTokens);

        int first = -1;
        int previous = -1;
        foreach (string term in terms)
        {
            int position = FindAfter(candidateTokens, term, previous + 1);
            if (position < 0)
                return 0;
            if (first < 0)
                first = position;
            previous = position;
        }

        int span = previous - first + 1;
        if (span == terms.Length)
            return 2.0;
        return span <= terms.Length + 2 ? 1.0 : 0.5;
    }

    private static int FindAfter(IReadOnlyList<string> tokens, string term, int start)
    {
        for (int index = Math.Max(0, start); index < tokens.Count; index++)
            if (string.Equals(tokens[index], term, StringComparison.Ordinal))
                return index;
        return -1;
    }

    private static double SourceRole(SymbolCandidate candidate)
    {
        if (candidate.Kind is "import" or "module")
            return -8.0;
        if (candidate.Kind is "property" or "key" &&
            Path.GetExtension(candidate.FilePath) is ".json" or ".yaml" or ".yml" or ".toml")
            return -1.5;
        return 0.75;
    }

    private static double PathRole(string query, string path)
    {
        string normalizedPath = path.Replace('\\', '/').ToLowerInvariant();
        if (normalizedPath.Contains("/node_modules/", StringComparison.Ordinal) ||
            normalizedPath.Contains("/vendor/", StringComparison.Ordinal) ||
            normalizedPath.StartsWith("vendor/", StringComparison.Ordinal))
            return -6.0;
        if (normalizedPath.Contains("/generated/", StringComparison.Ordinal) ||
            normalizedPath.StartsWith("generated/", StringComparison.Ordinal) ||
            normalizedPath.EndsWith(".g.cs", StringComparison.Ordinal) ||
            normalizedPath.EndsWith(".designer.cs", StringComparison.Ordinal))
            return -5.0;
        if (!HasTestIntent(query) && LooksLikeTestPath(normalizedPath))
            return -2.0;
        return 0;
    }

    private static bool LooksLikeTestPath(string path) =>
        path.Contains("/test/", StringComparison.Ordinal) ||
        path.Contains("/tests/", StringComparison.Ordinal) ||
        path.Contains("/spec/", StringComparison.Ordinal) ||
        path.Contains("/specs/", StringComparison.Ordinal) ||
        path.EndsWith("test.cs", StringComparison.Ordinal) ||
        path.EndsWith("tests.cs", StringComparison.Ordinal) ||
        path.EndsWith("_test.go", StringComparison.Ordinal) ||
        path.EndsWith(".spec.ts", StringComparison.Ordinal) ||
        path.EndsWith(".test.ts", StringComparison.Ordinal);

    private static bool HasTestIntent(string query) =>
        query.Contains("test", StringComparison.OrdinalIgnoreCase) ||
        query.Contains("spec", StringComparison.OrdinalIgnoreCase);

    private static double LanguageAffinity(string? language, string? dominantLanguage) =>
        !string.IsNullOrWhiteSpace(language) &&
        !string.IsNullOrWhiteSpace(dominantLanguage) &&
        string.Equals(language, dominantLanguage, StringComparison.OrdinalIgnoreCase)
            ? 0.5
            : 0;

    private static string Compact(string value)
    {
        Span<char> buffer = value.Length <= 256
            ? stackalloc char[value.Length]
            : new char[value.Length];
        int length = 0;
        foreach (char character in value)
            if (char.IsLetterOrDigit(character))
                buffer[length++] = char.ToLowerInvariant(character);
        return new string(buffer[..length]);
    }
}
