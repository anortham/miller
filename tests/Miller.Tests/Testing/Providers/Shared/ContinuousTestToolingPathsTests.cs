using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Shared;

public sealed class ContinuousTestToolingPathsTests
{
    [Theory]
    [InlineData(".git/config")]
    [InlineData(".miller/index.db")]
    [InlineData(".julie/logs/run.log")]
    [InlineData("target/debug/app")]
    [InlineData("node_modules/left-pad/index.js")]
    [InlineData("src/App.Tests/bin/Debug/App.dll")]
    [InlineData("src/App/obj/project.assets.json")]
    [InlineData(".vs/config/applicationhost.config")]
    [InlineData("dist/bundle.js")]
    public void IsToolingPath_true_for_every_tooling_directory_in_the_set(string path)
    {
        Assert.True(ContinuousTestToolingPaths.IsToolingPath(path));
    }

    [Fact]
    public void IsToolingPath_true_for_miller_ct_temp_namespace()
    {
        var path = $"{CtTempPaths.RootDirectoryName}/04a112c00503/socket";
        Assert.True(ContinuousTestToolingPaths.IsToolingPath(path));
        Assert.Equal("miller-ct", CtTempPaths.RootDirectoryName);
    }

    [Theory]
    [InlineData("src/App.cs")]
    [InlineData("tests/AppTests.cs")]
    [InlineData("docs/plans/design.md")]
    [InlineData("Cargo.toml")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsToolingPath_false_for_normal_source_and_blank_paths(string path)
    {
        Assert.False(ContinuousTestToolingPaths.IsToolingPath(path));
    }

    [Fact]
    public void IsToolingPath_matches_windows_backslash_segments()
    {
        Assert.True(ContinuousTestToolingPaths.IsToolingPath(@"src\App.Tests\bin\Debug\App.dll"));
        Assert.False(ContinuousTestToolingPaths.IsToolingPath(@"src\App\Program.cs"));
    }

    [Fact]
    public void IsToolingPath_matches_tooling_segment_anywhere_in_the_path()
    {
        Assert.True(ContinuousTestToolingPaths.IsToolingPath("workspace/.git/HEAD"));
        Assert.True(ContinuousTestToolingPaths.IsToolingPath("a/b/node_modules/c/d.js"));
    }

    [Fact]
    public void Partition_splits_kept_and_dropped_preserving_order()
    {
        var (kept, dropped) = ContinuousTestToolingPaths.Partition(
        [
            "src/App.cs",
            ".git/index",
            "tests/AppTests.cs",
            "target/debug/x",
            "docs/readme.md",
        ]);

        Assert.Equal(["src/App.cs", "tests/AppTests.cs", "docs/readme.md"], kept.ToArray());
        Assert.Equal([".git/index", "target/debug/x"], dropped.ToArray());
    }

    [Fact]
    public void Partition_drops_all_when_every_path_is_tooling()
    {
        var (kept, dropped) = ContinuousTestToolingPaths.Partition(
        [
            ".miller/index.db",
            "target/debug/x",
            ".julie/logs/run.log",
        ]);

        Assert.Empty(kept);
        Assert.Equal([".miller/index.db", "target/debug/x", ".julie/logs/run.log"], dropped.ToArray());
    }

    [Fact]
    public void Partition_skips_blank_entries()
    {
        var (kept, dropped) = ContinuousTestToolingPaths.Partition(["", "   ", "src/App.cs"]);

        Assert.Equal(["src/App.cs"], kept.ToArray());
        Assert.Empty(dropped);
    }
}
