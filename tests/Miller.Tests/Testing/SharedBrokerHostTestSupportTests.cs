using Xunit;

namespace Miller.Tests.Testing;

public sealed class SharedBrokerHostTestSupportTests
{
    [Fact]
    public void CandidatePaths_ProbeTheTestOutputDirectoryFirst()
    {
        // The CT dotnet provider builds with a global OutDir, so every project in the
        // graph — the broker host included — lands flat in the generation's out\ folder.
        string baseDirectory = Path.Combine(
            Path.GetTempPath(), "miller-ct", "build", "ws", "proj", "gen", "out");

        var candidates = SharedBrokerHostTestSupport.CandidatePaths(baseDirectory, "host.exe");

        Assert.Equal(Path.Combine(baseDirectory, "host.exe"), candidates[0]);
    }

    [Fact]
    public void CandidatePaths_KeepTheRepoLayoutProbes()
    {
        string testsRoot = Path.Combine(Path.GetTempPath(), "repo", "tests");
        string baseDirectory = Path.Combine(testsRoot, "Miller.Tests", "bin", "Release", "net10.0");

        var candidates = SharedBrokerHostTestSupport.CandidatePaths(baseDirectory, "host.exe");

        string hostRoot = Path.Combine(testsRoot, "Miller.SharedBrokerTestHost", "bin");
        Assert.Contains(Path.Combine(hostRoot, "Release", "net10.0", "host.exe"), candidates);
        Assert.Contains(Path.Combine(hostRoot, "Debug", "net10.0", "host.exe"), candidates);
    }

    [Fact]
    public void CandidatePaths_PutTheBuiltConfigurationBeforeTheOthers()
    {
        string testsRoot = Path.Combine(Path.GetTempPath(), "repo", "tests");
        string baseDirectory = Path.Combine(testsRoot, "Miller.Tests", "bin", "Debug", "net10.0");

        var candidates = SharedBrokerHostTestSupport.CandidatePaths(baseDirectory, "host.exe").ToList();

        string hostRoot = Path.Combine(testsRoot, "Miller.SharedBrokerTestHost", "bin");
        int debugIndex = candidates.IndexOf(Path.Combine(hostRoot, "Debug", "net10.0", "host.exe"));
        int releaseIndex = candidates.IndexOf(Path.Combine(hostRoot, "Release", "net10.0", "host.exe"));
        Assert.True(debugIndex >= 0, "the built configuration probe is missing");
        Assert.True(releaseIndex >= 0, "the Release probe is missing");
        Assert.True(debugIndex < releaseIndex, "the built configuration must be probed first");
    }

    [Fact]
    public void Locate_SkipsAnExeWithoutItsCompanionDll()
    {
        // The tests' own output folder holds the host exe, deps.json, and runtimeconfig —
        // but not the host dll. An apphost exe without its dll dies at launch, so the
        // locator must fall through to the host's own complete bin tree.
        string testsRoot = Path.Combine(Path.GetTempPath(), "repo", "tests");
        string baseDirectory = Path.Combine(testsRoot, "Miller.Tests", "bin", "Debug", "net10.0");
        string siblingExe = Path.Combine(baseDirectory, "host.exe");
        string repoExe = Path.Combine(
            testsRoot, "Miller.SharedBrokerTestHost", "bin", "Debug", "net10.0", "host.exe");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            siblingExe,
            repoExe,
            Path.ChangeExtension(repoExe, ".dll"),
        };

        string? found = SharedBrokerHostTestSupport.Locate(baseDirectory, "host.exe", present.Contains);

        Assert.Equal(repoExe, found);
    }

    [Fact]
    public void Locate_AcceptsTheSiblingWhenItsDllIsBesideIt()
    {
        string baseDirectory = Path.Combine(
            Path.GetTempPath(), "miller-ct", "build", "ws", "proj", "gen", "out");
        string siblingExe = Path.Combine(baseDirectory, "host.exe");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            siblingExe,
            Path.ChangeExtension(siblingExe, ".dll"),
        };

        string? found = SharedBrokerHostTestSupport.Locate(baseDirectory, "host.exe", present.Contains);

        Assert.Equal(siblingExe, found);
    }

    [Fact]
    public void CandidatePaths_HandleATrailingSeparatorOnTheBaseDirectory()
    {
        // AppContext.BaseDirectory always ends with a separator.
        string testsRoot = Path.Combine(Path.GetTempPath(), "repo", "tests");
        string baseDirectory = Path.Combine(testsRoot, "Miller.Tests", "bin", "Release", "net10.0")
            + Path.DirectorySeparatorChar;

        var candidates = SharedBrokerHostTestSupport.CandidatePaths(baseDirectory, "host.exe");

        string hostRoot = Path.Combine(testsRoot, "Miller.SharedBrokerTestHost", "bin");
        Assert.Contains(Path.Combine(hostRoot, "Release", "net10.0", "host.exe"), candidates);
    }
}
