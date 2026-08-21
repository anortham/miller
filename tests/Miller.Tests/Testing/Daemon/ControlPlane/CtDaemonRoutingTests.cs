using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Daemon.ControlPlane;

/// <summary>
/// A <c>tests</c> verb issued against a linked worktree must find the FAMILY daemon: the one
/// holding the lease on the repo's main checkout. The worktree's own lease, when live, always
/// wins - a worktree running its own daemon is not adopted and not routed away.
/// </summary>
public sealed class CtDaemonRoutingTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-route-").FullName;

    private string MainRoot => Path.Combine(_dir, "main");
    private string WorktreeRoot => Path.Combine(_dir, "wt");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_worktree_with_no_daemon_anywhere_resolves_no_endpoint()
    {
        BuildLinkedWorktree();

        Assert.Null(CtDaemonRouting.ResolveLiveEndpoint(WorktreeRoot));
        // The probe creates nothing.
        Assert.False(Directory.Exists(Path.Combine(WorktreeRoot, ".miller")));
    }

    [Fact]
    public void A_worktree_resolves_the_live_family_daemon_on_the_main_checkout()
    {
        BuildLinkedWorktree();
        using CtDaemonLease? family = CtDaemonLease.TryAcquire(MainRoot, "test");
        Assert.NotNull(family);

        CtDaemonEndpoint? endpoint = CtDaemonRouting.ResolveLiveEndpoint(WorktreeRoot);

        Assert.NotNull(endpoint);
        Assert.Equal(Path.GetFullPath(MainRoot), endpoint.EndpointRoot);
        Assert.True(endpoint.Adopting);
        Assert.Equal(family.Record.Identity, endpoint.Lease.Identity);
    }

    [Fact]
    public void A_worktrees_own_live_daemon_beats_the_family_daemon()
    {
        BuildLinkedWorktree();
        using CtDaemonLease? family = CtDaemonLease.TryAcquire(MainRoot, "test");
        using CtDaemonLease? own = CtDaemonLease.TryAcquire(WorktreeRoot, "test");
        Assert.NotNull(family);
        Assert.NotNull(own);

        CtDaemonEndpoint? endpoint = CtDaemonRouting.ResolveLiveEndpoint(WorktreeRoot);

        Assert.NotNull(endpoint);
        Assert.Equal(Path.GetFullPath(WorktreeRoot), endpoint.EndpointRoot);
        Assert.False(endpoint.Adopting);
    }

    [Fact]
    public void ResolveRun_from_a_worktree_names_the_family_endpoint()
    {
        BuildLinkedWorktree();
        using CtDaemonLease? family = CtDaemonLease.TryAcquire(MainRoot, "test");
        Assert.NotNull(family);

        CtRunDisposition disposition = CtDaemonLauncher.ResolveRun(WorktreeRoot);

        Assert.Equal(CtRunExecution.Daemon, disposition.Execution);
        Assert.Equal(Path.GetFullPath(MainRoot), disposition.EndpointRoot);
    }

    [Fact]
    public void A_routed_request_lands_at_the_endpoint_and_carries_the_target_workspace()
    {
        BuildLinkedWorktree();

        CtDaemonCommandRequest request = CtDaemonRouting.WriteRoutedRequest(
            MainRoot,
            CtDaemonCommandKind.Stop,
            reason: "detach",
            freshness: null,
            targetWorkspaceRoot: WorktreeRoot);

        Assert.True(File.Exists(CtDaemonProtocol.CommandRequestPath(MainRoot, request.CommandId)));
        CtDaemonCommandRequest? read = CtCommandChannel.TryReadRequest(MainRoot, request.CommandId);
        Assert.Equal(Path.GetFullPath(WorktreeRoot), read?.WorkspaceRoot);
        Assert.Equal(CtDaemonCommandKind.Stop, read?.Kind);
        // Nothing was written under the worktree itself.
        Assert.False(Directory.Exists(Path.Combine(WorktreeRoot, ".miller")));
    }

    [Fact]
    public void A_request_file_without_a_workspace_field_still_reads_as_a_primary_command()
    {
        BuildLinkedWorktree();
        CtDaemonCommandRequest legacy = CtCommandChannel.WriteRequest(
            MainRoot,
            CtDaemonCommandKind.Run,
            reason: "run",
            freshness: null);

        CtDaemonCommandRequest? read = CtCommandChannel.TryReadRequest(MainRoot, legacy.CommandId);

        Assert.NotNull(read);
        Assert.Null(read.WorkspaceRoot);
    }

    [Fact]
    public void ResolveSpawnRoot_anchors_a_worktree_of_an_opted_in_main_at_the_main_checkout()
    {
        BuildLinkedWorktree();
        WriteMarker(ContinuousTestPolicy.EnabledMarkerPath(MainRoot));

        Assert.Equal(Path.GetFullPath(MainRoot), CtDaemonLauncher.ResolveSpawnRoot(WorktreeRoot));
    }

    [Fact]
    public void ResolveSpawnRoot_keeps_a_worktree_of_a_never_enabled_main_on_its_own_root()
    {
        BuildLinkedWorktree();

        Assert.Equal(Path.GetFullPath(WorktreeRoot), CtDaemonLauncher.ResolveSpawnRoot(WorktreeRoot));
    }

    [Fact]
    public void ResolveSpawnRoot_keeps_a_plain_checkout_on_its_own_root()
    {
        Directory.CreateDirectory(Path.Combine(MainRoot, ".git"));

        Assert.Equal(Path.GetFullPath(MainRoot), CtDaemonLauncher.ResolveSpawnRoot(MainRoot));
    }

    private void BuildLinkedWorktree()
    {
        string adminDir = Path.Combine(MainRoot, ".git", "worktrees", "wt");
        Directory.CreateDirectory(adminDir);
        File.WriteAllText(Path.Combine(adminDir, "commondir"), "../..\n");
        Directory.CreateDirectory(WorktreeRoot);
        File.WriteAllText(Path.Combine(WorktreeRoot, ".git"), $"gitdir: {adminDir}\n");
    }

    private static void WriteMarker(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }
}
