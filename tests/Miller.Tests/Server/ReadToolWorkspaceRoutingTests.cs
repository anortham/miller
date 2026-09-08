using Miller.Server.Tools;
using Miller.Indexing;
using Miller.Indexing.Reads;
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

    [Theory]
    [InlineData(WorkspaceRegistryState.Ready)]
    [InlineData(WorkspaceRegistryState.Current)]
    [InlineData(WorkspaceRegistryState.LoadedExisting)]
    [InlineData(WorkspaceRegistryState.Refreshing)]
    public void HealthyRegistryWithoutSourceCheck_ReportsUnconfirmed(WorkspaceRegistryState state)
    {
        var row = new WorkspaceRegistryRow("ws", "display", "/root", "/root/.miller/symbols.db",
            DateTimeOffset.UnixEpoch, null, 7, state, null);
        Assert.Null(WorkspaceFreshnessView.IndexFreshFor(null, row));
        Assert.Equal("unconfirmed", WorkspaceFreshnessView.FreshnessStatusFor(null, row));
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("current")]
    [InlineData("loaded_existing")]
    [InlineData("refreshing")]
    public void CompactBanner_UnknownSourceFreshnessNeverClaimsReady(string status)
    {
        string? banner = ReadToolWorkspaceRouting.CompactBanner("display", "ws", "/root", null,
            status, 7, "ws", false);
        Assert.Contains("freshness: unconfirmed", banner);
    }

    [Theory]
    [InlineData(WorkspaceRegistryState.Stale)]
    [InlineData(WorkspaceRegistryState.Missing)]
    [InlineData(WorkspaceRegistryState.Error)]
    public void DegradedRegistryWithoutSourceCheck_PreservesItsDiagnostic(WorkspaceRegistryState state)
    {
        var row = new WorkspaceRegistryRow("ws", "display", "/root", "/root/.miller/symbols.db",
            DateTimeOffset.UnixEpoch, null, 7, state, null);
        Assert.False(WorkspaceFreshnessView.IndexFreshFor(null, row));
        Assert.Equal(row.StateText, WorkspaceFreshnessView.FreshnessStatusFor(null, row));
    }

    [Theory]
    [InlineData(WorkspaceRefreshStatus.LockBusy)]
    [InlineData(WorkspaceRefreshStatus.Queued)]
    public void RefreshWithoutSourceCheck_ReportsUnknownFreshness(WorkspaceRefreshStatus status)
    {
        var row = new WorkspaceRegistryRow("ws", "display", "/root", "db", DateTimeOffset.UnixEpoch,
            null, 7, WorkspaceRegistryState.Ready, null);
        var result = new WorkspaceRefreshResult(status, "ws", "/root", "db", Revision: 7);
        Assert.Null(WorkspaceFreshnessView.IndexFreshFor(result, row));
        Assert.StartsWith("unconfirmed", WorkspaceFreshnessView.FreshnessStatusFor(result, row));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ForegroundRefreshEvidence_IsRejectedWhenServedSnapshotChanged(bool family, bool revisionChanged)
    {
        var original = new WorkspaceReadSnapshot("/root", "ws", "generation-a", "view",
            new WorkspaceFreshnessToken("generation-a", 7), "full",
            family ? WorkspaceReadMode.FamilyStore : WorkspaceReadMode.LegacyArtifact);
        var served = revisionChanged
            ? original with { Freshness = original.Freshness with { Revision = 8 } }
            : original with { ArtifactOrStoreId = "generation-b" };
        var result = new WorkspaceRefreshResult(WorkspaceRefreshStatus.Refreshed, "ws", "/root", "db",
            Revision: 7, ArtifactId: original.ArtifactOrStoreId,
            IndexGenerationIdentity: original.IndexGenerationIdentity);
        Assert.Null(WorkspaceFreshnessView.ForServedSnapshot(result, served, reused: false));
    }

    [Fact]
    public void CompactBanner_IncludesWarningTextWhenPresent()
    {
        string? banner = ReadToolWorkspaceRouting.CompactBanner(
            displayId: "target-111111111111",
            workspaceId: "target-ws",
            workspaceRoot: "/target",
            indexFresh: true,
            freshnessStatus: "current",
            revision: 3,
            requestedWorkspaceId: "target-ws",
            json: false,
            warningText: "Background refresh failed");

        Assert.Equal("workspace: target-111111111111\nwarning: Background refresh failed", banner);
    }

    [Fact]
    public void CompactBanner_WithRefreshPending_ShowsFreshnessAndRevision()
    {
        string? banner = ReadToolWorkspaceRouting.CompactBanner(
            displayId: "target-111111111111",
            workspaceId: "target-ws",
            workspaceRoot: "/target",
            indexFresh: null,
            freshnessStatus: "refresh_pending",
            revision: 3,
            requestedWorkspaceId: "target-ws",
            json: false,
            warningText: null);

        Assert.Equal("workspace: target-111111111111\nfreshness: refresh_pending\nrevision: 3", banner);
    }
}
