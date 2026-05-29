using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the watcher's path filter (m3-design §Components/3): it is LANGUAGE-AGNOSTIC — it never whitelists
/// source extensions (the multi-language rule: a feature scopes to every capable language, and julie decides
/// what is indexable, not a hand-picked extension list). It only skips noise directories julie itself ignores:
/// version-control internals (<c>.git</c>), the Miller-owned <c>.miller</c> sidecar (its own DB churn must not
/// feed back as events), and common build output. Any other path — a <c>.rs</c>, <c>.vue</c>, <c>.zig</c>, a
/// file with NO extension, a Dockerfile — is accepted, because julie's <c>update</c> no-ops harmlessly on a
/// file it does not index (verified-fact 2) and an extension whitelist would silently drop a supported language.
/// </summary>
public sealed class WatchPathFilterTests
{
    private const string Root = "/repo";

    [Theory]
    // Accepted: every plausible source path, regardless of language or extension, including none.
    [InlineData("/repo/src/Main.cs")]
    [InlineData("/repo/core/math.rs")]
    [InlineData("/repo/ui/App.vue")]
    [InlineData("/repo/k/main.zig")]
    [InlineData("/repo/scripts/build.sh")]
    [InlineData("/repo/Dockerfile")]            // no extension — must NOT be dropped
    [InlineData("/repo/Makefile")]              // no extension
    [InlineData("/repo/docs/readme.md")]
    [InlineData("/repo/deep/nested/dir/x.py")]
    public void Accepts_AnySourcePath_LanguageAgnostic(string path)
    {
        Assert.True(WatchPathFilter.ShouldProcess(Root, path));
    }

    [Theory]
    // Skipped: version-control internals + Miller's own sidecar + common build output dirs.
    [InlineData("/repo/.git/HEAD")]
    [InlineData("/repo/.git/objects/ab/cdef")]
    [InlineData("/repo/.miller/symbols.db")]    // our own DB — would feed back as events
    [InlineData("/repo/.miller/symbols.db-wal")]
    [InlineData("/repo/.miller/logs/miller-.log")]
    [InlineData("/repo/node_modules/pkg/index.js")]
    [InlineData("/repo/target/debug/app")]      // rust build output
    [InlineData("/repo/bin/Debug/net10.0/x.dll")]
    [InlineData("/repo/obj/project.assets.json")]
    public void Skips_NoiseDirectories(string path)
    {
        Assert.False(WatchPathFilter.ShouldProcess(Root, path));
    }

    [Fact]
    public void Skip_IsScopedToASegment_NotASubstring()
    {
        // A directory literally named ".github" or a file named "obj.cs" must NOT be skipped just because the
        // skip token appears as a substring — the match is on a whole path SEGMENT.
        Assert.True(WatchPathFilter.ShouldProcess(Root, "/repo/.github/workflows/ci.yml"));
        Assert.True(WatchPathFilter.ShouldProcess(Root, "/repo/src/object.cs"));
        Assert.True(WatchPathFilter.ShouldProcess(Root, "/repo/src/binformat.cs"));
    }

    [Fact]
    public void Skips_GitDir_AtAnyDepth()
    {
        // A nested submodule's .git directory is still VCS noise.
        Assert.False(WatchPathFilter.ShouldProcess(Root, "/repo/vendor/lib/.git/index"));
    }
}
