using Miller.Testing;
using Xunit;

namespace Miller.Tests;

/// <summary>
/// Unit coverage for <see cref="ScaleTestSupport.RepoRoot"/>'s repo-root resolution, in particular the
/// fallback chain added for continuous testing. CT runs Miller's test binary from an out-of-repo build
/// directory, so the
/// assembly-based walk (which starts from <c>AppContext.BaseDirectory</c>, outside the repo in that
/// scenario) fails. The cwd walk is also defeated under CT because xunit v3 resets the process working
/// directory to the test-assembly directory before tests execute, so the reliable channel is the
/// <see cref="CtEnvironment.WorkspaceRoot"/> environment variable every CT provider sets.
///
/// These tests exercise the pure helpers <see cref="ScaleTestSupport.LocateRepoRoot"/> and
/// <see cref="ScaleTestSupport.LocateRepoRootFromWorkspaceRoot"/> directly — the pieces <c>RepoRoot()</c>
/// composes — so the fallbacks are verified without faking <c>AppContext.BaseDirectory</c> (which C#
/// cannot do), the process's working directory, or the process-global environment (unsafe under xunit
/// v3's parallel collections).
/// </summary>
public sealed class ScaleTestSupportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BinarySelectionWithoutSourceKeepsThePlatformPin(string? source)
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-selection");
        string pinned = Path.Combine(root, ".tools", OperatingSystem.IsWindows() ? "julie-extract.exe" : "julie-extract");
        var examined = new List<string>();
        Assert.Equal(pinned, ScaleTestSupport.SelectJulieBinary(root, source, path =>
        {
            examined.Add(path);
            return path == pinned;
        }));
        Assert.Equal([pinned], examined);
    }

    [Fact]
    public void MissingPinWithoutSourceRemainsUnavailable()
    {
        Assert.Null(ScaleTestSupport.SelectJulieBinary(Path.GetTempPath(), null, _ => false));
    }

    [Fact]
    public void SelectedSourceWinsWithoutExaminingThePin()
    {
        string source = Path.Combine(Path.GetTempPath(), "miller-selection", "source-extractor");
        var examined = new List<string>();
        Assert.Equal(source, ScaleTestSupport.SelectJulieBinary(Path.GetTempPath(), source, path =>
        {
            examined.Add(path);
            return true;
        }));
        Assert.Equal([source], examined);
    }

    [Fact]
    public void MissingSelectedSourceRefusesRatherThanUsingThePin()
    {
        string source = Path.Combine(Path.GetTempPath(), "miller-selection", "missing-extractor");
        var examined = new List<string>();
        Assert.Throws<FileNotFoundException>(() => ScaleTestSupport.SelectJulieBinary(Path.GetTempPath(), source, path =>
        {
            examined.Add(path);
            return path != source;
        }));
        Assert.Equal([source], examined);
    }

    [Fact]
    public void RelativeSelectedSourceRefusesBeforeFilesystemChecks()
    {
        int examined = 0;
        Assert.Throws<ArgumentException>(() => ScaleTestSupport.SelectJulieBinary(Path.GetTempPath(), "relative-extractor", _ =>
        {
            examined++;
            return true;
        }));
        Assert.Equal(0, examined);
    }

    [Fact]
    public void LocateRepoRoot_ReturnsNull_WhenNoAncestorHasSlnx()
    {
        // A directory under the OS temp root has no Miller.slnx anywhere in its ancestry. The nested
        // segments need not exist on disk: LocateRepoRoot walks .Parent (pure path manipulation) and only
        // ever touches the filesystem to check for Miller.slnx, so no directories need to be created here.
        string start = Path.Combine(
            Path.GetTempPath(), "miller-repo-root-test-" + Guid.NewGuid().ToString("N"), "nested", "deeper");

        string? result = ScaleTestSupport.LocateRepoRoot(start);

        Assert.Null(result);
    }

    [Fact]
    public void LocateRepoRoot_FindsRepoRoot_FromCwdStyleStart()
    {
        // Ground truth: the assembly-based walk, which works in a normal (non-CT) test run.
        string expectedRepoRoot = ScaleTestSupport.RepoRoot();

        // A cwd-style start: nested directories under the real repo root that need not exist on disk
        // (see the null test above for why that's safe). This mimics Eros CT's cwd == repo root, just
        // walking from a few levels deeper to prove the walk climbs correctly either way.
        string cwdStyleStart = Path.Combine(expectedRepoRoot, "some", "nested", "cwd");

        string? result = ScaleTestSupport.LocateRepoRoot(cwdStyleStart);

        Assert.Equal(expectedRepoRoot, result);
    }

    [Fact]
    public void RepoRoot_StillSucceeds_InANormalTestRun()
    {
        // The assembly walk path: AppContext.BaseDirectory lives under the repo (bin/.../net10.0), so
        // RepoRoot() must resolve without needing the cwd fallback at all in a normal `dotnet test` run.
        string repoRoot = ScaleTestSupport.RepoRoot();

        Assert.True(File.Exists(Path.Combine(repoRoot, "Miller.slnx")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LocateRepoRootFromWorkspaceRoot_ReturnsNull_WhenValueBlank(string? workspaceRoot)
    {
        // The env-var fallback must no-op when the variable is unset or blank, so RepoRoot() falls
        // through to its final throw rather than walking up from an empty/garbage path.
        Assert.Null(ScaleTestSupport.LocateRepoRootFromWorkspaceRoot(workspaceRoot));
    }

    [Fact]
    public void LocateRepoRootFromWorkspaceRoot_ReturnsNull_WhenValueIsNotUnderRepo()
    {
        // A non-repo workspace root (e.g. pointing at an unrelated tree) resolves nothing.
        string outside = Path.Combine(
            Path.GetTempPath(), "miller-workspace-root-test-" + Guid.NewGuid().ToString("N"), "nested");

        Assert.Null(ScaleTestSupport.LocateRepoRootFromWorkspaceRoot(outside));
    }

    [Fact]
    public void LocateRepoRootFromWorkspaceRoot_FindsRepoRoot_WhenValuePointsIntoRepo()
    {
        // This is the CT path xunit v3's cwd reset defeats: the variable points into the repo, and
        // the env-var walk must resolve the repo root even though the assembly and cwd walks both miss.
        string expectedRepoRoot = ScaleTestSupport.RepoRoot();
        string workspaceRoot = Path.Combine(expectedRepoRoot, "some", "nested", "workspace");

        Assert.Equal(expectedRepoRoot, ScaleTestSupport.LocateRepoRootFromWorkspaceRoot(workspaceRoot));
    }

    /// <summary>
    /// The variable NAME is the contract, and getting it wrong is exactly what broke CT. Every provider sets
    /// <see cref="CtEnvironment.WorkspaceRoot"/> (<c>DotnetTestProvider.WorkspaceEnvironment</c>), while this
    /// helper read the retired <c>EROS_WORKSPACE_ROOT</c>. The two names never met, so the third fallback
    /// always returned null and roughly 50 tests threw "Could not locate repo root" under CT while passing
    /// everywhere else. The rename was required by docs/plans/2026-08-18-ct-sidecar-migration.md; only the
    /// producer side landed.
    /// </summary>
    [Fact]
    public void RepoRootFrom_ReadsTheWorkspaceRootVariableThatCtActuallySets()
    {
        string repoRoot = ScaleTestSupport.RepoRoot();
        string outsideTheRepo = Path.Combine(Path.GetTempPath(), "miller-oop-" + Guid.NewGuid().ToString("N"));
        var namesRead = new List<string>();

        string? resolved = ScaleTestSupport.RepoRootFrom(outsideTheRepo, outsideTheRepo, name =>
        {
            namesRead.Add(name);
            return string.Equals(name, CtEnvironment.WorkspaceRoot, StringComparison.Ordinal) ? repoRoot : null;
        });

        Assert.Equal(repoRoot, resolved);
        Assert.Equal([CtEnvironment.WorkspaceRoot], namesRead);
    }

    [Fact]
    public void RepoRootFrom_ReturnsNull_WhenBothWalksMissAndTheVariableIsUnset()
    {
        string outsideTheRepo = Path.Combine(Path.GetTempPath(), "miller-oop-" + Guid.NewGuid().ToString("N"));

        Assert.Null(ScaleTestSupport.RepoRootFrom(outsideTheRepo, outsideTheRepo, static _ => null));
    }
}
