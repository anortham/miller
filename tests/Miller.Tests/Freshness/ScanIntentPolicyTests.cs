using Miller.Core.Freshness;
using Xunit;

namespace Miller.Tests.Freshness;

/// <summary>
/// Pins <see cref="ScanIntentPolicy"/>, the table that replaced the orchestration boundary's <c>bool force</c>.
/// The facts a boolean could not carry each have a defect they prevent: a repair running as a delta extends a
/// broken artifact, a repair completion clearing a different repair's latch drops a promised rebuild, a folded
/// latch retried at the weakest member downgrades a rebuild nobody may downgrade, and an extractor upgrade left
/// armed by a completed force runs a second byte-equivalent rebuild of the whole repo.
/// </summary>
public sealed class ScanIntentPolicyTests
{
    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile, false)]
    [InlineData(ScanIntent.UserFullRebuild, true)]
    [InlineData(ScanIntent.RootRebind, true)]
    [InlineData(ScanIntent.SchemaHeal, true)]
    [InlineData(ScanIntent.CorruptionHeal, true)]
    [InlineData(ScanIntent.ExtractorUpgrade, true)]
    public void RequiresForce_IsTrueForEveryIntentExceptTheDeltaReconcile(ScanIntent intent, bool expected) =>
        Assert.Equal(expected, ScanIntentPolicy.RequiresForce(intent));

    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile, false)]
    [InlineData(ScanIntent.UserFullRebuild, true)]
    [InlineData(ScanIntent.RootRebind, false)]
    [InlineData(ScanIntent.SchemaHeal, false)]
    [InlineData(ScanIntent.CorruptionHeal, false)]
    [InlineData(ScanIntent.ExtractorUpgrade, false)]
    public void MayDowngradeToIncremental_IsTrueOnlyForAUserRequestedRebuild(ScanIntent intent, bool expected) =>
        Assert.Equal(expected, ScanIntentPolicy.MayDowngradeToIncremental(intent));

    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile)]
    [InlineData(ScanIntent.UserFullRebuild)]
    [InlineData(ScanIntent.RootRebind)]
    [InlineData(ScanIntent.SchemaHeal)]
    [InlineData(ScanIntent.CorruptionHeal)]
    [InlineData(ScanIntent.ExtractorUpgrade)]
    public void Satisfies_AnyCompletedScanDischargesAPendingDelta(ScanIntent completed) =>
        Assert.True(ScanIntentPolicy.Satisfies(completed, ScanIntent.IncrementalReconcile));

    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile, false)]
    [InlineData(ScanIntent.UserFullRebuild, true)]
    [InlineData(ScanIntent.RootRebind, true)]
    [InlineData(ScanIntent.SchemaHeal, true)]
    [InlineData(ScanIntent.CorruptionHeal, true)]
    [InlineData(ScanIntent.ExtractorUpgrade, true)]
    public void Satisfies_APendingUserRebuildIsDischargedByAnyForceButNeverByADelta(
        ScanIntent completed, bool expected) =>
        Assert.Equal(expected, ScanIntentPolicy.Satisfies(completed, ScanIntent.UserFullRebuild));

    [Theory]
    [InlineData(ScanIntent.RootRebind)]
    [InlineData(ScanIntent.SchemaHeal)]
    [InlineData(ScanIntent.CorruptionHeal)]
    public void Satisfies_ARepairIsDischargedOnlyByItsOwnIntent(ScanIntent pending)
    {
        foreach (ScanIntent completed in Enum.GetValues<ScanIntent>())
            Assert.Equal(completed == pending, ScanIntentPolicy.Satisfies(completed, pending));
    }

    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile, false)]
    [InlineData(ScanIntent.UserFullRebuild, true)]
    [InlineData(ScanIntent.RootRebind, true)]
    [InlineData(ScanIntent.SchemaHeal, true)]
    [InlineData(ScanIntent.CorruptionHeal, true)]
    [InlineData(ScanIntent.ExtractorUpgrade, true)]
    public void Satisfies_APendingExtractorUpgradeIsDischargedByAnyForceButNeverByADelta(
        ScanIntent completed, bool expected) =>
        Assert.Equal(expected, ScanIntentPolicy.Satisfies(completed, ScanIntent.ExtractorUpgrade));

    [Fact]
    public void Satisfies_AnExtractorUpgradeCompletionStillLeavesAPendingRepairArmed()
    {
        Assert.False(ScanIntentPolicy.Satisfies(ScanIntent.ExtractorUpgrade, ScanIntent.SchemaHeal));
        Assert.False(ScanIntentPolicy.Satisfies(ScanIntent.ExtractorUpgrade, ScanIntent.CorruptionHeal));
        Assert.False(ScanIntentPolicy.Satisfies(ScanIntent.ExtractorUpgrade, ScanIntent.RootRebind));
    }

    [Theory]
    [InlineData(ScanIntent.IncrementalReconcile)]
    [InlineData(ScanIntent.UserFullRebuild)]
    [InlineData(ScanIntent.RootRebind)]
    [InlineData(ScanIntent.SchemaHeal)]
    [InlineData(ScanIntent.CorruptionHeal)]
    [InlineData(ScanIntent.ExtractorUpgrade)]
    public void ClearsFailureRecord_AnyCompletedScanClearsADeltaIntentRecord(ScanIntent completed) =>
        Assert.True(ScanIntentPolicy.ClearsFailureRecord(completed, ScanIntent.IncrementalReconcile));

    [Theory]
    [InlineData(ScanIntent.UserFullRebuild)]
    [InlineData(ScanIntent.RootRebind)]
    [InlineData(ScanIntent.SchemaHeal)]
    [InlineData(ScanIntent.CorruptionHeal)]
    [InlineData(ScanIntent.ExtractorUpgrade)]
    public void ClearsFailureRecord_ADeltaNeverClearsAForceIntentRecord(ScanIntent recorded) =>
        Assert.False(ScanIntentPolicy.ClearsFailureRecord(ScanIntent.IncrementalReconcile, recorded));

    [Theory]
    [InlineData(ScanIntent.RootRebind)]
    [InlineData(ScanIntent.SchemaHeal)]
    [InlineData(ScanIntent.CorruptionHeal)]
    public void ClearsFailureRecord_ARepairIntentRecordIsClearedByAnyCompletedForce(ScanIntent recorded)
    {
        Assert.True(ScanIntentPolicy.ClearsFailureRecord(ScanIntent.UserFullRebuild, recorded));
        Assert.True(ScanIntentPolicy.ClearsFailureRecord(ScanIntent.ExtractorUpgrade, recorded));
        Assert.False(ScanIntentPolicy.Satisfies(ScanIntent.UserFullRebuild, recorded));
    }

    [Fact]
    public void Strongest_OfADeltaAndAUserRebuild_IsTheUserRebuild() =>
        Assert.Equal(
            ScanIntent.UserFullRebuild,
            ScanIntentPolicy.Strongest(new[] { ScanIntent.IncrementalReconcile, ScanIntent.UserFullRebuild }));

    [Theory]
    [InlineData(ScanIntent.RootRebind)]
    [InlineData(ScanIntent.SchemaHeal)]
    [InlineData(ScanIntent.CorruptionHeal)]
    [InlineData(ScanIntent.ExtractorUpgrade)]
    public void Strongest_OfAUserRebuildAndAHeal_IsTheHeal_SoTheRetryIsNotDowngradable(ScanIntent heal)
    {
        ScanIntent strongest = ScanIntentPolicy.Strongest(new[] { ScanIntent.UserFullRebuild, heal });

        Assert.Equal(heal, strongest);
        Assert.False(ScanIntentPolicy.MayDowngradeToIncremental(strongest));
    }

    [Fact]
    public void Strongest_OfASingleIntent_IsThatIntent()
    {
        foreach (ScanIntent intent in Enum.GetValues<ScanIntent>())
            Assert.Equal(intent, ScanIntentPolicy.Strongest(new[] { intent }));
    }

    [Fact]
    public void Strongest_OfNothingPending_Throws() =>
        Assert.Throws<ArgumentException>(() => ScanIntentPolicy.Strongest(Array.Empty<ScanIntent>()));
}
