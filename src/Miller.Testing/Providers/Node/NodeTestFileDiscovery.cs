using System.IO.Enumeration;
using System.Text.Json;

namespace Miller.Testing;

/// <summary>
/// Which files make up a <c>node:test</c> project's suite.
///
/// <para>Jest and vitest own their own discovery conventions, and this provider keeps matching those by
/// the <c>.test.</c>/<c>.spec.</c> stem. Node's runner does NOT use that convention: it matches its own
/// documented pattern set, which also takes every file under a <c>test</c> directory whatever the file is
/// called. A repo that keeps its suite in <c>tests/index.js</c> therefore discovered ZERO cases and got no
/// coverage at all, while its own <c>npm test</c> ran 63 passing tests (dogfood finding F8, 2026-08-21).</para>
///
/// <para>Two rules, in Node's own order. When the project's test script names no path, Node runs the
/// DEFAULT patterns below, copied verbatim from <c>https://nodejs.org/api/test.html</c> ("Running tests
/// from the command line" — by default the runner runs all files matching these patterns). When the script
/// DOES name paths or globs, those replace the defaults, exactly as they do on Node's command line; that
/// is the rule which finds <c>node --test ./tests/*.js</c>, the shape the finding was raised against.</para>
/// </summary>
internal sealed class NodeTestFileDiscovery
{
    /// <summary>
    /// Node's documented default test-file patterns, verbatim from the Node.js API documentation for the
    /// test runner. The second block is the TypeScript set Node matches unless <c>--no-strip-types</c> is
    /// supplied; Miller does not supply it, so both blocks apply. Keep these strings diffable against the
    /// doc page — do not "tidy" them into a different spelling.
    /// </summary>
    private static readonly string[] DocumentedDefaultPatterns =
    [
        "**/*.test.{cjs,mjs,js}",
        "**/*-test.{cjs,mjs,js}",
        "**/*_test.{cjs,mjs,js}",
        "**/test-*.{cjs,mjs,js}",
        "**/test.{cjs,mjs,js}",
        "**/test/**/*.{cjs,mjs,js}",
        "**/*.test.{cts,mts,ts}",
        "**/*-test.{cts,mts,ts}",
        "**/*_test.{cts,mts,ts}",
        "**/test-*.{cts,mts,ts}",
        "**/test.{cts,mts,ts}",
        "**/test/**/*.{cts,mts,ts}",
    ];

    /// <summary>
    /// Every extension Node's runner loads, as one brace group. Used when a script names a DIRECTORY: Node
    /// walks it and takes the files it can run, which is the whole extension set rather than one pattern.
    /// </summary>
    private const string NodeExtensions = "{cjs,mjs,js,cts,mts,ts}";

    private readonly string[] _patterns;

    private NodeTestFileDiscovery(string[] patterns) => _patterns = patterns;

    /// <summary>The default set, for a project whose script names no path.</summary>
    internal static NodeTestFileDiscovery Default { get; } = FromPatterns(DocumentedDefaultPatterns);

    /// <summary>
    /// The discovery rule for one package root: the paths its own node:test script names, or Node's
    /// defaults when it names none. A manifest that cannot be read falls back to the defaults — the same
    /// answer the runner itself would give a script-less invocation.
    /// </summary>
    internal static NodeTestFileDiscovery ForPackage(string packageRoot)
    {
        var patterns = new List<string>();
        foreach (var token in ScriptPathArguments(packageRoot))
        {
            var normalized = NormalizePath(token);
            if (normalized.Length == 0)
                continue;

            // A directory argument means "every runnable file under here", which is what Node's own walk
            // of a supplied directory produces. A glob argument travels as written.
            if (Directory.Exists(Path.Combine(packageRoot, normalized)))
                patterns.Add(normalized.TrimEnd('/') + "/**/*." + NodeExtensions);
            else if (LooksLikeAPath(normalized))
                patterns.Add(normalized);
        }

        return patterns.Count == 0 ? Default : FromPatterns(patterns);
    }

    internal static NodeTestFileDiscovery FromPatterns(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        return new NodeTestFileDiscovery(patterns.SelectMany(ExpandBraces).Distinct(StringComparer.Ordinal).ToArray());
    }

    internal bool IsMatch(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var path = NormalizePath(relativePath);
        foreach (var pattern in _patterns)
        {
            if (MatchesGlob(pattern, path))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The positional path arguments the package's node:test scripts hand to the runner. Only the chained
    /// segments that really start Node's runner are read, so a script such as
    /// <c>rimraf coverage &amp;&amp; node --test ./tests/*.js</c> contributes the glob and not the
    /// <c>coverage</c> the cleanup names.
    /// </summary>
    private static IReadOnlyList<string> ScriptPathArguments(string packageRoot) =>
        NodeTestScriptCommands(packageRoot).SelectMany(ScriptTestPathArguments).ToArray();

    /// <summary>
    /// The positional arguments one script hands Node's runner. Empty for a script that only carries
    /// flags.
    /// </summary>
    internal static IReadOnlyList<string> ScriptTestPathArguments(string command)
    {
        var arguments = new List<string>();
        foreach (var segment in NodeCommandLine.SplitChainedSegments(command))
        {
            if (!JavaScriptTestProvider.IsNodeTestRunnerCommand(segment))
                continue;

            arguments.AddRange(PositionalArguments(NodeCommandLine.SplitCommand(segment)));
        }

        return arguments;
    }

    /// <summary>
    /// True when a node:test script already names a test path.
    ///
    /// <para>Node stops reading OPTIONS at the first positional argument, so a script such as
    /// <c>node --test ./tests/*.js</c> reads an appended <c>--test-reporter junit</c> as two more paths and
    /// writes no report at all — measured, not assumed: the run exits 0, prints the default spec output,
    /// and leaves the destination file absent. A run routed through such a script therefore produces a
    /// verdict for nothing.</para>
    /// </summary>
    internal static bool SuppliesPositionalArguments(string command) =>
        ScriptTestPathArguments(command).Count > 0;

    private static IEnumerable<string> NodeTestScriptCommands(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(manifestPath))
            return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
                return [];

            return scripts.EnumerateObject()
                .Where(script => script.Value.ValueKind == JsonValueKind.String)
                .Select(script => script.Value.GetString() ?? string.Empty)
                .Where(JavaScriptTestProvider.IsNodeTestRunnerCommand)
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The tokens of one command that could name a test path: not the launcher, not a flag. A flag's
    /// separate VALUE can survive this filter (<c>--test-concurrency 4</c> leaves "4" behind), which costs
    /// nothing — a value that names no file matches no file.
    /// </summary>
    private static IEnumerable<string> PositionalArguments(IReadOnlyList<string> tokens)
    {
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Length == 0 || token[0] == '-')
                continue;
            if (IsLauncherName(token))
                continue;

            yield return token;
        }
    }

    private static bool IsLauncherName(string token) =>
        token is "node" or "nodejs" or "npx" or "pnpm" or "npm" or "yarn" or "exec" or "run" or "tsx";

    private static bool LooksLikeAPath(string token) =>
        token.Contains('/', StringComparison.Ordinal)
        || token.Contains('*', StringComparison.Ordinal)
        || token.Contains('?', StringComparison.Ordinal)
        || token.Contains('.', StringComparison.Ordinal);

    /// <summary>
    /// glob(7)-shaped matching over a workspace-relative path, which is the behaviour Node documents for
    /// the patterns above. <c>**</c> spans any number of path segments; every other segment is matched on
    /// its own, so <c>tests/*.js</c> takes <c>tests/index.js</c> and leaves <c>tests/deep/index.js</c>.
    /// </summary>
    internal static bool MatchesGlob(string pattern, string relativePath)
    {
        var patternSegments = SplitSegments(NormalizePath(pattern));
        var pathSegments = SplitSegments(NormalizePath(relativePath));
        return MatchSegments(patternSegments, 0, pathSegments, 0);
    }

    private static bool MatchSegments(string[] pattern, int patternIndex, string[] path, int pathIndex)
    {
        while (patternIndex < pattern.Length)
        {
            if (pattern[patternIndex] == "**")
            {
                for (var skipped = pathIndex; skipped <= path.Length; skipped++)
                {
                    if (MatchSegments(pattern, patternIndex + 1, path, skipped))
                        return true;
                }

                return false;
            }

            if (pathIndex >= path.Length)
                return false;
            if (!FileSystemName.MatchesSimpleExpression(pattern[patternIndex], path[pathIndex], ignoreCase: true))
                return false;

            patternIndex++;
            pathIndex++;
        }

        return pathIndex == path.Length;
    }

    private static string[] SplitSegments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    /// <summary>
    /// Expands one brace group at a time, so <c>**/test.{cjs,mjs,js}</c> becomes three plain patterns and
    /// the matcher never has to understand braces. An unbalanced brace is left alone rather than guessed at.
    /// </summary>
    private static IEnumerable<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{', StringComparison.Ordinal);
        if (open < 0)
        {
            yield return pattern;
            yield break;
        }

        var close = pattern.IndexOf('}', open);
        if (close < 0)
        {
            yield return pattern;
            yield break;
        }

        var prefix = pattern[..open];
        var suffix = pattern[(close + 1)..];
        foreach (var option in pattern[(open + 1)..close].Split(','))
        {
            foreach (var expanded in ExpandBraces(prefix + option.Trim() + suffix))
                yield return expanded;
        }
    }
}
