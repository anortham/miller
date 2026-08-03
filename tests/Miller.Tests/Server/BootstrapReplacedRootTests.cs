using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins how a replaced root folds into the artifact decision. The previous occupant's artifact records a
/// <c>root_path</c> that still matches (workspace identity is the canonical path), so the unmodified decision is
/// "reuse" — which would serve a removed worktree's symbols under the new one's name.
/// </summary>
public sealed class BootstrapReplacedRootTests
{
    [Fact]
    public void AReusableArtifactIsEscalatedToAForcedRootRebind()
    {
        var reuse = IndexBootstrapService.DecideBootstrapScan(
            dbExists: true, existingRootPath: "/repo/wt", canonicalRoot: "/repo/wt", hasCommittedRevision: true);

        var escalated = IndexBootstrapService.EscalateForReplacedRoot(reuse);

        Assert.False(reuse.ShouldScan);
        Assert.Equal(WorkspaceRegistryState.LoadedExisting, reuse.RegistryStateAfterLoad);
        Assert.True(escalated.ShouldScan);
        Assert.Equal(ScanIntent.RootRebind, escalated.Intent);
        Assert.True(escalated.Force);
        Assert.Equal(WorkspaceRegistryState.Ready, escalated.RegistryStateAfterLoad);
    }

    [Fact]
    public void AMissingArtifactIsStillEscalatedToAForcedRootRebind()
    {
        var firstScan = IndexBootstrapService.DecideBootstrapScan(
            dbExists: false, existingRootPath: null, canonicalRoot: "/repo/wt", hasCommittedRevision: false);

        var escalated = IndexBootstrapService.EscalateForReplacedRoot(firstScan);

        Assert.Equal(ScanIntent.RootRebind, escalated.Intent);
        Assert.Equal(WorkspaceRegistryState.Ready, escalated.RegistryStateAfterLoad);
    }

    [Fact]
    public void ARepairIntentSurvivesTheEscalation()
    {
        var corrupt = new IndexBootstrapService.BootstrapScanDecision(
            ShouldScan: true, ScanIntent.CorruptionHeal, WorkspaceRegistryState.Ready);

        Assert.Equal(
            ScanIntent.CorruptionHeal, IndexBootstrapService.EscalateForReplacedRoot(corrupt).Intent);
    }

    [Fact]
    public void AnEscalatedRebindIsNeverDowngradable()
    {
        var escalated = IndexBootstrapService.EscalateForReplacedRoot(
            IndexBootstrapService.DecideBootstrapScan(
                dbExists: true, existingRootPath: "/repo/wt", canonicalRoot: "/repo/wt",
                hasCommittedRevision: true));

        Assert.False(ScanIntentPolicy.MayDowngradeToIncremental(escalated.Intent));
    }
}
