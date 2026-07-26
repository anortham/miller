using System.Collections.Frozen;

namespace Miller.Core.Search;

/// <summary>
/// Maps a <see cref="SemanticQueryPolicy"/> route to one of the six frozen <c>canary_query_class</c> values of
/// <c>canary-telemetry-v1</c> §Enums. Pure and offline-reproducible: the class is recomputable from the query alone.
/// </summary>
public static class CanaryQueryClassifier
{
    public const string ShortToken = "short_token";
    public const string Identifier = "identifier";
    public const string Path = "path";
    public const string Prose = "prose";
    public const string DocsLike = "docs_like";
    public const string Mixed = "mixed";

    /// <summary>The op that promotes a prose query to <see cref="DocsLike"/> regardless of vocabulary.</summary>
    public const string ContentOp = "content";

    private static readonly FrozenSet<string> DocsVocabulary = new[]
    {
        "readme", "docs", "documentation", "config", "configuration", "guide", "install", "setup",
        "changelog", "license", "tutorial", "faq",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The frozen class for <paramref name="route"/> under the search <paramref name="op"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The route reason is outside the policy enum.</exception>
    public static string Classify(string op, string? query, SemanticQueryRoute route) => route.Reason switch
    {
        SemanticQueryReason.Empty or SemanticQueryReason.Short => ShortToken,
        SemanticQueryReason.IdentifierLike or SemanticQueryReason.CodeSyntax => Identifier,
        SemanticQueryReason.PathLike => Path,
        SemanticQueryReason.Prose => IsDocsLike(op, query) ? DocsLike : Prose,
        SemanticQueryReason.Ambiguous => Mixed,
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    private static bool IsDocsLike(string op, string? query) =>
        string.Equals(op, ContentOp, StringComparison.Ordinal) || HasDocsVocabulary(query);

    private static bool HasDocsVocabulary(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return false;

        int start = -1;
        for (int i = 0; i <= query.Length; i++)
        {
            bool wordChar = i < query.Length && char.IsLetterOrDigit(query[i]);
            if (wordChar && start < 0)
            {
                start = i;
            }
            else if (!wordChar && start >= 0)
            {
                if (DocsVocabulary.Contains(query[start..i]))
                    return true;
                start = -1;
            }
        }

        return false;
    }
}
