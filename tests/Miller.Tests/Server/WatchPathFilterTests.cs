using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the watcher's path filter (m3-design §Components/3): it is LANGUAGE-AGNOSTIC — it never hand-picks
/// source extensions (the multi-language rule: a feature scopes to every capable language, and julie decides
/// what is indexable, not a hand-picked extension list). It only skips noise directories julie itself ignores:
/// version-control internals (<c>.git</c>/<c>.hg</c>/<c>.svn</c>), the Miller-owned <c>.miller</c> sidecar
/// (its own DB churn must not feed back as events), julie's <c>.julie</c> home, tool caches, and common build
/// output. WITHOUT a supported-extension set, any other path — a <c>.rs</c>, <c>.vue</c>, <c>.zig</c>, a file
/// with NO extension, a Dockerfile — is accepted, because julie's <c>update</c> no-ops harmlessly on a file it
/// does not index (verified-fact 2) and a hardcoded whitelist would silently drop a supported language. WITH
/// the set (julie's OWN claimed catalog from <c>languages --json</c>, injected here — fetching is the edge),
/// events for explicit extensions julie cannot parse are dropped before they spawn a subprocess; extensionless
/// paths remain fail-soft because the extension-only catalog cannot prove they are unsupported. A null/empty set
/// gates nothing.
///
/// <para>The skip set is matched against the path's segments BELOW the root, so cases here pin roots that
/// themselves carry a skip segment — <c>/repo/.worktrees/feature</c>, the agent-worktree convention. Pinning
/// only <c>Root = "/repo"</c> is what let a filter that rejected every file of every worktree workspace look
/// correct.</para>
/// </summary>
public sealed class WatchPathFilterTests
{
    private const string Root = "/repo";

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "miller-watch-filter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

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
    [InlineData("/repo/.vs/AccessIQ/FileContentIndex/cache.vsidx")]
    [InlineData(@"C:\source\AccessIQ\.vs\AccessIQ\FileContentIndex\8cfe21f6-17d4-4a7a-9a09-638f199e871b.vsidx")]
    [InlineData("/repo/node_modules/pkg/index.js")]
    [InlineData("/repo/target/debug/app")]      // rust build output
    [InlineData("/repo/bin/Debug/net10.0/x.dll")]
    [InlineData("/repo/obj/project.assets.json")]
    // Parity with julie-extract's hard-excluded dirs: the extractor refuses these regardless of ignore
    // files, so the watcher must not spawn subprocesses for them either.
    [InlineData("/repo/.hg/store/data/x.i")]
    [InlineData("/repo/.svn/pristine/ab/abc.svn-base")]
    [InlineData("/repo/.cache/build/some.o")]
    [InlineData("/repo/.julie/indexes/workspace/symbols.db")]
    [InlineData("/repo/.memories/2026-06-11-checkpoint.md")]
    public void Skips_NoiseDirectories(string path)
    {
        Assert.False(WatchPathFilter.ShouldProcess(Root, path));
    }

    // An empty-name FileSystemWatcher notification (a rename whose old-name record landed in the previous
    // buffer read) resolves back to the workspace root itself, and the root reached the extractor as
    // delete(<root>\) -> invalid_file_path (2026-08-12 triage). The root is not a file; never dispatch it.
    [Theory]
    [InlineData("/repo")]
    [InlineData("/repo/")]
    [InlineData("/repo/.")]
    public void Skips_TheWorkspaceRootItself(string path)
    {
        Assert.False(WatchPathFilter.ShouldProcess(Root, path));
    }

    [Fact]
    public void Skips_TheWorkspaceRootItself_EvenWithTheExtensionGateActive()
    {
        Assert.False(WatchPathFilter.ShouldProcess(Root, Root, new HashSet<string> { "cs" }));
    }

    [Fact]
    public void Skip_IsScopedToASegment_NotASubstring()
    {
        // A directory literally named ".github" or a file named "obj.cs" must NOT be skipped just because the
        // skip token appears as a substring — the match is on a whole path SEGMENT.
        Assert.True(WatchPathFilter.ShouldProcess(Root, "/repo/.github/workflows/ci.yml"));
        Assert.True(WatchPathFilter.ShouldProcess(Root, "/repo/src/object.cs"));
        Assert.True(WatchPathFilter.ShouldProcess(Root, "/repo/src/binformat.cs"));
        Assert.True(WatchPathFilter.ShouldProcess(Root, "/repo/src/caches.cs"));      // not ".cache"
        Assert.True(WatchPathFilter.ShouldProcess(Root, "/repo/memories/notes.md"));  // not ".memories"
    }

    [Theory]
    [InlineData("/repo/.claude/worktrees/example/src/A.cs")]
    [InlineData(@"/repo\.claude\worktrees\example\src\A.cs")]
    [InlineData(@"/repo\.claude/worktrees\example/src/A.cs")]
    public void Skips_ClaudeNestedWorktrees_WithEitherSlashStyle(string path)
    {
        Assert.False(WatchPathFilter.ShouldProcess(Root, path));
    }

    [Theory]
    [InlineData("/repo/.claude/prompts/example.md")]
    [InlineData("/repo/.claude/worktree/example/src/A.cs")]
    [InlineData("/repo/claude/worktrees/example/src/A.cs")]
    public void Accepts_ClaudePaths_OutsideTheNestedWorktreeDirectory(string path)
    {
        Assert.True(WatchPathFilter.ShouldProcess(Root, path));
    }

    [Theory]
    [InlineData("/repo/.worktrees/feature/src/A.cs")]
    [InlineData(@"/repo\.worktrees\feature\src\A.cs")]
    [InlineData("/repo/src/.worktrees/nested/B.cs")]
    public void Skips_RepoRootNestedWorktrees(string path)
    {
        Assert.False(WatchPathFilter.ShouldProcess(Root, path));
    }

    [Theory]
    [InlineData("/repo/docs/worktrees.md")]
    [InlineData("/repo/worktrees/feature/src/A.cs")]
    [InlineData("/repo/src/.worktrees.cs")]
    public void Accepts_PathsThatMerelyContainTheWorktreesToken(string path)
    {
        Assert.True(WatchPathFilter.ShouldProcess(Root, path));
    }

    [Theory]
    [InlineData("/repo/.worktrees/feature", "/repo/.worktrees/feature/src/A.cs")]
    [InlineData("/repo/.worktrees/feature", "/repo/.worktrees/feature/docs/readme.md")]
    [InlineData("/repo/.claude/worktrees/feature", "/repo/.claude/worktrees/feature/src/A.cs")]
    [InlineData("/repo/bin/tools", "/repo/bin/tools/src/A.cs")]
    [InlineData("/repo/obj", "/repo/obj/src/A.cs")]
    public void Accepts_FilesOfAWorkspaceWhoseOwnRootContainsASkipSegment(string root, string path)
    {
        Assert.True(WatchPathFilter.ShouldProcess(root, path));
    }

    [Theory]
    [InlineData("/repo", "/repo/.worktrees/feature/src/A.cs")]
    [InlineData("/repo/.worktrees/feature", "/repo/.worktrees/feature/.worktrees/inner/B.cs")]
    [InlineData("/repo/.worktrees/feature", "/repo/.worktrees/feature/node_modules/pkg/index.js")]
    [InlineData("/repo/.worktrees/feature", "/repo/.worktrees/feature/.miller/symbols.db")]
    [InlineData("/repo/.claude/worktrees/feature", "/repo/.claude/worktrees/feature/.claude/worktrees/in/B.cs")]
    public void Skips_SkipSegmentsBelowTheRoot_EvenWhenTheRootItselfContainsOne(string root, string path)
    {
        Assert.False(WatchPathFilter.ShouldProcess(root, path));
    }

    [Theory]
    [InlineData("/repo", "/repo/.worktrees/feature/.gitignore")]
    [InlineData("/repo", "/repo/.claude/worktrees/feature/.julieignore")]
    [InlineData("/repo", "/repo/node_modules/pkg/.gitignore")]
    public void ShouldForceRescan_PolicyFileInsideAnExcludedSubtree_CannotArmAWholeRepoScan(
        string root, string path)
    {
        Assert.False(WatchPathFilter.ShouldForceRescan(root, path));
    }

    [Theory]
    [InlineData("/repo/.worktrees/feature", "/repo/.worktrees/feature/.gitignore")]
    [InlineData("/repo/.worktrees/feature", "/repo/.worktrees/feature/src/.julieignore")]
    public void ShouldForceRescan_PolicyFileOfAWorkspaceRootedInsideAWorktreePool_StillArmsAScan(
        string root, string path)
    {
        Assert.True(WatchPathFilter.ShouldForceRescan(root, path));
    }

    [Fact]
    public void ClaudeNestedWorktrees_MixedCaseFollowsPlatformSegmentComparison()
    {
        bool expected = !OperatingSystem.IsWindows();

        Assert.Equal(
            expected,
            WatchPathFilter.ShouldProcess(Root, "/repo/.CLAUDE/WORKTREES/example/src/A.cs"));
    }

    // ---------- supported-extension gate (julie's claimed set, injected — pure, no process) ----------

    // A miniature stand-in for the `languages --json` catalog: lowercase, dot-less, case-insensitive —
    // exactly what JulieExtractRunner.ParseSupportedExtensions produces.
    private static readonly IReadOnlySet<string> Extensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs", "rs", "vue", "md" };

    [Theory]
    [InlineData("/repo/src/Main.cs")]
    [InlineData("/repo/core/math.rs")]
    [InlineData("/repo/ui/App.vue")]
    [InlineData("/repo/docs/readme.md")]
    [InlineData("/repo/src/Main.CS")]           // extension case never matters
    [InlineData("/repo/src/archive.tar.md")]    // last dot wins
    public void ExtensionGate_AcceptsClaimedExtensions(string path)
    {
        Assert.True(WatchPathFilter.ShouldProcess(Root, path, Extensions));
    }

    [Theory]
    [InlineData("/repo/daemon.log")]            // julie parses no logs — zero rows, wasted spawn
    [InlineData("/repo/assets/logo.png")]
    [InlineData("/repo/pkg/yarn.lock")]
    public void ExtensionGate_DropsUnclaimedExtensions(string path)
    {
        Assert.False(WatchPathFilter.ShouldProcess(Root, path, Extensions));
    }

    [Theory]
    [InlineData("/repo/Dockerfile")]            // extensionless: catalog cannot prove unsupported
    [InlineData("/repo/Makefile")]
    [InlineData("/repo/.env")]                  // dotfile = no extension
    [InlineData("/repo/src/trailingdot.")]
    public void ExtensionGate_KeepsExtensionlessPathsFailSoft(string path)
    {
        Assert.True(WatchPathFilter.ShouldProcess(Root, path, Extensions));
    }

    [Theory]
    // Fail soft: when the languages probe yielded nothing usable (null) — or, defensively, an EMPTY set
    // that would otherwise drop everything — the gate must gate NOTHING (the historical behavior).
    [InlineData("/repo/daemon.log")]
    [InlineData("/repo/Dockerfile")]
    [InlineData("/repo/k/main.zig")]
    public void ExtensionGate_NullOrEmptySet_GatesNothing(string path)
    {
        Assert.True(WatchPathFilter.ShouldProcess(Root, path, null));
        Assert.True(WatchPathFilter.ShouldProcess(Root, path, new HashSet<string>()));
    }

    [Fact]
    public void ExtensionGate_IgnorePolicyFiles_StillForceRescan_EvenThoughGateDropsThem()
    {
        using var temp = new TempDir();
        string gitignore = System.IO.Path.Combine(temp.Path, ".gitignore");
        string julieignore = System.IO.Path.Combine(temp.Path, ".julieignore");

        // The gate drops them from per-file extract dispatch (no supported extension)…
        Assert.False(WatchPathFilter.ShouldProcess(temp.Path, gitignore, Extensions));
        Assert.False(WatchPathFilter.ShouldProcess(temp.Path, julieignore, Extensions));

        // …but the watcher consults ShouldForceRescan FIRST, and that path is untouched by the gate.
        Assert.True(WatchPathFilter.ShouldForceRescan(temp.Path, gitignore));
        Assert.True(WatchPathFilter.ShouldForceRescan(temp.Path, julieignore));
    }

    [Fact]
    public void Skips_GitDir_AtAnyDepth()
    {
        // A nested submodule's .git directory is still VCS noise.
        Assert.False(WatchPathFilter.ShouldProcess(Root, "/repo/vendor/lib/.git/index"));
    }

    [Fact]
    public void ShouldProcess_RootGitignore_SkipsIgnoredFilesAndDirectories()
    {
        using var temp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, ".gitignore"), "ignored.rs\nignored_dir/\n*.log\n");

        Assert.False(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "ignored.rs")));
        Assert.False(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "ignored_dir", "a.cs")));
        Assert.False(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "daemon.log")));
        Assert.True(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "src", "keep.rs")));
    }

    [Fact]
    public void ShouldProcess_NestedGitignore_IsScopedToItsDirectory()
    {
        using var temp = new TempDir();
        string sub = System.IO.Path.Combine(temp.Path, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(System.IO.Path.Combine(sub, ".gitignore"), "local_only/\n");

        Assert.False(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(sub, "local_only", "a.rs")));
        Assert.True(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "local_only", "a.rs")));
    }

    [Fact]
    public void ShouldProcess_Julieignore_SkipsIgnoredFiles()
    {
        using var temp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, ".julieignore"), "julie_ignored.rs\njulie_dir/\n");

        Assert.False(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "julie_ignored.rs")));
        Assert.False(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "julie_dir", "a.rs")));
        Assert.True(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "src", "keep.rs")));
    }

    [Fact]
    public void ShouldProcess_AncestorGitignoreAboveWorkspaceRoot_IsInherited()
    {
        using var temp = new TempDir();
        string repo = System.IO.Path.Combine(temp.Path, "repo");
        string workspace = System.IO.Path.Combine(repo, "packages", "app");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(System.IO.Path.Combine(repo, ".git"), "gitdir: .git/worktrees/app\n");
        File.WriteAllText(System.IO.Path.Combine(repo, ".gitignore"), "private_data/\n");

        Assert.False(WatchPathFilter.ShouldProcess(
            workspace,
            System.IO.Path.Combine(workspace, "private_data", "secret.rs")));
        Assert.True(WatchPathFilter.ShouldProcess(workspace, System.IO.Path.Combine(workspace, "src", "keep.rs")));
    }

    [Fact]
    public void ShouldProcess_GitignoreNegation_ReincludesFile()
    {
        using var temp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(temp.Path, ".gitignore"), "*.cs\n!keep.cs\n");

        Assert.False(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "drop.cs")));
        Assert.True(WatchPathFilter.ShouldProcess(temp.Path, System.IO.Path.Combine(temp.Path, "keep.cs")));
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".julieignore")]
    [InlineData("sub/.gitignore")]
    [InlineData("sub/.julieignore")]
    public void ShouldForceRescan_IgnoresPolicyFileChanges(string relativePath)
    {
        using var temp = new TempDir();
        string path = System.IO.Path.Combine(temp.Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        Assert.True(WatchPathFilter.ShouldForceRescan(temp.Path, path));
    }

    [Fact]
    public void AncestorGitignoreFilesOutsideRoot_IncludesGitRootAndIntermediateParents()
    {
        using var temp = new TempDir();
        string repo = System.IO.Path.Combine(temp.Path, "repo");
        string workspace = System.IO.Path.Combine(repo, "packages", "app");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(System.IO.Path.Combine(repo, ".git"), "gitdir: .git/worktrees/app\n");

        string[] files = WorkspaceIgnorePolicy.AncestorGitignoreFilesOutsideRoot(workspace).ToArray();

        Assert.Equal(
            new[]
            {
                System.IO.Path.Combine(repo, ".gitignore"),
                System.IO.Path.Combine(repo, "packages", ".gitignore"),
            },
            files);
    }
}
