using Xunit;

namespace Miller.Tests;

/// <summary>
/// Unit coverage for <see cref="ScaleTestSupport.RepoRoot"/>'s repo-root resolution, in particular the
/// fallback chain added for Eros CT. CT runs Miller's test binary from an out-of-repo sandbox, so the
/// assembly-based walk (which starts from <c>AppContext.BaseDirectory</c>, outside the repo in that
/// scenario) fails. The cwd walk is also defeated under CT because xunit v3 resets the process working
/// directory to the test-assembly directory before tests execute, so the reliable channel is the
/// <c>EROS_WORKSPACE_ROOT</c> environment variable CT always sets.
///
/// These tests exercise the pure helpers <see cref="ScaleTestSupport.LocateRepoRoot"/> and
/// <see cref="ScaleTestSupport.LocateRepoRootFromWorkspaceRoot"/> directly — the pieces <c>RepoRoot()</c>
/// composes — so the fallbacks are verified without faking <c>AppContext.BaseDirectory</c> (which C#
/// cannot do), the process's working directory, or the process-global environment (unsafe under xunit
/// v3's parallel collections).
/// </summary>
public sealed class ScaleTestSupportTests
{
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
        // The env-var fallback must no-op when EROS_WORKSPACE_ROOT is unset or blank, so RepoRoot() falls
        // through to its final throw rather than walking up from an empty/garbage path.
        Assert.Null(ScaleTestSupport.LocateRepoRootFromWorkspaceRoot(workspaceRoot));
    }

    [Fact]
    public void LocateRepoRootFromWorkspaceRoot_ReturnsNull_WhenValueIsNotUnderRepo()
    {
        // A non-repo EROS_WORKSPACE_ROOT (e.g. pointing at an unrelated tree) resolves nothing.
        string outside = Path.Combine(
            Path.GetTempPath(), "miller-workspace-root-test-" + Guid.NewGuid().ToString("N"), "nested");

        Assert.Null(ScaleTestSupport.LocateRepoRootFromWorkspaceRoot(outside));
    }

    [Fact]
    public void LocateRepoRootFromWorkspaceRoot_FindsRepoRoot_WhenValuePointsIntoRepo()
    {
        // This is the CT path xunit v3's cwd reset defeats: EROS_WORKSPACE_ROOT points into the repo, and
        // the env-var walk must resolve the repo root even though the assembly and cwd walks both miss.
        string expectedRepoRoot = ScaleTestSupport.RepoRoot();
        string workspaceRoot = Path.Combine(expectedRepoRoot, "some", "nested", "workspace");

        Assert.Equal(expectedRepoRoot, ScaleTestSupport.LocateRepoRootFromWorkspaceRoot(workspaceRoot));
    }
}
