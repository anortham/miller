using Xunit;

namespace Miller.Tests.Conventions;

/// <summary>
/// The drift guard that keeps the default fast suite fast. The single worst failure mode for this test
/// project is the one that nearly killed julie: a slow, subprocess-spawning test sneaks into the DEFAULT
/// suite (no <c>[Trait("Category","Scale")]</c>), so every agent that runs a bare <c>dotnet test</c> pays
/// the multi-minute cost on every tiny change. The default-suite filter (the test csproj's
/// <c>VSTestTestCaseFilter=Category!=Scale</c>) excludes <c>Category=Scale</c>; this guard makes sure
/// nothing that SHOULD be tagged Scale escapes the tag.
///
/// The launch signals are the two places a test obtains a real pinned binary to spawn it:
/// <see cref="ScaleTestSupport.RequireJulieServer"/> / <see cref="ScaleTestSupport.LocateJulieServer"/> for
/// <c>.tools/julie-extract</c>, and <see cref="ScaleTestSupport.RequireSemanticSidecar"/> /
/// <see cref="ScaleTestSupport.LocateSemanticSidecar"/> for <c>.tools/julie-semantic-sidecar</c>. The guard
/// scans the test sources and asserts: every file that references either signal also carries the Scale trait.
/// It is intentionally ONE-directional (spawns a pinned binary ⟹ Scale), not the converse: a test can be Scale
/// for other reasons (e.g. <c>RebuildLatencyTests</c> builds a 50k-symbol fixture, no julie), and that is fine.
///
/// Each signal family gets its OWN non-vacuity assertion. One combined counter would let a rename of the
/// semantic signal pass silently as long as some julie test still existed — precisely the coverage hole the
/// counter was added to close.
///
/// This is a SOURCE scan, not reflection, because "this test spawns a subprocess" is a property of the
/// method body that reflection cannot see. It runs in the fast suite in a few ms over ~40 small files.
/// Both the launch-signal scan and the trait check operate on COMMENT-STRIPPED source, so neither can be
/// fooled by a doc-comment <c>&lt;see cref&gt;</c> mention of the signal nor by a prose note that merely
/// quotes the trait without applying it (the false-negative this guard was first written to avoid).
/// </summary>
public sealed class ScaleTraitConventionTests
{
    // The substrings that mark a test as julie-extract-spawning. Referencing any means the test launches the
    // real binary and therefore must be excluded from the default suite.
    private static readonly string[] JulieLaunchSignals = ["RequireJulieServer", "LocateJulieServer", "RunJulie"];

    // The same, for the pinned julie-semantic-sidecar binary.
    private static readonly string[] SemanticLaunchSignals = ["RequireSemanticSidecar", "LocateSemanticSidecar"];

    // Files that legitimately contain the launch-signal substrings WITHOUT spawning julie, and so must be
    // excluded from the scan: the helper that DEFINES them, and this guard that NAMES them as literals.
    private static readonly HashSet<string> ExemptFileNames = new(StringComparer.Ordinal)
    {
        "ScaleTestSupport.cs",
        "ScaleTraitConventionTests.cs",
    };

    [Fact]
    public void EveryPinnedBinarySpawningTest_IsTaggedScale_SoTheDefaultSuiteExcludesIt()
    {
        string testRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "tests", "Miller.Tests");
        Assert.True(Directory.Exists(testRoot), $"Could not locate the test source root at '{testRoot}'.");

        var sources = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsUnderBinOrObj(p))
            .ToList();

        // Sanity: a broken path resolution would scan nothing and let this guard pass vacuously. Refuse to.
        Assert.True(sources.Count > 10,
            $"Expected to scan the Miller.Tests sources but found only {sources.Count} .cs files under " +
            $"'{testRoot}'. The convention guard must not pass vacuously.");

        var violations = new List<string>();
        int julieFilesSeen = 0;
        int semanticFilesSeen = 0;

        foreach (var path in sources)
        {
            if (ExemptFileNames.Contains(Path.GetFileName(path)))
                continue;

            // Strip comments FIRST so a signal/trait mentioned only in prose never counts as real code.
            string code = StripComments(File.ReadAllText(path));
            bool spawnsJulie = JulieLaunchSignals.Any(s => code.Contains(s, StringComparison.Ordinal));
            bool spawnsSemantic = SemanticLaunchSignals.Any(s => code.Contains(s, StringComparison.Ordinal));
            if (!spawnsJulie && !spawnsSemantic)
                continue;

            if (spawnsJulie)
                julieFilesSeen++;
            if (spawnsSemantic)
                semanticFilesSeen++;

            if (!HasScaleTrait(code))
                violations.Add(Path.GetRelativePath(testRoot, path));
        }

        // Sanity: the live tests exist, so the scan must actually see each signal family somewhere. If it sees
        // none, that signal was renamed without updating this guard — a silent coverage hole.
        AssertSignalFamilyIsCovered(julieFilesSeen, "julie-extract", JulieLaunchSignals);
        AssertSignalFamilyIsCovered(semanticFilesSeen, "julie-semantic-sidecar", SemanticLaunchSignals);

        Assert.True(violations.Count == 0,
            "These tests spawn a real pinned binary but are MISSING [Trait(\"Category\",\"Scale\")], so a " +
            "bare `dotnet test` would run them in the default fast suite (the julie 30-min trap). Tag each " +
            "with [Trait(\"Category\",\"Scale\")] at the class level:\n  " +
            string.Join("\n  ", violations));
    }

    private static void AssertSignalFamilyIsCovered(int filesSeen, string binary, string[] signals) =>
        Assert.True(filesSeen >= 1,
            $"The convention guard found NO test referencing the {binary} launch signal " +
            $"({string.Join("/", signals)}). Either the live tests were removed or the signal was renamed " +
            "without updating this guard. Refusing to pass with zero coverage.");

    private static bool IsUnderBinOrObj(string path)
    {
        // Match a path segment exactly (so a file literally named "bin.cs" is not excluded).
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal)
            || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    // Whitespace-insensitive match for [Trait("Category", "Scale")] (the real lines vary the space after
    // the comma). Strips ALL whitespace, then looks for the canonical form. Expects comment-stripped input.
    private static bool HasScaleTrait(string code)
    {
        string collapsed = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
        return collapsed.Contains("[Trait(\"Category\",\"Scale\")]", StringComparison.Ordinal);
    }

    // Remove // line comments and /* */ block comments so neither check is fooled by comment text. This is
    // a lightweight stripper: it does NOT track string literals, so a "//" inside a string truncates that
    // line. That is acceptable here — the launch-signal calls and the [Trait("Category","Scale")] attribute
    // each sit on their own line and never share a line with a string that contains "//", so stripping can
    // never hide a real signal or a real trait (the only failure direction that would matter).
    private static string StripComments(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        int i = 0, n = source.Length;
        bool inBlock = false;
        while (i < n)
        {
            if (inBlock)
            {
                if (i + 1 < n && source[i] == '*' && source[i + 1] == '/') { inBlock = false; i += 2; }
                else i++;
                continue;
            }
            if (i + 1 < n && source[i] == '/' && source[i + 1] == '*') { inBlock = true; i += 2; continue; }
            if (i + 1 < n && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n') i++; // skip to end of line (keep the newline)
                continue;
            }
            sb.Append(source[i]);
            i++;
        }
        return sb.ToString();
    }
}
