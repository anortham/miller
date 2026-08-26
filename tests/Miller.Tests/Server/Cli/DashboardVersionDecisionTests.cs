using Miller.Server.Cli;
using Xunit;

namespace Miller.Tests.Server.Cli;

public sealed class DashboardVersionDecisionTests
{
    [Fact]
    public void TheSameBuildIsNeitherAMismatchNorReplaceable()
    {
        DashboardVersionDecision decision = DashboardVersionDecision.For("1.23.0+aaaaaaa", "1.23.0+aaaaaaa");

        Assert.False(decision.MayReplace);
        Assert.False(decision.Mismatch);
        Assert.Contains("runs this build", decision.Reason);
    }

    [Fact]
    public void AnOlderReleaseMayBeReplaced()
    {
        DashboardVersionDecision decision = DashboardVersionDecision.For("1.23.0+bbbbbbb", "1.9.0+aaaaaaa");

        Assert.True(decision.MayReplace);
        Assert.True(decision.Mismatch);
        Assert.Contains("older build (1.9.0+aaaaaaa)", decision.Reason);
    }

    [Fact]
    public void ANewerReleaseIsLeftAlone()
    {
        DashboardVersionDecision decision = DashboardVersionDecision.For("1.9.0+bbbbbbb", "1.23.0+aaaaaaa");

        Assert.False(decision.MayReplace);
        Assert.True(decision.Mismatch);
        Assert.Contains("newer build (1.23.0+aaaaaaa)", decision.Reason);
    }

    [Fact]
    public void TheSameReleaseFromADifferentCommitMayBeReplaced()
    {
        DashboardVersionDecision decision = DashboardVersionDecision.For("1.23.0+bbbbbbb", "1.23.0+aaaaaaa");

        Assert.True(decision.MayReplace);
        Assert.True(decision.Mismatch);
        Assert.Contains("same release from a different build", decision.Reason);
    }

    [Fact]
    public void AnUnorderablePairIsLeftAlone()
    {
        DashboardVersionDecision decision = DashboardVersionDecision.For("experimental", "nightly");

        Assert.False(decision.MayReplace);
        Assert.True(decision.Mismatch);
        Assert.Contains("neither can be ordered", decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ARecordWithNoBuildPredatesTheCheckAndMayBeReplaced(string? runningVersion)
    {
        DashboardVersionDecision decision = DashboardVersionDecision.For("1.23.0+bbbbbbb", runningVersion);

        Assert.True(decision.MayReplace);
        Assert.True(decision.Mismatch);
        Assert.Contains("records no build", decision.Reason);
        Assert.Equal("unknown", decision.RunningVersionLabel);
    }

    [Fact]
    public void ARecordedBuildIsItsOwnLabel()
    {
        DashboardVersionDecision decision = DashboardVersionDecision.For("1.23.0+bbbbbbb", "1.22.0+aaaaaaa");

        Assert.Equal("1.22.0+aaaaaaa", decision.RunningVersionLabel);
    }
}
