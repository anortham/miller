namespace Miller.Testing;

internal static class JsFrameworkTestFileDiscovery
{
    private static readonly string[] DefaultJestPatterns =
    [
        "**/__tests__/**/*.{js,jsx,ts,tsx,mjs,cjs,mts,cts,cjsx,mjsx,mtsx,ctsx}",
        "**/*.{test,spec}.{js,jsx,ts,tsx,mjs,cjs,mts,cts,cjsx,mjsx,mtsx,ctsx}",
        "**/{test,spec}.{js,jsx,ts,tsx,mjs,cjs,mts,cts,cjsx,mjsx,mtsx,ctsx}",
        "!**/build/**",
        "!**/dist/**",
        "!**/e2e/**",
        "!**/cypress/**",
        "!**/playwright/**",
    ];

    private static readonly string[] DefaultVitestPatterns =
    [
        "**/*.{test,spec}.{js,jsx,ts,tsx,mjs,cjs,mts,cts,cjsx,mjsx,mtsx,ctsx}",
        "!**/build/**",
        "!**/dist/**",
        "!**/e2e/**",
        "!**/cypress/**",
        "!**/playwright/**",
    ];

    internal static JsTestPatternSet ForFramework(string? framework, string packageRoot)
    {
        if (string.Equals(framework, "jest", StringComparison.OrdinalIgnoreCase))
        {
            var config = JsTestConfigPatterns.ReadJest(packageRoot);
            return config.ToPatternSet("jest", DefaultJestPatterns);
        }

        if (string.Equals(framework, "vitest", StringComparison.OrdinalIgnoreCase))
        {
            var config = JsTestConfigPatterns.ReadVitest(packageRoot);
            return config.ToPatternSet("vitest", DefaultVitestPatterns);
        }

        throw new ContinuousTestProviderException(
            $"Continuous test framework '{framework ?? "<unspecified>"}' is unsupported for JavaScript discovery.");
    }
}

internal sealed class JsTestPatternSet
{
    private readonly PatternEntry[] _patterns;
    private readonly bool _ordered;

    private JsTestPatternSet(IEnumerable<PatternEntry> patterns, bool ordered)
    {
        _patterns = patterns.ToArray();
        _ordered = ordered;
    }

    internal static JsTestPatternSet ForJest(IReadOnlyList<string> patterns) =>
        Create(patterns, ordered: true);

    internal static JsTestPatternSet ForVitest(
        IReadOnlyList<string> include,
        IReadOnlyList<string> exclude) =>
        new(
            include.Select(pattern => new PatternEntry(pattern, pattern.StartsWith('!')))
                .Concat(exclude.Select(pattern => new PatternEntry(pattern, true))),
            ordered: false);

    internal bool IsMatch(string relativePath)
    {
        if (_ordered)
        {
            var matched = false;
            foreach (var pattern in _patterns)
            {
                if (pattern.Matcher.IsMatch(relativePath))
                    matched = !pattern.Negative;
            }

            return matched;
        }

        var included = _patterns.Any(pattern => !pattern.Negative && pattern.Matcher.IsMatch(relativePath));
        return included && _patterns.All(pattern => !pattern.Negative || !pattern.Matcher.IsMatch(relativePath));
    }

    private static JsTestPatternSet Create(IReadOnlyList<string> patterns, bool ordered) =>
        new(
            patterns.Select(pattern => new PatternEntry(pattern, pattern.StartsWith('!'))),
            ordered);

    private sealed class PatternEntry
    {
        internal PatternEntry(string pattern, bool negative)
        {
            var hasPrefix = pattern.StartsWith('!');
            Negative = negative || hasPrefix;
            var normalized = hasPrefix ? pattern[1..] : pattern;
            var expanded = JsTestGlob.ExpandExtglobs(normalized)
                ?? throw new ContinuousTestProviderException(
                    $"JavaScript test discovery pattern '{pattern}' uses unsupported glob syntax.");
            Matcher = NodeTestFileDiscovery.FromPatterns([expanded]);
        }

        internal bool Negative { get; }

        internal NodeTestFileDiscovery Matcher { get; }
    }
}
