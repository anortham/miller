using Xunit;

namespace Miller.Tests.Conventions;

/// <summary>
/// Drift guard for one shared repo-root resolver.
///
/// <para>A test that needs the repo root must call <see cref="ScaleTestSupport.RepoRoot"/>. The obvious
/// private version — walk up from <c>AppContext.BaseDirectory</c> until <c>Miller.slnx</c> appears — is
/// correct under an ordinary <c>dotnet test</c> and WRONG under continuous testing, which builds the test
/// assembly into an out-of-repo directory. That walk starts outside the repo and never finds the file.
/// Walking up from the current directory does not save it either: xunit v3 resets the process current
/// directory to the test-assembly directory before tests execute. Only the workspace-root variable CT sets
/// survives, and only the shared helper reads it.</para>
///
/// <para><b>Why a guard and not a code review.</b> The private copy passes every local run and every CI run.
/// It fails only under CT, where the report is a red verdict in <c>ct.db</c> that nobody is watching. About
/// 50 tests failed that way before the shared helper learned the variable, and two more private copies
/// survived that repair because nothing failed when they were left behind.</para>
///
/// <para>The scan drops doc-comment lines first, so this explanation cannot satisfy the guard, and it
/// asserts the exempt files still contain the token, so the check cannot go vacuously green.</para>
/// </summary>
public sealed class RepoRootResolutionConventionTests
{
    /// <summary>
    /// The marker file a private walk compares against. Written as the quoted C# literal on purpose: a skip
    /// message that tells a reader to run <c>dotnet build Miller.slnx</c> embeds the name inside a longer
    /// string and must not trip the guard.
    /// </summary>
    private const string MarkerLiteral = "\"Miller.slnx\"";

    /// <summary>
    /// The resolver itself, and the test that proves it. Both name the marker for real reasons.
    /// </summary>
    private static readonly HashSet<string> ExemptFileNames = new(StringComparer.Ordinal)
    {
        "ScaleTestSupport.cs",
        "ScaleTestSupportTests.cs",
    };

    [Fact]
    public void No_test_resolves_the_repo_root_on_its_own()
    {
        var offenders = new List<string>();
        var exemptSeen = 0;

        foreach (string path in TestSources())
        {
            string name = Path.GetFileName(path);
            string code = StripDocComments(File.ReadAllText(path));
            if (!code.Contains(MarkerLiteral, StringComparison.Ordinal))
                continue;

            if (ExemptFileNames.Contains(name))
                exemptSeen++;
            else
                offenders.Add(name);
        }

        Assert.True(
            offenders.Count == 0,
            $"these test files locate the repo root themselves: {string.Join(", ", offenders)}. "
            + "Call ScaleTestSupport.RepoRoot() instead. A private walk up from AppContext.BaseDirectory "
            + "starts outside the repo under continuous testing and throws there while passing locally.");

        // Non-vacuity: if the exempt files stop naming the marker, this guard is scanning for a token that
        // no longer exists and would pass no matter what anyone adds.
        Assert.Equal(ExemptFileNames.Count, exemptSeen);
    }

    private static IEnumerable<string> TestSources()
    {
        string testRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "tests", "Miller.Tests");
        return Directory
            .EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Drops <c>///</c> documentation lines, so prose that names the marker cannot satisfy a guard about code.
    /// </summary>
    private static string StripDocComments(string source) =>
        string.Join(
            '\n',
            source
                .Split('\n')
                .Where(static line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));
}
