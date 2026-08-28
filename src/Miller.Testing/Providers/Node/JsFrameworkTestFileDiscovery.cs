namespace Miller.Testing;

/// <summary>
/// Which files make up a jest or vitest project's suite.
///
/// <para>Defaults follow each runner's own documented patterns, expanded to the ESM/CJS/TS
/// extensions CT already ships and to component files named as tests (<c>.spec.vue</c> and
/// the like). A readable literal <c>testMatch</c> / <c>include</c> array replaces those
/// defaults, the same way the runner itself treats a configured set.</para>
///
/// <para>node:test stays on <see cref="NodeTestFileDiscovery"/> — its rules are not this
/// convention.</para>
/// </summary>
internal static class JsFrameworkTestFileDiscovery
{
    /// <summary>
    /// Jest documented <c>testMatch</c>, expanded:
    /// <c>**/__tests__/**/*.[jt]s?(x)</c> plus <c>**/?(*.)+(spec|test).[jt]s?(x)</c>.
    /// The stem set also keeps <c>mjs</c>/<c>cjs</c>/<c>mts</c>/<c>cts</c> (already shipped)
    /// and component test files. Bare files under <c>__tests__/</c> stay JS/TS only — a
    /// <c>Button.vue</c> sitting there is not a jest default.
    /// </summary>
    private static readonly string[] DefaultJestPatterns =
    [
        "**/__tests__/**/*.{js,jsx,ts,tsx,mjs,cjs,mts,cts}",
        "**/*.{test,spec}.{js,jsx,ts,tsx,mjs,cjs,mts,cts,vue,svelte,astro}",
        "**/{test,spec}.{js,jsx,ts,tsx,mjs,cjs,mts,cts}",
    ];

    /// <summary>
    /// Vitest documented default <c>include</c>:
    /// <c>**/*.{test,spec}.?(c|m)[jt]s?(x)</c>, plus the same component test files.
    /// No bare <c>__tests__/</c> — vitest does not use that convention.
    /// </summary>
    private static readonly string[] DefaultVitestPatterns =
    [
        "**/*.{test,spec}.{js,jsx,ts,tsx,mjs,cjs,mts,cts,vue,svelte,astro}",
    ];

    internal static NodeTestFileDiscovery ForFramework(string? framework, string packageRoot)
    {
        if (string.Equals(framework, "jest", StringComparison.OrdinalIgnoreCase))
        {
            return NodeTestFileDiscovery.FromPatterns(
                JsTestConfigPatterns.ReadJestTestMatch(packageRoot) ?? DefaultJestPatterns);
        }

        return NodeTestFileDiscovery.FromPatterns(
            JsTestConfigPatterns.ReadVitestInclude(packageRoot) ?? DefaultVitestPatterns);
    }
}
