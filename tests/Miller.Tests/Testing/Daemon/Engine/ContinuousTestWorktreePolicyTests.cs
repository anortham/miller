using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.Engine;

/// <summary>
/// Worktree inheritance for the CT opt-in. A linked worktree of an enabled main checkout counts as
/// enabled with zero manual calls; a local <c>ct.disabled</c> tombstone opts that worktree back out;
/// every non-worktree or broken git layout inherits nothing and fails closed to off. The probe stays
/// pure: it reads the filesystem and never creates a file or a directory.
///
/// <para>Fixtures build the real linked-worktree shape by hand — a <c>.git</c> FILE holding
/// <c>gitdir: &lt;admin dir&gt;</c> plus the admin dir's <c>commondir</c> pointer — with no git
/// subprocess, because that pointer-file layout is the contract
/// (<see cref="Miller.Indexing.GitWorktreeLayout"/>).</para>
/// </summary>
public sealed class ContinuousTestWorktreePolicyTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-wt-policy-").FullName;

    private string MainRoot => Path.Combine(_dir, "main");
    private string WorktreeRoot => Path.Combine(_dir, "wt");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Worktree_of_an_enabled_main_checkout_is_opted_in_and_the_probe_creates_nothing()
    {
        BuildLinkedWorktree();
        EnableMain();

        Assert.True(ContinuousTestPolicy.IsWorkspaceOptedIn(WorktreeRoot));
        Assert.False(Directory.Exists(Path.Combine(WorktreeRoot, ".miller")));
    }

    [Fact]
    public void Worktree_of_a_never_enabled_main_checkout_stays_off()
    {
        BuildLinkedWorktree();

        Assert.False(ContinuousTestPolicy.IsWorkspaceOptedIn(WorktreeRoot));
    }

    [Fact]
    public void Local_tombstone_beats_inherited_enable()
    {
        BuildLinkedWorktree();
        EnableMain();
        WriteMarker(ContinuousTestPolicy.DisabledMarkerPath(WorktreeRoot));

        Assert.False(ContinuousTestPolicy.IsWorkspaceOptedIn(WorktreeRoot));
    }

    [Fact]
    public void Local_tombstone_beats_a_local_enabled_marker()
    {
        Directory.CreateDirectory(WorktreeRoot);
        WriteMarker(ContinuousTestPolicy.EnabledMarkerPath(WorktreeRoot));
        WriteMarker(ContinuousTestPolicy.DisabledMarkerPath(WorktreeRoot));

        Assert.False(ContinuousTestPolicy.IsWorkspaceOptedIn(WorktreeRoot));
    }

    [Fact]
    public void Kill_switch_beats_inherited_enable()
    {
        BuildLinkedWorktree();
        EnableMain();

        Assert.True(ContinuousTestPolicy.ShouldConstructEngine(WorktreeRoot, killSwitch: null));
        Assert.False(ContinuousTestPolicy.ShouldConstructEngine(WorktreeRoot, killSwitch: "off"));
    }

    [Fact]
    public void A_normal_checkout_with_a_git_directory_inherits_nothing()
    {
        Directory.CreateDirectory(Path.Combine(MainRoot, ".git"));

        Assert.False(ContinuousTestPolicy.IsWorkspaceOptedIn(MainRoot));
    }

    [Fact]
    public void A_malformed_git_file_inherits_nothing()
    {
        BuildLinkedWorktree();
        EnableMain();
        File.WriteAllText(Path.Combine(WorktreeRoot, ".git"), "not a gitdir pointer\n");

        Assert.False(ContinuousTestPolicy.IsWorkspaceOptedIn(WorktreeRoot));
    }

    [Fact]
    public void A_dangling_gitdir_pointer_inherits_nothing()
    {
        BuildLinkedWorktree();
        EnableMain();
        File.WriteAllText(
            Path.Combine(WorktreeRoot, ".git"),
            $"gitdir: {Path.Combine(MainRoot, ".git", "worktrees", "gone")}\n");

        Assert.False(ContinuousTestPolicy.IsWorkspaceOptedIn(WorktreeRoot));
    }

    private void BuildLinkedWorktree()
    {
        string adminDir = Path.Combine(MainRoot, ".git", "worktrees", "wt");
        Directory.CreateDirectory(adminDir);
        File.WriteAllText(Path.Combine(adminDir, "commondir"), "../..\n");
        Directory.CreateDirectory(WorktreeRoot);
        File.WriteAllText(Path.Combine(WorktreeRoot, ".git"), $"gitdir: {adminDir}\n");
    }

    private void EnableMain() => WriteMarker(ContinuousTestPolicy.EnabledMarkerPath(MainRoot));

    private static void WriteMarker(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }
}
