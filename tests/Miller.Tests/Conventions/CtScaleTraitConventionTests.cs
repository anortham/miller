using Miller.Tests.Testing;
using Xunit;

namespace Miller.Tests.Conventions;

/// <summary>
/// Drift guard that keeps real CT provider smokes out of the default fast suite. Mirrors
/// <see cref="ScaleTraitConventionTests"/> for the continuous-testing toolchain launch signals in
/// <see cref="CtProviderTestSupport"/>.
///
/// A test that calls a <see cref="CtProviderTestSupport"/> toolchain launch signal (or its matching
/// <c>Locate*</c> method) spawns a real provider process and MUST carry
/// <c>[Trait("Category","Scale")]</c>. The default-suite filter
/// (<c>VSTestTestCaseFilter=Category!=Scale</c>) excludes that trait; this guard makes sure nothing
/// that SHOULD be tagged Scale escapes the tag.
///
/// Each toolchain family gets its OWN non-vacuity assertion. One combined counter would let a rename
/// of one signal pass silently as long as another Scale test still existed.
///
/// This is a SOURCE scan, not reflection. Both the launch-signal scan and the trait check operate on
/// COMMENT-STRIPPED source, so a doc-comment <c>&lt;see cref&gt;</c> mention of the signal cannot
/// satisfy the guard.
/// </summary>
public sealed class CtScaleTraitConventionTests
{
    private static readonly string[] DotnetLaunchSignals = ["RequireDotnet", "LocateDotnet"];
    private static readonly string[] CargoLaunchSignals = ["RequireCargo", "LocateCargo"];
    private static readonly string[] NodeLaunchSignals = ["RequireNode", "LocateNode"];
    private static readonly string[] PythonLaunchSignals = ["RequirePython", "LocatePython"];
    private static readonly string[] CMakeLaunchSignals = ["RequireCMake", "LocateCMake"];
    private static readonly string[] CTestLaunchSignals = ["RequireCTest", "LocateCTest"];
    private static readonly string[] QtQuickTestLaunchSignals =
        ["RequireQtQuickTestCMakePrefix", "LocateQtPaths"];

    private static readonly HashSet<string> ExemptFileNames = new(StringComparer.Ordinal)
    {
        "CtProviderTestSupport.cs",
        "CtScaleTraitConventionTests.cs",
    };

    [Fact]
    public void EveryCtProviderSpawningTest_IsTaggedScale_SoTheDefaultSuiteExcludesIt()
    {
        string testRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "tests", "Miller.Tests");
        Assert.True(Directory.Exists(testRoot), $"Could not locate the test source root at '{testRoot}'.");

        var sources = Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsUnderBinOrObj(p))
            .ToList();

        Assert.True(sources.Count > 10,
            $"Expected to scan the Miller.Tests sources but found only {sources.Count} .cs files under " +
            $"'{testRoot}'. The convention guard must not pass vacuously.");

        var violations = new List<string>();
        int dotnetFilesSeen = 0;
        int cargoFilesSeen = 0;
        int nodeFilesSeen = 0;
        int pythonFilesSeen = 0;
        int cmakeFilesSeen = 0;
        int ctestFilesSeen = 0;
        int qtQuickTestFilesSeen = 0;

        foreach (var path in sources)
        {
            if (ExemptFileNames.Contains(Path.GetFileName(path)))
                continue;

            string code = StripComments(File.ReadAllText(path));
            bool spawnsDotnet = DotnetLaunchSignals.Any(s => code.Contains(s, StringComparison.Ordinal));
            bool spawnsCargo = CargoLaunchSignals.Any(s => code.Contains(s, StringComparison.Ordinal));
            bool spawnsNode = NodeLaunchSignals.Any(s => code.Contains(s, StringComparison.Ordinal));
            bool spawnsPython = PythonLaunchSignals.Any(s => code.Contains(s, StringComparison.Ordinal));
            bool spawnsCMake = CMakeLaunchSignals.Any(s => code.Contains(s, StringComparison.Ordinal));
            bool spawnsCTest = CTestLaunchSignals.Any(s => code.Contains(s, StringComparison.Ordinal));
            bool spawnsQtQuickTest = QtQuickTestLaunchSignals.Any(s => code.Contains(s, StringComparison.Ordinal));
            if (!spawnsDotnet && !spawnsCargo && !spawnsNode && !spawnsPython
                && !spawnsCMake && !spawnsCTest && !spawnsQtQuickTest)
                continue;

            if (spawnsDotnet)
                dotnetFilesSeen++;
            if (spawnsCargo)
                cargoFilesSeen++;
            if (spawnsNode)
                nodeFilesSeen++;
            if (spawnsPython)
                pythonFilesSeen++;
            if (spawnsCMake)
                cmakeFilesSeen++;
            if (spawnsCTest)
                ctestFilesSeen++;
            if (spawnsQtQuickTest)
                qtQuickTestFilesSeen++;

            if (!HasScaleTrait(code))
                violations.Add(Path.GetRelativePath(testRoot, path));
        }

        AssertSignalFamilyIsCovered(dotnetFilesSeen, "dotnet", DotnetLaunchSignals);
        AssertSignalFamilyIsCovered(cargoFilesSeen, "cargo", CargoLaunchSignals);
        AssertSignalFamilyIsCovered(nodeFilesSeen, "node", NodeLaunchSignals);
        AssertSignalFamilyIsCovered(pythonFilesSeen, "python", PythonLaunchSignals);
        AssertSignalFamilyIsCovered(cmakeFilesSeen, "cmake", CMakeLaunchSignals);
        AssertSignalFamilyIsCovered(ctestFilesSeen, "ctest", CTestLaunchSignals);
        AssertSignalFamilyIsCovered(qtQuickTestFilesSeen, "Qt Quick Test", QtQuickTestLaunchSignals);

        Assert.True(violations.Count == 0,
            "These tests spawn a real CT provider toolchain but are MISSING [Trait(\"Category\",\"Scale\")], so a " +
            "bare `dotnet test` would run them in the default fast suite (the julie 30-min trap). Tag each " +
            "with [Trait(\"Category\",\"Scale\")] at the class level:\n  " +
            string.Join("\n  ", violations));
    }

    private static void AssertSignalFamilyIsCovered(int filesSeen, string toolchain, string[] signals) =>
        Assert.True(filesSeen >= 1,
            $"The convention guard found NO test referencing the {toolchain} launch signal " +
            $"({string.Join("/", signals)}). Either the live tests were removed or the signal was renamed " +
            "without updating this guard. Refusing to pass with zero coverage.");

    private static bool IsUnderBinOrObj(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal)
            || normalized.Contains("/obj/", StringComparison.Ordinal);
    }

    private static bool HasScaleTrait(string code)
    {
        string collapsed = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
        return collapsed.Contains("[Trait(\"Category\",\"Scale\")]", StringComparison.Ordinal);
    }

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
                while (i < n && source[i] != '\n') i++;
                continue;
            }
            sb.Append(source[i]);
            i++;
        }
        return sb.ToString();
    }
}
