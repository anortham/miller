using Xunit;

namespace Miller.Tests;

/// <summary>
/// Unit coverage for <see cref="ScaleTestSupport.RepoRoot"/>'s repo-root resolution, in particular the
/// cwd fallback added for Eros CT: CT runs Miller's test binary from an out-of-repo sandbox with the
/// working directory set to the Miller repo root, so the assembly-based walk (which starts from
/// <c>AppContext.BaseDirectory</c>, outside the repo in that scenario) fails, and a second walk from
/// <c>Directory.GetCurrentDirectory()</c> must succeed instead.
///
/// These tests exercise <see cref="ScaleTestSupport.LocateRepoRoot"/> directly — the single-walk helper
/// that <c>RepoRoot()</c> composes twice — so the fallback is verified without faking
/// <c>AppContext.BaseDirectory</c> (which C# cannot do) or the process's actual working directory.
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
}
