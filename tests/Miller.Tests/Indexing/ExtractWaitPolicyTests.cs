using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pure fast-suite coverage for the progress-aware bounded wait (no subprocess; the live kill path is the
/// Scale-tagged <see cref="JulieExtractRunnerTimeoutTests"/>). The contract under test: a progressing extract
/// outlives the stall window, a silent one is killed at the stall window, and even a progressing one is killed
/// at the absolute cap.
/// </summary>
public sealed class ExtractWaitPolicyTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Hard = TimeSpan.FromSeconds(60);

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void ProgressingProcess_SurvivesWellPastTheStallWindow()
    {
        var policy = new ExtractWaitPolicy(Stall, Hard);
        // Stamp moves every observation: 50s elapsed is 5x the stall window and must still be Continue.
        for (int i = 1; i <= 50; i++)
            Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(i), progressStamp: i));
    }

    [Fact]
    public void SilentProcess_StallsOutMeasuredFromStart()
    {
        var policy = new ExtractWaitPolicy(Stall, Hard);
        // The first stamp is a baseline, not progress: a process that never produces anything must be
        // killed at the stall window measured from start, not from the first observation.
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(1), progressStamp: 0));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(9), progressStamp: 0));
        Assert.Equal(ExtractWaitVerdict.Stalled, policy.Observe(At(10), progressStamp: 0));
    }

    [Fact]
    public void ProgressThenSilence_StallsRelativeToLastProgress()
    {
        var policy = new ExtractWaitPolicy(Stall, Hard);
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(1), progressStamp: 100));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(5), progressStamp: 200)); // progress at 5s
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(14), progressStamp: 200));
        Assert.Equal(ExtractWaitVerdict.Stalled, policy.Observe(At(15), progressStamp: 200)); // 10s after last progress
    }

    [Fact]
    public void AnyStampChangeCountsAsProgress_IncludingShrinkage()
    {
        // A WAL checkpoint SHRINKS the -wal file; byte-total decrease is still activity, not a stall.
        var policy = new ExtractWaitPolicy(Stall, Hard);
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(1), progressStamp: 1_000_000));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(9), progressStamp: 500_000));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(18), progressStamp: 500_000));
        Assert.Equal(ExtractWaitVerdict.Stalled, policy.Observe(At(19), progressStamp: 500_000));
    }

    [Fact]
    public void ProgressingProcess_IsStillKilledAtTheHardCap()
    {
        var policy = new ExtractWaitPolicy(Stall, Hard);
        for (int i = 1; i < 60; i++)
            Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(i), progressStamp: i));
        Assert.Equal(ExtractWaitVerdict.HardCapExceeded, policy.Observe(At(60), progressStamp: 60));
    }

    [Fact]
    public void HardCapWins_WhenBothLimitsAreExceeded()
    {
        var policy = new ExtractWaitPolicy(Stall, Hard);
        // One giant gap past both limits: report the absolute cap, not the stall, so the error message
        // does not mislabel a coarse-polled long run as a hang.
        Assert.Equal(ExtractWaitVerdict.HardCapExceeded, policy.Observe(At(120), progressStamp: 0));
    }

    [Fact]
    public void HardTimeoutFor_IsTheDocumentedMultipleOfTheStallWindow()
    {
        Assert.Equal(TimeSpan.FromHours(4), ExtractWaitPolicy.HardTimeoutFor(TimeSpan.FromMinutes(10)));
    }

    // The measured floor this cap has to clear: a healthy force scan of a 74k-file repo took 3,677 s
    // (docs/findings/2026-08-02-w10-scale-repro-wal-measurement.md). The previous 6x cap was 3,600 s, so the
    // scan was killed 77 s short of finishing on every attempt.
    [Fact]
    public void TheDefaultHardCap_OutlastsAMeasuredHealthy74kFileScan()
    {
        var policy = new ExtractWaitPolicy(
            JulieExtractRunner.DefaultTimeout,
            ExtractWaitPolicy.HardTimeoutFor(JulieExtractRunner.DefaultTimeout));

        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(1), progressStamp: 1));
        Assert.Equal(ExtractWaitVerdict.Continue, policy.Observe(At(3677), progressStamp: 2));
    }

    [Fact]
    public void AnEnvironmentOverrideAboveTheStallWindow_ReplacesTheDerivedCap()
    {
        Assert.Equal(
            TimeSpan.FromHours(9),
            ExtractWaitPolicy.HardTimeoutForEnvironment(TimeSpan.FromMinutes(10), _ => "32400"));

        Assert.Equal(
            TimeSpan.FromMinutes(90),
            ExtractWaitPolicy.HardTimeoutForEnvironment(TimeSpan.FromMinutes(10), _ => "01:30:00"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("600")]
    [InlineData("300")]
    public void AnAbsentOrUnusableEnvironmentOverride_KeepsTheDerivedCap(string? value)
    {
        Assert.Equal(
            TimeSpan.FromHours(4),
            ExtractWaitPolicy.HardTimeoutForEnvironment(TimeSpan.FromMinutes(10), _ => value));
    }

    [Fact]
    public void TheEnvironmentOverrideIsReadFromTheDocumentedVariable()
    {
        string? asked = null;

        ExtractWaitPolicy.HardTimeoutForEnvironment(TimeSpan.FromMinutes(10), name =>
        {
            asked = name;
            return null;
        });

        Assert.Equal("MILLER_EXTRACT_HARD_CAP", asked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveStallTimeout_IsRejected(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractWaitPolicy(TimeSpan.FromSeconds(seconds), Hard));

    [Fact]
    public void HardCapBelowStallWindow_IsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExtractWaitPolicy(Stall, TimeSpan.FromSeconds(5)));
}
