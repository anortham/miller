using Miller.Core.Telemetry;
using Xunit;

namespace Miller.Tests.Core;

public sealed class CanaryGateMathTests
{
    [Theory]
    [InlineData(4, 2.77645)]
    [InlineData(10, 2.22814)]
    [InlineData(30, 2.04227)]
    public void StudentTCritical_MatchesPublishedTableValuesWithinHillsTolerance(double df, double expected)
    {
        Assert.Equal(expected, CanaryGateMath.StudentTCritical(0.05, df), 0.001);
    }

    [Fact]
    public void StudentTCritical_ConvergesToTheNormalQuantileAtLargeDf()
    {
        Assert.Equal(1.95996, CanaryGateMath.StudentTCritical(0.05, 1e9), 0.001);
    }

    [Fact]
    public void WelchInterval_ReproducesAHandComputedIntervalAndDf()
    {
        double[] treatment = [0.6, 0.7, 0.8];
        double[] control = [0.2, 0.3, 0.4];

        (double lower, double upper, double effect) = CanaryGateMath.WelchInterval(treatment, control);

        Assert.Equal(0.4, effect, 6);
        Assert.Equal(0.17334, lower, 4);
        Assert.Equal(0.62666, upper, 4);
    }

    [Fact]
    public void WelchInterval_LowerBoundExcludesZeroForASeparatedTreatmentArm()
    {
        double[] treatment = [0.55, 0.60, 0.58, 0.62, 0.57];
        double[] control = [0.20, 0.22, 0.19, 0.25, 0.21];

        (double lower, _, double effect) = CanaryGateMath.WelchInterval(treatment, control);

        Assert.True(effect > 0);
        Assert.True(lower > 0);
    }

    [Fact]
    public void WelchInterval_RejectsArmsSmallerThanTwo()
    {
        Assert.Throws<ArgumentException>(() => CanaryGateMath.WelchInterval([0.5], [0.1, 0.2]));
    }

    [Fact]
    public void OneSampleInterval_ReproducesAHandComputedInterval()
    {
        double[] xs = [8.0, 9.0, 10.0, 9.0, 8.0, 10.0];

        (double lower, double upper, double mean) = CanaryGateMath.OneSampleInterval(xs);

        Assert.Equal(9.0, mean, 6);
        Assert.Equal(8.0613, lower, 3);
        Assert.Equal(9.9387, upper, 3);
    }

    [Fact]
    public void NearestRankP95_TakesTheCeilingRankWithoutInterpolation()
    {
        long[] hundred = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();
        Assert.Equal(95, CanaryGateMath.NearestRankP95(hundred));

        Assert.Equal(7, CanaryGateMath.NearestRankP95([3, 5, 7]));
        Assert.Equal(42, CanaryGateMath.NearestRankP95([42]));
    }

    [Fact]
    public void NearestRankP95_NeverSkipsTheMaxOnSmallSamples()
    {
        Assert.Equal(20, CanaryGateMath.NearestRankP95([10, 20]));
    }

    [Fact]
    public void BucketedP95_WalksTheLadderToTheCumulativeNinetyFifthPercentile()
    {
        var counts = new Dictionary<string, int>
        {
            ["lt_100"] = 9,
            ["lt_250"] = 27,
            ["lt_500"] = 3,
            ["gte_3000"] = 2,
        };

        Assert.Equal("lt_500", CanaryGateMath.BucketedP95(counts, 41));
    }

    [Fact]
    public void BucketedP95_ReturnsTheOnlyRungWhenAllCallsShareIt()
    {
        Assert.Equal("lt_25", CanaryGateMath.BucketedP95(new Dictionary<string, int> { ["lt_25"] = 6 }, 6));
    }
}
