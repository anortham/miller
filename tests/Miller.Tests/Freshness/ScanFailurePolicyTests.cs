using Miller.Core.Freshness;
using Xunit;

namespace Miller.Tests.Freshness;

/// <summary>
/// Pins the pure scan-failure decision core: the 30s / 2m / 10m / 30m-max jittered schedule, the post-SIGKILL
/// single-job clamp, and the downgrade rule. Everything here is total over (record, now, intent) with an injected
/// clock and jitter draw, so the persisted store above it only has to be a correct file.
/// </summary>
public sealed class ScanFailurePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static ScanFailureRecord Failed(
        int consecutiveFailures,
        int? exitCode = 1,
        ScanIntent intent = ScanIntent.UserFullRebuild,
        TimeSpan? retryIn = null,
        int jobs = 4) =>
        new(intent, exitCode, consecutiveFailures, jobs, T0, T0 + (retryIn ?? TimeSpan.FromSeconds(30)));

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 120)]
    [InlineData(3, 600)]
    [InlineData(4, 1800)]
    [InlineData(9, 1800)]
    public void BaseBackoffFor_FollowsTheDocumentedScheduleAndCapsAtThirtyMinutes(
        int consecutiveFailures, int expectedSeconds) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds), ScanFailurePolicy.BaseBackoffFor(consecutiveFailures));

    [Fact]
    public void BackoffFor_JitterOnlyEverAddsTime_SoTheScheduleIsAFloor()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ScanFailurePolicy.BackoffFor(1, jitter01: 0));
        Assert.Equal(TimeSpan.FromSeconds(37.5), ScanFailurePolicy.BackoffFor(1, jitter01: 1));
        Assert.InRange(
            ScanFailurePolicy.BackoffFor(1, jitter01: 0.5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(37.5));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    [InlineData(double.NaN)]
    public void BackoffFor_AnOutOfRangeJitterDrawCannotShortenTheBackoff(double jitter01) =>
        Assert.InRange(
            ScanFailurePolicy.BackoffFor(1, jitter01),
            ScanFailurePolicy.BaseBackoffFor(1),
            ScanFailurePolicy.MaxJitteredBackoffFor(1));

    [Fact]
    public void Decide_WithNoRecordedFailure_AttemptsTheRequestedIntentAtTheAmbientJobsCap()
    {
        ScanAttemptDecision decision = ScanFailurePolicy.Decide(
            record: null, T0, ScanIntent.UserFullRebuild, bypassBackoff: false, priorArtifactUsable: () => true);

        Assert.True(decision.Attempt);
        Assert.Equal(ScanIntent.UserFullRebuild, decision.EffectiveIntent);
        Assert.Null(decision.Jobs);
        Assert.False(decision.Downgraded);
        Assert.Equal(0, decision.ConsecutiveFailures);
    }

    [Fact]
    public void Decide_BeforeTheNextAttemptTime_DefersAndReportsWhenToRetry()
    {
        ScanFailureRecord record = Failed(consecutiveFailures: 2, retryIn: TimeSpan.FromMinutes(2));

        ScanAttemptDecision decision = ScanFailurePolicy.Decide(
            record, T0 + TimeSpan.FromSeconds(119), ScanIntent.UserFullRebuild,
            bypassBackoff: false, priorArtifactUsable: () => false);

        Assert.False(decision.Attempt);
        Assert.Equal(record.NextAttemptAtUtc, decision.RetryAtUtc);
        Assert.Equal(2, decision.ConsecutiveFailures);
    }

    [Fact]
    public void Decide_AtTheNextAttemptTime_Attempts()
    {
        ScanFailureRecord record = Failed(consecutiveFailures: 2, retryIn: TimeSpan.FromMinutes(2));

        ScanAttemptDecision decision = ScanFailurePolicy.Decide(
            record, record.NextAttemptAtUtc, ScanIntent.UserFullRebuild,
            bypassBackoff: false, priorArtifactUsable: () => false);

        Assert.True(decision.Attempt);
        Assert.Null(decision.RetryAtUtc);
    }

    [Fact]
    public void Decide_WithBypass_AttemptsInsideTheBackoffWindowButStillClampsJobsAfterASigkill()
    {
        ScanFailureRecord record = Failed(
            consecutiveFailures: 3, exitCode: 137, retryIn: TimeSpan.FromMinutes(10));

        ScanAttemptDecision decision = ScanFailurePolicy.Decide(
            record, T0, ScanIntent.UserFullRebuild, bypassBackoff: true, priorArtifactUsable: () => false);

        Assert.True(decision.Attempt);
        Assert.Equal(1, decision.Jobs);
        Assert.Equal(3, decision.ConsecutiveFailures);
    }

    [Fact]
    public void Decide_AfterASigkill_ClampsTheRetryToOneJob()
    {
        ScanAttemptDecision decision = ScanFailurePolicy.Decide(
            Failed(consecutiveFailures: 1, exitCode: 137), T0 + TimeSpan.FromMinutes(1),
            ScanIntent.ExtractorUpgrade, bypassBackoff: false, priorArtifactUsable: () => false);

        Assert.Equal(1, decision.Jobs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(null)]
    public void Decide_AfterANonSigkillFailure_LeavesTheJobsCapToTheAmbientPolicy(int? exitCode)
    {
        ScanAttemptDecision decision = ScanFailurePolicy.Decide(
            Failed(consecutiveFailures: 1, exitCode: exitCode), T0 + TimeSpan.FromMinutes(1),
            ScanIntent.ExtractorUpgrade, bypassBackoff: false, priorArtifactUsable: () => false);

        Assert.Null(decision.Jobs);
    }

    [Fact]
    public void Decide_AUserRebuildRetry_DowngradesOnlyWhenThePriorArtifactIsServable()
    {
        ScanAttemptDecision servable = ScanFailurePolicy.Decide(
            Failed(consecutiveFailures: 1), T0 + TimeSpan.FromMinutes(1), ScanIntent.UserFullRebuild,
            bypassBackoff: false, priorArtifactUsable: () => true);

        Assert.True(servable.Downgraded);
        Assert.Equal(ScanIntent.IncrementalReconcile, servable.EffectiveIntent);

        ScanAttemptDecision unusable = ScanFailurePolicy.Decide(
            Failed(consecutiveFailures: 1), T0 + TimeSpan.FromMinutes(1), ScanIntent.UserFullRebuild,
            bypassBackoff: false, priorArtifactUsable: () => false);

        Assert.False(unusable.Downgraded);
        Assert.Equal(ScanIntent.UserFullRebuild, unusable.EffectiveIntent);
    }

    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile)]
    [InlineData(ScanIntent.RootRebind)]
    [InlineData(ScanIntent.SchemaHeal)]
    [InlineData(ScanIntent.CorruptionHeal)]
    [InlineData(ScanIntent.ExtractorUpgrade)]
    public void Decide_EveryIntentOtherThanAUserRebuild_RunsAtFullStrengthEvenWithAServableArtifact(
        ScanIntent intent)
    {
        ScanAttemptDecision decision = ScanFailurePolicy.Decide(
            Failed(consecutiveFailures: 4, intent: intent), T0 + TimeSpan.FromHours(1), intent,
            bypassBackoff: false, priorArtifactUsable: () => true);

        Assert.False(decision.Downgraded);
        Assert.Equal(intent, decision.EffectiveIntent);
    }

    [Fact]
    public void Decide_WithBypass_RunsTheRequestedRebuildAtFullStrengthRatherThanDowngradingIt()
    {
        ScanAttemptDecision decision = ScanFailurePolicy.Decide(
            Failed(consecutiveFailures: 4), T0 + TimeSpan.FromHours(1), ScanIntent.UserFullRebuild,
            bypassBackoff: true, priorArtifactUsable: () => true);

        Assert.True(decision.Attempt);
        Assert.False(decision.Downgraded);
        Assert.Equal(ScanIntent.UserFullRebuild, decision.EffectiveIntent);
    }

    [Fact]
    public void RecordDowngrade_KeepsTheStreakAndTheFailureButConsumesTheAttemptSlot()
    {
        ScanFailureRecord failed = ScanFailurePolicy.RecordFailure(
            previous: null, ScanIntent.UserFullRebuild, exitCode: 137, jobs: 4, T0, jitter01: 0);
        DateTimeOffset served = failed.NextAttemptAtUtc;

        ScanFailureRecord downgraded = ScanFailurePolicy.RecordDowngrade(failed, served, jitter01: 0);

        Assert.Equal(1, downgraded.ConsecutiveFailures);
        Assert.Equal(137, downgraded.ExitCode);
        Assert.Equal(ScanIntent.UserFullRebuild, downgraded.Intent);
        Assert.Equal(failed.LastFailureAtUtc, downgraded.LastFailureAtUtc);
        Assert.Equal(served + ScanFailurePolicy.FirstBackoff, downgraded.NextAttemptAtUtc);
        Assert.False(
            ScanFailurePolicy.Decide(
                downgraded, served, ScanIntent.UserFullRebuild,
                bypassBackoff: false, priorArtifactUsable: () => true).Attempt);
    }

    [Fact]
    public void RecordDowngrade_AtASaturatedStreak_ReusesThatStreaksBackoffWithoutEscalating()
    {
        var saturated = new ScanFailureRecord(ScanIntent.UserFullRebuild, 137, 9, 1, T0, T0);

        ScanFailureRecord downgraded = ScanFailurePolicy.RecordDowngrade(saturated, T0, jitter01: 0);

        Assert.Equal(9, downgraded.ConsecutiveFailures);
        Assert.Equal(T0 + ScanFailurePolicy.MaxBackoff, downgraded.NextAttemptAtUtc);
    }

    [Fact]
    public void Decide_WithNoArtifactProbe_NeverDowngrades()
    {
        ScanAttemptDecision decision = ScanFailurePolicy.Decide(
            Failed(consecutiveFailures: 1), T0 + TimeSpan.FromMinutes(1), ScanIntent.UserFullRebuild,
            bypassBackoff: false, priorArtifactUsable: null);

        Assert.False(decision.Downgraded);
        Assert.Equal(ScanIntent.UserFullRebuild, decision.EffectiveIntent);
    }

    [Fact]
    public void RecordFailure_ExtendsTheStreakAndPushesOutTheNextAttempt()
    {
        ScanFailureRecord first = ScanFailurePolicy.RecordFailure(
            previous: null, ScanIntent.UserFullRebuild, exitCode: 137, jobs: 4, T0, jitter01: 0);

        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.Equal(137, first.ExitCode);
        Assert.Equal(4, first.Jobs);
        Assert.Equal(T0, first.LastFailureAtUtc);
        Assert.Equal(T0 + TimeSpan.FromSeconds(30), first.NextAttemptAtUtc);

        ScanFailureRecord second = ScanFailurePolicy.RecordFailure(
            first, ScanIntent.ExtractorUpgrade, exitCode: 1, jobs: 1,
            T0 + TimeSpan.FromMinutes(1), jitter01: 0);

        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.Equal(ScanIntent.ExtractorUpgrade, second.Intent);
        Assert.Equal(1, second.Jobs);
        Assert.Equal(T0 + TimeSpan.FromMinutes(1) + TimeSpan.FromMinutes(2), second.NextAttemptAtUtc);
    }

    [Fact]
    public void RecordFailure_TheStreakCapsTheBackoffAtThirtyMinutesRatherThanGrowingForever()
    {
        ScanFailureRecord record = ScanFailurePolicy.RecordFailure(
            new ScanFailureRecord(ScanIntent.UserFullRebuild, 137, 20, 1, T0, T0), ScanIntent.UserFullRebuild,
            exitCode: 137, jobs: 1, T0, jitter01: 0);

        Assert.Equal(21, record.ConsecutiveFailures);
        Assert.Equal(T0 + TimeSpan.FromMinutes(30), record.NextAttemptAtUtc);
    }

    [Fact]
    public void WasSignalKilled_IsTrueOnlyForExit137()
    {
        Assert.True(ScanFailurePolicy.WasSignalKilled(137));
        Assert.False(ScanFailurePolicy.WasSignalKilled(1));
        Assert.False(ScanFailurePolicy.WasSignalKilled(null));
    }
}
