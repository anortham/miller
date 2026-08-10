using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the generation signal that makes path reuse observable. Miller's stable workspace_id is a hash of the
/// canonical ROOT PATH, so <c>git worktree remove wt &amp;&amp; git worktree add wt other-branch</c> produces an
/// identical id, registry row, and artifact root_path — the git administrative directory is what differs.
/// </summary>
public sealed class WorkspaceRootIdentityTests : IDisposable
{
    private readonly string _dir;

    public WorkspaceRootIdentityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-root-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void CaptureReadsTheAdminDirectoryOfALinkedWorktree()
    {
        string adminDir = AdminDir("feature");
        string worktree = LinkedWorktree("wt", adminDir);

        WorkspaceRootIdentity identity = WorkspaceRootIdentity.Capture(worktree);

        Assert.True(identity.IsKnown);
        Assert.Equal(adminDir, identity.GitDir);
    }

    [Fact]
    public void CaptureReadsTheDotGitDirectoryOfANormalCheckout()
    {
        string root = NewDirectory("repo");
        string gitDir = NewDirectory("repo", ".git");

        WorkspaceRootIdentity identity = WorkspaceRootIdentity.Capture(root);

        Assert.True(identity.IsKnown);
        Assert.Equal(gitDir, identity.GitDir);
    }

    [Fact]
    public void CaptureReturnsUnknownForADirectoryWithNoGitLayout()
    {
        Assert.False(WorkspaceRootIdentity.Capture(NewDirectory("plain")).IsKnown);
    }

    [Fact]
    public void AWorktreeRemovedAndReAddedAtTheSamePathIsAReplacement()
    {
        string adminDir = AdminDir("wt");
        string worktree = LinkedWorktree("wt", adminDir);
        WorkspaceRootIdentity before = WorkspaceRootIdentity.Capture(worktree);

        Directory.Delete(worktree, recursive: true);
        Directory.Delete(adminDir, recursive: true);
        LinkedWorktree("wt", AdminDir("wt"));

        Assert.True(WorkspaceRootIdentity.IsReplacement(before, WorkspaceRootIdentity.Capture(worktree)));
    }

    [Fact]
    public void AWorktreeReAddedUnderADifferentNameIsAReplacement()
    {
        string worktree = LinkedWorktree("wt", AdminDir("feature"));
        WorkspaceRootIdentity before = WorkspaceRootIdentity.Capture(worktree);

        Directory.Delete(worktree, recursive: true);
        LinkedWorktree("wt", AdminDir("other-branch"));

        Assert.True(WorkspaceRootIdentity.IsReplacement(before, WorkspaceRootIdentity.Capture(worktree)));
    }

    [Fact]
    public void TheSameWorktreeReadTwiceIsNotAReplacement()
    {
        string worktree = LinkedWorktree("wt", AdminDir("feature"));

        Assert.False(WorkspaceRootIdentity.IsReplacement(
            WorkspaceRootIdentity.Capture(worktree), WorkspaceRootIdentity.Capture(worktree)));
    }

    [Fact]
    public void LinuxGitMetadataChangesDoNotAlterTheCapturedIdentity()
    {
        if (!OperatingSystem.IsLinux())
            return;

        string root = NewDirectory("repo");
        string gitDir = NewDirectory("repo", ".git");
        WorkspaceRootIdentity before = WorkspaceRootIdentity.Capture(root);

        Thread.Sleep(10);
        File.WriteAllText(Path.Combine(gitDir, "index"), "changed");

        Assert.Equal(before, WorkspaceRootIdentity.Capture(root));
    }

    [Fact]
    public void ARootRecreatedAroundASurvivingAdminDirectoryIsNotAReplacement()
    {
        string adminDir = AdminDir("feature");
        string worktree = LinkedWorktree("wt", adminDir);
        WorkspaceRootIdentity before = WorkspaceRootIdentity.Capture(worktree);

        Directory.Delete(worktree, recursive: true);
        LinkedWorktree("wt", adminDir);

        Assert.False(WorkspaceRootIdentity.IsReplacement(before, WorkspaceRootIdentity.Capture(worktree)));
    }

    [Fact]
    public void ADifferentCreationTimestampAtTheSameAdminPathIsAReplacement()
    {
        var before = new WorkspaceRootIdentity("/repo/.git/worktrees/wt", DateTimeOffset.UnixEpoch);
        var after = new WorkspaceRootIdentity("/repo/.git/worktrees/wt", DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.True(WorkspaceRootIdentity.IsReplacement(before, after));
    }

    [Fact]
    public void AnUnknownIdentityOnEitherSideIsNeverAReplacement()
    {
        var known = new WorkspaceRootIdentity("/repo/.git", DateTimeOffset.UnixEpoch);

        Assert.False(WorkspaceRootIdentity.IsReplacement(WorkspaceRootIdentity.Unknown, known));
        Assert.False(WorkspaceRootIdentity.IsReplacement(known, WorkspaceRootIdentity.Unknown));
        Assert.False(WorkspaceRootIdentity.IsReplacement(
            new WorkspaceRootIdentity("/repo/.git", null), new WorkspaceRootIdentity("/other/.git", null)));
    }

    private string NewDirectory(params string[] segments)
    {
        string path = Path.Combine(new[] { _dir }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return Path.GetFullPath(path);
    }

    private string AdminDir(string name)
    {
        string adminDir = NewDirectory("repo", ".git", "worktrees", name);
        File.WriteAllText(Path.Combine(adminDir, "commondir"), "../..\n");
        return adminDir;
    }

    private string LinkedWorktree(string name, string adminDir)
    {
        string worktree = NewDirectory(name);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {adminDir}\n");
        return worktree;
    }
}
