namespace Miller.Indexing;

/// <summary>
/// The docs-like scope filter for content search (phase 3). A manifest file is content-searchable when it
/// is prose/markup/config — by language, by extension, or by living under a documentation tree — so symbol
/// search (which already covers source) and content search stay complementary rather than overlapping.
/// Ported from the measured 2026-06-02 spike, plus common config formats per the locked phase 3 design.
/// </summary>
internal static class ContentFileClassifier
{
    private static readonly HashSet<string> ProseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // prose / markup
        ".md", ".markdown", ".mdx", ".rst", ".adoc", ".txt", ".org",
    };

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // config
        ".json", ".yaml", ".yml", ".toml", ".ini", ".cfg",
    };

    public static bool IsDocsLike(string path, string language)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(language);

        string normalized = path.Replace('\\', '/');
        if (normalized.Contains("/docs/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/doc/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("doc/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/documentation/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("documentation/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(language, "markdown", StringComparison.OrdinalIgnoreCase))
            return true;

        string extension = Path.GetExtension(normalized);
        return ProseExtensions.Contains(extension) || ConfigExtensions.Contains(extension);
    }

    public static string WorkspaceContentKind(string path, string language)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(language);

        if (IsConfigLike(path, language))
            return TextContentKind.WorkspaceConfig;

        if (IsDocsLike(path, language))
            return TextContentKind.WorkspaceDocs;

        return TextContentKind.WorkspaceSource;
    }

    private static bool IsConfigLike(string path, string language)
    {
        string normalized = path.Replace('\\', '/');
        if (ConfigExtensions.Contains(Path.GetExtension(normalized)))
            return true;

        return string.Equals(language, "json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "toml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, "ini", StringComparison.OrdinalIgnoreCase);
    }
}
