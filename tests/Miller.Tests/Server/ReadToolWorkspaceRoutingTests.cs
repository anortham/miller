using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// The read tools' <c>ensure_fresh</c> contract. Only the DEFAULT for an explicit <c>workspace_id</c> changed
/// (2026-08-21): it used to be a foreground refresh every caller waited on (measured p50 ~2.9s, p95 20s+), and it
/// is now a serve-then-refresh. The two EXPLICIT answers are unchanged.
/// </summary>
public sealed class ReadToolWorkspaceRoutingTests
{
    [Fact]
    public void ResolveRefreshMode_NoWorkspaceId_DefaultsToNoRefresh()
    {
        Assert.Equal(
            WorkspaceRefreshMode.None,
            ReadToolWorkspaceRouting.ResolveRefreshMode(workspaceId: null, ensureFresh: null));
    }

    [Fact]
    public void ResolveRefreshMode_NoWorkspaceIdButExplicitTrue_Blocks()
    {
        Assert.Equal(
            WorkspaceRefreshMode.Blocking,
            ReadToolWorkspaceRouting.ResolveRefreshMode(workspaceId: null, ensureFresh: true));
    }

    [Fact]
    public void ResolveRefreshMode_NoWorkspaceIdButExplicitFalse_DoesNoRefresh()
    {
        Assert.Equal(
            WorkspaceRefreshMode.None,
            ReadToolWorkspaceRouting.ResolveRefreshMode(workspaceId: null, ensureFresh: false));
    }

    [Fact]
    public void ResolveRefreshMode_ExplicitWorkspaceId_DefaultsToBackground()
    {
        Assert.Equal(
            WorkspaceRefreshMode.Background,
            ReadToolWorkspaceRouting.ResolveRefreshMode("target-ws", ensureFresh: null));
    }

    [Fact]
    public void ResolveRefreshMode_ExplicitWorkspaceIdWithEnsureFreshTrue_StillBlocks()
    {
        Assert.Equal(
            WorkspaceRefreshMode.Blocking,
            ReadToolWorkspaceRouting.ResolveRefreshMode("target-ws", ensureFresh: true));
    }

    [Fact]
    public void ResolveRefreshMode_ExplicitWorkspaceIdWithEnsureFreshFalse_DoesNoRefresh()
    {
        Assert.Equal(
            WorkspaceRefreshMode.None,
            ReadToolWorkspaceRouting.ResolveRefreshMode("target-ws", ensureFresh: false));
    }
}
