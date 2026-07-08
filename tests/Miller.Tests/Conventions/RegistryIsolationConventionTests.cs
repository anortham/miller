using Xunit;

namespace Miller.Tests.Conventions;

/// <summary>
/// Drift guard: every test that constructs <see cref="Miller.Server.IndexBootstrapService"/> directly must set
/// <see cref="Miller.Server.IndexBootstrapService.TestHomeDirectoryOverride"/> so bootstrap failure paths never
/// append registry error rows to the real <c>~/.miller/workspaces.db</c>. A SOURCE scan (not reflection) because
/// "this test file constructs the bootstrap" is visible in the constructor call, not in type metadata.
/// </summary>
public sealed class RegistryIsolationConventionTests
{
    private const string ConstructionSignal = "new IndexBootstrapService(";
    private const string IsolationSignal = "TestHomeDirectoryOverride";

    private static readonly HashSet<string> ExemptFileNames = new(StringComparer.Ordinal)
    {
        "RegistryIsolationConventionTests.cs",
    };

    [Fact]
    public void EveryDirectBootstrapConstruction_SetsTestHomeDirectoryOverride_SoRegistryStaysIsolated()
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
        int constructingFilesSeen = 0;

        foreach (var path in sources)
        {
            if (ExemptFileNames.Contains(Path.GetFileName(path)))
                continue;

            string code = StripComments(File.ReadAllText(path));
            if (!code.Contains(ConstructionSignal, StringComparison.Ordinal))
                continue;

            constructingFilesSeen++;
            if (!code.Contains(IsolationSignal, StringComparison.Ordinal))
                violations.Add(Path.GetRelativePath(testRoot, path));
        }

        Assert.True(constructingFilesSeen >= 1,
            $"The convention guard found NO test referencing '{ConstructionSignal}'. Either the live tests were " +
            "removed or the construction pattern was renamed without updating this guard.");

        Assert.True(violations.Count == 0,
            "These tests construct IndexBootstrapService directly but do NOT set " +
            "TestHomeDirectoryOverride, so bootstrap failure paths can write to the real " +
            "~/.miller/workspaces.db. Set the override to a per-test temp home in each file:\n  " +
            string.Join("\n  ", violations));
    }

    private static bool IsUnderBinOrObj(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.Ordinal)
            || normalized.Contains("/obj/", StringComparison.Ordinal);
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
