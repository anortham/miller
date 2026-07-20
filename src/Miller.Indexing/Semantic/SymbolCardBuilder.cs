using System.Security.Cryptography;
using System.Text;

namespace Miller.Indexing.Semantic;

/// <summary>
/// The local-only facts a symbol card is built from. Deliberately carries no graph enrichment: card text v1
/// has no callees, members, or implementors, so editing one symbol never invalidates its callers' cards.
/// </summary>
public sealed record SymbolCardInput(
    string SymbolId,
    string Name,
    string Kind,
    string Path,
    bool IsTest,
    string? Signature = null,
    string? DocComment = null,
    string? Container = null);

/// <summary>
/// Card text v1 (vectors-v1 §Corpus contract, design §5.2): <c>{kind} {qualified name} {signature first line}
/// {doc excerpt ≤300} in: {container} {path}</c> within a ~1,200-character budget, truncated on word
/// boundaries with comment markers stripped. Pure text construction — no I/O, no database, no encoder.
/// </summary>
/// <remarks>
/// Eligibility is symbol-kind driven, never a language blocklist: a language is excluded only by emitting no
/// eligible kind. Test symbols DO get cards; <see cref="SymbolCardInput.IsTest"/> rides the vec0 metadata
/// column so they are filtered out of default recall while staying available to test-scoped queries.
/// </remarks>
public static class SymbolCardBuilder
{
    public const int CardBudget = 1200;

    public const int DocExcerptBudget = 300;

    /// <summary>
    /// The <c>chunks-v1</c> truncation policy: docs/config chunks average ~836 tokens and a third exceed 1,024,
    /// so chunk text is capped at roughly a 1,024-token window before embedding. The cap is part of
    /// <c>corpus_generation</c> — changing it is a corpus-generation bump, not a silent retune.
    /// </summary>
    public const int ChunkTextBudget = 4000;

    private const string ContainerMarker = "in:";

    private static readonly string[] LeadingCommentMarkers =
        ["///", "//", "/**", "*/", "/*", "*", "\"\"\"", "'''", "<!--", "-->", "---", "--", "#"];

    private static readonly string[] TrailingCommentMarkers =
        ["*/", "-->", "\"\"\"", "'''", "---", "--"];

    /// <summary>
    /// The symbol kinds that carry enough local meaning to be worth a vector. Declaration kinds only —
    /// identifier-shaped kinds (<c>variable</c>, <c>property</c>, <c>field</c>, <c>constant</c>,
    /// <c>enum_member</c>) and structural kinds (<c>import</c>, <c>module</c>, <c>namespace</c>,
    /// <c>export</c>) are excluded, which is what keeps document languages such as json/yaml/markdown out of
    /// the card corpus without naming them.
    /// </summary>
    public static IReadOnlySet<string> EligibleKinds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "function",
        "method",
        "constructor",
        "class",
        "interface",
        "struct",
        "record",
        "enum",
        "delegate",
        "trait",
        "protocol",
        "union",
        "type_alias",
    };

    public static bool IsEligible(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && EligibleKinds.Contains(kind);

    /// <summary>Builds the card text for <paramref name="input"/>.</summary>
    public static string Build(SymbolCardInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var parts = new List<string>(6) { input.Kind, QualifiedName(input) };

        if (FirstLine(input.Signature) is { Length: > 0 } signature)
            parts.Add(signature);

        if (DocExcerpt(input.DocComment) is { Length: > 0 } doc)
            parts.Add(doc);

        parts.Add(ContainerMarker);
        if (!string.IsNullOrWhiteSpace(input.Container))
            parts.Add(input.Container.Trim());
        parts.Add(input.Path);

        return TruncateOnWordBoundary(
            string.Join(' ', parts.Where(static p => !string.IsNullOrWhiteSpace(p))),
            CardBudget);
    }

    /// <summary>The embedded text of one docs/config chunk: whitespace collapsed, then truncated on a word
    /// boundary within <see cref="ChunkTextBudget"/>.</summary>
    public static string ChunkText(string? rawText) =>
        string.IsNullOrWhiteSpace(rawText)
            ? string.Empty
            : TruncateOnWordBoundary(CollapseWhitespace(rawText), ChunkTextBudget);

    /// <summary>The stable identity of a constructed unit text, stored as <c>embed_text_hash</c>. A unit
    /// re-embeds only when this changes, which is what makes convergence replay idempotent.</summary>
    public static string EmbedTextHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Container-qualified name, or the bare name when the symbol has no container.</summary>
    public static string QualifiedName(SymbolCardInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return string.IsNullOrWhiteSpace(input.Container)
            ? input.Name.Trim()
            : $"{input.Container.Trim()}.{input.Name.Trim()}";
    }

    /// <summary>Comment markers stripped, whitespace collapsed, truncated to
    /// <see cref="DocExcerptBudget"/> on a word boundary.</summary>
    public static string DocExcerpt(string? docComment)
    {
        if (string.IsNullOrWhiteSpace(docComment))
            return string.Empty;

        var lines = new List<string>();
        foreach (string rawLine in docComment.Split('\n'))
        {
            string stripped = StripCommentMarkers(rawLine);
            if (stripped.Length > 0)
                lines.Add(stripped);
        }

        return TruncateOnWordBoundary(CollapseWhitespace(string.Join(' ', lines)), DocExcerptBudget);
    }

    /// <summary>Strips leading and trailing comment markers from one line.</summary>
    public static string StripCommentMarkers(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        string value = line.Trim();
        bool stripped = true;
        while (stripped && value.Length > 0)
        {
            stripped = false;
            foreach (string marker in LeadingCommentMarkers)
            {
                if (!value.StartsWith(marker, StringComparison.Ordinal))
                    continue;

                value = value[marker.Length..].Trim();
                stripped = true;
                break;
            }
        }

        stripped = true;
        while (stripped && value.Length > 0)
        {
            stripped = false;
            foreach (string marker in TrailingCommentMarkers)
            {
                if (!value.EndsWith(marker, StringComparison.Ordinal))
                    continue;

                value = value[..^marker.Length].TrimEnd();
                stripped = true;
                break;
            }
        }

        return value;
    }

    /// <summary>
    /// Cuts <paramref name="text"/> to <paramref name="budget"/> characters at the last whitespace boundary at
    /// or before the budget, so a truncated card never ends mid-token. Text with no boundary before the budget
    /// is hard-cut rather than dropped.
    /// </summary>
    public static string TruncateOnWordBoundary(string text, int budget)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);

        if (text.Length <= budget)
            return text;

        int boundary = text.LastIndexOf(' ', budget);
        return boundary > 0 ? text[..boundary].TrimEnd() : text[..budget];
    }

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        int newline = value.IndexOf('\n', StringComparison.Ordinal);
        return (newline < 0 ? value : value[..newline]).Trim();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
