using System.Collections.Frozen;

namespace Miller.Core.Search;

/// <summary>The fusion-profile class a hybrid query is scored under (design §6.2, profile <c>fusion-v1</c>).</summary>
public enum SemanticFusionClass
{
    /// <summary>Named-symbol intent: lexical dominates, semantic assists.</summary>
    SymbolLookup,

    /// <summary>Prose/concept intent: semantic dominates.</summary>
    Conceptual,

    /// <summary>Both signals plausible: balanced weights.</summary>
    Mixed,
}

/// <summary>Why <see cref="SemanticQueryPolicy"/> reached its decision. Enum-only, safe to persist as telemetry.</summary>
public enum SemanticQueryReason
{
    /// <summary>Blank query.</summary>
    Empty,

    /// <summary>A single token of three characters or fewer.</summary>
    Short,

    /// <summary>A single token the caller can only have meant as a name.</summary>
    IdentifierLike,

    /// <summary>A filesystem path shape.</summary>
    PathLike,

    /// <summary>Contains code punctuation, so it is a literal-text lookup.</summary>
    CodeSyntax,

    /// <summary>Reads as a question or description rather than a name.</summary>
    Prose,

    /// <summary>Shape-ambiguous, and lexical evidence did not settle it.</summary>
    AmbiguousWeakLexical,

    /// <summary>Shape-ambiguous, but lexical already produced a dominant hit.</summary>
    AmbiguousStrongLexical,
}

/// <summary>
/// The lexical arm's own confidence, supplied by the caller for shape-ambiguous queries. Deliberately
/// scale-free: raw BM25 magnitudes vary with corpus statistics, so the policy compares the top hit to
/// the runner-up rather than to an absolute threshold.
/// </summary>
/// <param name="HitCount">Lexical hits produced for the query.</param>
/// <param name="TopScore">Lexical score of the top hit.</param>
/// <param name="RunnerUpScore">Lexical score of the second hit, or zero when there is only one.</param>
public readonly record struct LexicalEvidence(int HitCount, double TopScore, double RunnerUpScore)
{
    /// <summary>No lexical evidence available — treated as weak.</summary>
    public static LexicalEvidence None => default;

    /// <summary>True when lexical produced a clear winner, which the policy reads as "lexical already answered this".</summary>
    public bool IsStrong =>
        HitCount >= 1 &&
        TopScore > 0 &&
        TopScore >= RunnerUpScore * SemanticQueryPolicy.StrongLexicalDominanceRatio;
}

/// <summary>The routing decision for one query.</summary>
/// <param name="IsHybrid">True when the semantic arm should participate.</param>
/// <param name="HybridClass">The fusion profile F3 keys weights on.</param>
/// <param name="Reason">Why this route was chosen.</param>
public readonly record struct SemanticQueryRoute(
    bool IsHybrid,
    SemanticFusionClass HybridClass,
    SemanticQueryReason Reason);

/// <summary>
/// Decides whether a query gets the semantic arm, and under which fusion profile. Pure and deterministic:
/// no index, no I/O, no clock — the decision depends only on the query text and caller-supplied lexical
/// evidence, so it can run before any artifact is opened.
/// </summary>
public static class SemanticQueryPolicy
{
    /// <summary>Frozen identifier for this decision table; bump when the rules change.</summary>
    public const string PolicyVersion = "policy-v1";

    /// <summary>How far the top lexical score must clear the runner-up before lexical counts as decisive.</summary>
    public const double StrongLexicalDominanceRatio = 1.25;

    /// <summary>A single token at or below this length carries too little signal for either arm.</summary>
    public const int ShortQueryLength = 3;

    /// <summary>Word count at which a marker-free query still reads as prose.</summary>
    public const int ProseWordCount = 5;

    private static readonly FrozenSet<string> ProseMarkers = new[]
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "could", "did", "do", "does",
        "for", "from", "how", "in", "is", "it", "of", "on", "or", "should", "that", "the", "this",
        "to", "was", "were", "what", "when", "where", "which", "who", "why", "with", "would",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly char[] WordSeparators = [' ', '\t', '\n', '\r'];

    /// <summary>Route <paramref name="query"/>, using <paramref name="evidence"/> only for shape-ambiguous queries.</summary>
    public static SemanticQueryRoute Route(string? query, LexicalEvidence? evidence)
    {
        string trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return LexicalOnly(SemanticFusionClass.Mixed, SemanticQueryReason.Empty);

        if (HasCodeSyntax(trimmed))
            return LexicalOnly(SemanticFusionClass.SymbolLookup, SemanticQueryReason.CodeSyntax);

        if (IsPathShaped(trimmed))
            return LexicalOnly(SemanticFusionClass.SymbolLookup, SemanticQueryReason.PathLike);

        string[] words = trimmed.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return LexicalOnly(
                SemanticFusionClass.SymbolLookup,
                trimmed.Length <= ShortQueryLength ? SemanticQueryReason.Short : SemanticQueryReason.IdentifierLike);
        }

        SemanticFusionClass fusionClass = ClassifyWords(words);
        if (fusionClass == SemanticFusionClass.Conceptual)
            return new SemanticQueryRoute(true, fusionClass, SemanticQueryReason.Prose);

        if (HasProseMarker(words))
            return new SemanticQueryRoute(true, fusionClass, SemanticQueryReason.Prose);

        return evidence.GetValueOrDefault().IsStrong
            ? LexicalOnly(fusionClass, SemanticQueryReason.AmbiguousStrongLexical)
            : new SemanticQueryRoute(true, fusionClass, SemanticQueryReason.AmbiguousWeakLexical);
    }

    /// <summary>The stable wire token for <paramref name="fusionClass"/>, as used by telemetry and the CLI.</summary>
    public static string WireName(SemanticFusionClass fusionClass) => fusionClass switch
    {
        SemanticFusionClass.SymbolLookup => "symbol_lookup",
        SemanticFusionClass.Conceptual => "conceptual",
        SemanticFusionClass.Mixed => "mixed",
        _ => throw new ArgumentOutOfRangeException(nameof(fusionClass)),
    };

    private static SemanticQueryRoute LexicalOnly(SemanticFusionClass fusionClass, SemanticQueryReason reason) =>
        new(false, fusionClass, reason);

    private static SemanticFusionClass ClassifyWords(string[] words)
    {
        int identifierWords = 0;
        foreach (string word in words)
            if (IsIdentifierShaped(word))
                identifierWords++;

        if (identifierWords == words.Length)
            return SemanticFusionClass.SymbolLookup;

        if (identifierWords == 0 && (words.Length >= ProseWordCount || HasProseMarker(words)))
            return SemanticFusionClass.Conceptual;

        return SemanticFusionClass.Mixed;
    }

    private static bool HasProseMarker(string[] words)
    {
        foreach (string word in words)
            if (ProseMarkers.Contains(word))
                return true;

        return false;
    }

    /// <summary>
    /// A word is identifier-shaped when its casing or punctuation could not have come from prose:
    /// interior capitals, an underscore, a dot, or a digit.
    /// </summary>
    private static bool IsIdentifierShaped(string word)
    {
        for (int i = 0; i < word.Length; i++)
        {
            char ch = word[i];
            if (ch is '_' or '.' || char.IsDigit(ch))
                return true;
            if (i > 0 && char.IsUpper(ch))
                return true;
        }

        return false;
    }

    private static bool HasCodeSyntax(string query)
    {
        foreach (char ch in query)
        {
            if (ch is '(' or ')' or '{' or '}' or '[' or ']' or ';' or '=' or '<' or '>' or '!'
                or '&' or '|' or '+' or '*' or '%' or ':' or '"' or '\'' or '`')
                return true;
        }

        return false;
    }

    private static bool IsPathShaped(string query)
    {
        if (query.StartsWith("./", StringComparison.Ordinal) ||
            query.StartsWith("../", StringComparison.Ordinal) ||
            query.StartsWith("~/", StringComparison.Ordinal))
            return true;

        bool hasSeparator = false;
        foreach (char ch in query)
        {
            if (char.IsWhiteSpace(ch))
                return false;
            if (ch is '/' or '\\')
                hasSeparator = true;
        }

        return hasSeparator;
    }
}
