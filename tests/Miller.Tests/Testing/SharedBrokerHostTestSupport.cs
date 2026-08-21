using Xunit;

namespace Miller.Tests.Testing;

/// <summary>
/// Locates the <c>Miller.SharedBrokerTestHost</c> executable for tests that spawn it. The host is
/// built by a <c>ProjectReference</c> with <c>ReferenceOutputAssembly=false</c>, so its location
/// depends on the build layout: a repo build puts it in its own <c>bin</c> tree beside
/// <c>Miller.Tests</c>, while a flattened build (the CT dotnet provider passes a global
/// <c>-p:OutDir</c>) drops it next to the test assembly itself. Probe the flat layout first, then
/// the repo layout. Before this helper existed the locator was copy-pasted into two test classes
/// and knew only the repo layout, so every broker test failed under continuous testing.
/// </summary>
public static class SharedBrokerHostTestSupport
{
    public static string ExecutableFileName => OperatingSystem.IsWindows()
        ? "Miller.SharedBrokerTestHost.exe"
        : "Miller.SharedBrokerTestHost";

    public static IReadOnlyList<string> CandidatePaths(string baseDirectory, string executableFileName)
    {
        string configuration = new DirectoryInfo(baseDirectory).Parent?.Name ?? "Release";
        string hostRoot = Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Miller.SharedBrokerTestHost",
            "bin"));

        var candidates = new List<string>
        {
            Path.Combine(Path.GetFullPath(baseDirectory), executableFileName),
        };
        candidates.AddRange(new[] { configuration, "Release", "Debug" }
            .Distinct(StringComparer.Ordinal)
            .Select(value => Path.Combine(hostRoot, value, "net10.0", executableFileName)));
        return candidates;
    }

    /// <summary>
    /// Returns the first candidate that can actually launch: the exe must have its companion
    /// dll beside it, because a framework-dependent apphost dies at startup without one. The
    /// tests' own output folder holds a copied exe with no dll, so presence alone lies.
    /// </summary>
    public static string? Locate(string baseDirectory, string executableFileName, Func<string, bool> fileExists)
    {
        foreach (string candidate in CandidatePaths(baseDirectory, executableFileName))
        {
            if (fileExists(candidate) && fileExists(Path.ChangeExtension(candidate, ".dll")))
                return candidate;
        }

        return null;
    }

    public static string RequireBrokerHostExecutable()
    {
        string? found = Locate(AppContext.BaseDirectory, ExecutableFileName, File.Exists);
        Assert.True(
            found is not null,
            "The shared broker test host (exe plus companion dll) was not found at any of: "
                + string.Join("; ", CandidatePaths(AppContext.BaseDirectory, ExecutableFileName)));
        return found!;
    }
}
