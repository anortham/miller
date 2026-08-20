using Xunit;

namespace Miller.Tests.Conventions;

/// <summary>
/// Drift guard for the detached-spawn handle discipline.
///
/// <para>Windows duplicates EVERY inheritable handle into a child created with handle inheritance on, and
/// .NET creates every child that way. So a launcher that swaps its standard handles to log files before a
/// spawn has solved only half the problem: the ORIGINAL handles are still inheritable, and when the caller
/// piped the command's stdout, the detached child keeps that pipe open for its whole life. The launcher
/// exits, the reader never sees end-of-file, and the pipeline hangs.</para>
///
/// <para>Nothing fails when the guard is missing. The daemon starts, the logs fill, every test stays green,
/// and only a shell that CAPTURES the output hangs — which no unit test does. That invisibility is why this
/// is a source scan rather than a behaviour test: the seam is correct, only the caller can be wrong.</para>
///
/// <para>The scan drops doc-comment lines first, so a <c>&lt;see cref=…&gt;</c> mention cannot satisfy it,
/// and it asserts the site count, so deleting the last swap cannot turn the guard vacuously green.</para>
/// </summary>
public sealed class DetachedSpawnHandleConventionTests
{
    private const string HandleSwap = "SetStdHandle(";
    private const string Guard = "StandardHandleInheritance.SuppressForSpawn(";

    [Fact]
    public void Every_production_standard_handle_swap_suppresses_inheritance_of_the_launchers_own_handles()
    {
        var unguarded = new List<string>();
        var sites = 0;

        foreach (string path in ProductionSources())
        {
            string code = StripDocComments(File.ReadAllText(path));
            if (!code.Contains(HandleSwap, StringComparison.Ordinal))
                continue;

            sites++;
            if (!code.Contains(Guard, StringComparison.Ordinal))
                unguarded.Add(Path.GetFileName(path));
        }

        Assert.True(
            unguarded.Count == 0,
            $"these production files swap standard handles for a spawn but never call `{Guard}`, so a "
            + $"detached child still inherits the launcher's own stdout: {string.Join(", ", unguarded)}. "
            + "Take the scope BEFORE the swap — after it, the standard handles are the log files the child "
            + "is meant to inherit.");

        // Non-vacuity: a guard that scans zero swap sites passes for the wrong reason. Raise this number
        // when a new launcher legitimately swaps handles; do not delete the assertion.
        Assert.Equal(1, sites);
    }

    private static IEnumerable<string> ProductionSources()
    {
        string sourceRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "src");
        return Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Drops <c>///</c> documentation lines. Both tokens this guard looks for are named in the prose that
    /// explains them, and prose must not satisfy a guard about code.
    /// </summary>
    private static string StripDocComments(string source) =>
        string.Join(
            '\n',
            source
                .Split('\n')
                .Where(static line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));
}
