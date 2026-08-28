namespace Miller.Testing;

/// <summary>
/// Expands the jest and vitest extglob shorthands their documented defaults use, so the existing
/// glob matcher (braces, <c>*</c>, <c>**</c>) can apply them. Unknown extglob syntax returns null
/// so a config read can fall back to defaults instead of matching nothing.
/// </summary>
internal static class JsTestGlob
{
    internal static string? ExpandExtglobs(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // Longest documented forms first so a suffix does not eat the prefix of a longer one.
        var expanded = pattern
            .Replace(
                "?(c|m)[jt]s?(x)",
                "{js,jsx,ts,tsx,cjs,cjsx,mjs,mjsx,cts,ctsx,mts,mtsx}",
                StringComparison.Ordinal)
            .Replace("?(*.)+(spec|test)", "{,*.}{spec,test}", StringComparison.Ordinal)
            .Replace("[jt]s?(x)", "{js,jsx,ts,tsx}", StringComparison.Ordinal)
            .Replace("+(spec|test)", "{spec,test}", StringComparison.Ordinal)
            .Replace("?(x)", "{,x}", StringComparison.Ordinal);

        return ContainsUnhandledExtglob(expanded) ? null : expanded;
    }

    internal static IReadOnlyList<string>? ExpandAll(IReadOnlyList<string>? patterns)
    {
        if (patterns is null)
            return null;

        var expanded = new List<string>(patterns.Count);
        foreach (var pattern in patterns)
        {
            var one = ExpandExtglobs(pattern);
            if (one is null)
                return null;

            expanded.Add(one);
        }

        return expanded;
    }

    private static bool ContainsUnhandledExtglob(string pattern) =>
        pattern.Contains("?(", StringComparison.Ordinal)
        || pattern.Contains("+(", StringComparison.Ordinal)
        || pattern.Contains("@(", StringComparison.Ordinal)
        || pattern.Contains("!(", StringComparison.Ordinal)
        || pattern.Contains('[', StringComparison.Ordinal);
}
