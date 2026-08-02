using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the linked-worktree resolution that <c>Directory.Exists(root + "/.git")</c> gets wrong. A worktree
/// created by <c>git worktree add</c> has a <c>.git</c> FILE, so the old test reported "not a git repo" and
/// took every consumer down the no-git path: no HEAD watch (branch switches degraded into watcher-buffer
/// overflow rescans) and no way to find the main checkout's ignore policy.
/// </summary>
public sealed class GitWorktreeLayoutTests : IDisposable
{
    private readonly string _dir;

    public GitWorktreeLayoutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-git-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string NewDirectory(params string[] segments)
    {
        string path = Path.Combine(new[] { _dir }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ResolveTreatsADotGitDirectoryAsTheMainCheckout()
    {
        string root = NewDirectory("repo");
        string gitDir = NewDirectory("repo", ".git");

        var layout = GitWorktreeLayout.Resolve(root);

        Assert.NotNull(layout);
        Assert.Equal(gitDir, layout.GitDir);
        Assert.Equal(gitDir, layout.CommonDir);
        Assert.Equal(root, layout.MainCheckoutRoot);
        Assert.False(layout.IsLinkedWorktree);
    }

    [Fact]
    public void ResolveFollowsADotGitFileToTheLinkedWorktreeGitDir()
    {
        string main = NewDirectory("repo");
        string worktreeGitDir = NewDirectory("repo", ".git", "worktrees", "feature");
        string worktree = NewDirectory("wt-feature");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}\n");
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..\n");

        var layout = GitWorktreeLayout.Resolve(worktree);

        Assert.NotNull(layout);
        Assert.Equal(worktreeGitDir, layout.GitDir);
        Assert.Equal(Path.Combine(main, ".git"), layout.CommonDir);
        Assert.Equal(main, layout.MainCheckoutRoot);
        Assert.True(layout.IsLinkedWorktree);
    }

    [Fact]
    public void ResolveReturnsNullWhenTheRootHasNoGitEntry()
    {
        Assert.Null(GitWorktreeLayout.Resolve(NewDirectory("plain")));
    }

    [Fact]
    public void ResolveReturnsNullWhenTheDotGitFilePointsAtAMissingDirectory()
    {
        string worktree = NewDirectory("orphan");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(_dir, "gone")}\n");

        Assert.Null(GitWorktreeLayout.Resolve(worktree));
    }

    [Fact]
    public void ResolveFallsBackToTheGitDirWhenNoCommondirFileExists()
    {
        string gitDir = NewDirectory("submodule-style", "modules", "child");
        string root = NewDirectory("child-checkout");
        File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {gitDir}\n");

        var layout = GitWorktreeLayout.Resolve(root);

        Assert.NotNull(layout);
        Assert.Equal(gitDir, layout.CommonDir);
        Assert.False(layout.IsLinkedWorktree);
    }

    [Fact]
    public void ResolveIgnoresACommondirPointingAtAMissingDirectory()
    {
        string gitDir = NewDirectory("repo", ".git", "worktrees", "feature");
        string root = NewDirectory("wt");
        File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {gitDir}\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), Path.Combine(_dir, "vanished"));

        var layout = GitWorktreeLayout.Resolve(root);

        Assert.NotNull(layout);
        Assert.Equal(gitDir, layout.CommonDir);
    }

    [Fact]
    public void ParseGitFileResolvesARelativeGitdirAgainstTheWorkingTree()
    {
        Assert.Equal(
            Path.GetFullPath(Path.Combine("/repo", ".git", "worktrees", "wt")),
            GitWorktreeLayout.ParseGitFile("gitdir: .git/worktrees/wt", "/repo"));
    }

    [Fact]
    public void ParseGitFileToleratesSurroundingWhitespaceAndCrLf()
    {
        Assert.Equal(
            Path.GetFullPath("/elsewhere/gd"),
            GitWorktreeLayout.ParseGitFile("  gitdir:   /elsewhere/gd  \r\n", "/repo"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("gitdir:")]
    [InlineData("gitdir:   ")]
    [InlineData("not a gitdir line\n")]
    public void ParseGitFileReturnsNullWithoutAUsableGitdirLine(string contents)
    {
        Assert.Null(GitWorktreeLayout.ParseGitFile(contents, "/repo"));
    }

    [Fact]
    public void ParseCommonDirFileResolvesRelativeToTheGitDir()
    {
        Assert.Equal(
            Path.GetFullPath("/repo/.git"),
            GitWorktreeLayout.ParseCommonDirFile("../..\n", "/repo/.git/worktrees/wt"));
    }

    [Fact]
    public void ParseCommonDirFileKeepsAnAbsolutePathAsWritten()
    {
        Assert.Equal(
            Path.GetFullPath("/shared/repo.git"),
            GitWorktreeLayout.ParseCommonDirFile("/shared/repo.git", "/repo/.git/worktrees/wt"));
    }

    [Fact]
    public void ParseCommonDirFileReturnsNullWhenBlank()
    {
        Assert.Null(GitWorktreeLayout.ParseCommonDirFile("\n\n", "/repo/.git/worktrees/wt"));
    }

    [Fact]
    public void MainCheckoutRootForReturnsNullForABareRepository()
    {
        Assert.Null(GitWorktreeLayout.MainCheckoutRootFor("/srv/repos/project.git"));
    }

    [Fact]
    public void MainCheckoutRootForStripsATrailingSeparator()
    {
        Assert.Equal(
            Path.GetFullPath("/repo"),
            GitWorktreeLayout.MainCheckoutRootFor("/repo/.git" + Path.DirectorySeparatorChar));
    }
}
