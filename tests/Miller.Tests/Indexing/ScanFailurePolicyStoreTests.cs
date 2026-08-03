using Miller.Core.Freshness;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the record-lifecycle contract every <see cref="IScanFailurePolicy"/> implementation owes, against BOTH of
/// them. The two carry the same clearing guard in their own bodies, so a mutation to either alone must fail a
/// test; a suite that only exercised the persisted one would let the in-memory fallback — what
/// <c>IndexerService</c> uses before a workspace is bound — drift silently.
/// </summary>
public sealed class ScanFailurePolicyStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "miller-scan-failure-store-" + Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = T0;

    public ScanFailurePolicyStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    public enum Store
    {
        Persisted,
        InMemory,
    }

    [Theory]
    [InlineData(Store.Persisted)]
    [InlineData(Store.InMemory)]
    public void RecordSuccess_ADeltaCompletion_LeavesAForceIntentRecordIntact(Store store)
    {
        IScanFailurePolicy policy = New(store);
        policy.RecordFailure(ScanIntent.UserFullRebuild, exitCode: 137, jobs: 4);

        policy.RecordSuccess(ScanIntent.IncrementalReconcile);

        Assert.Equal(1, policy.Read()?.ConsecutiveFailures);
    }

    [Theory]
    [InlineData(Store.Persisted)]
    [InlineData(Store.InMemory)]
    public void RecordSuccess_ADeltaThatRanOnceTheThrottleElapsed_RespacesTheStillOwedForce(Store store)
    {
        IScanFailurePolicy policy = New(store);
        policy.RecordFailure(ScanIntent.UserFullRebuild, exitCode: 137, jobs: 4);
        DateTimeOffset firstDeadline = policy.Read()!.NextAttemptAtUtc;

        _now = firstDeadline;
        Assert.True(policy.Evaluate(ScanIntent.IncrementalReconcile).Attempt);
        policy.RecordSuccess(ScanIntent.IncrementalReconcile);

        Assert.False(policy.Evaluate(ScanIntent.IncrementalReconcile).Attempt);
        Assert.Equal(1, policy.Read()?.ConsecutiveFailures);
        Assert.Equal(ScanIntent.UserFullRebuild, policy.Read()?.Intent);
        Assert.True(policy.Read()?.NextAttemptAtUtc > firstDeadline);
    }

    [Theory]
    [InlineData(Store.Persisted)]
    [InlineData(Store.InMemory)]
    public void RecordSuccess_ADeltaWhileTheThrottleStillHolds_LeavesTheDeadlineAlone(Store store)
    {
        IScanFailurePolicy policy = New(store);
        policy.RecordFailure(ScanIntent.UserFullRebuild, exitCode: 137, jobs: 4);
        DateTimeOffset deadline = policy.Read()!.NextAttemptAtUtc;

        policy.RecordSuccess(ScanIntent.IncrementalReconcile);

        Assert.Equal(deadline, policy.Read()?.NextAttemptAtUtc);
    }

    [Theory]
    [InlineData(Store.Persisted)]
    [InlineData(Store.InMemory)]
    public void RecordSuccess_ADeltaCompletion_ClearsADeltaIntentRecord(Store store)
    {
        IScanFailurePolicy policy = New(store);
        policy.RecordFailure(ScanIntent.IncrementalReconcile, exitCode: 1, jobs: 4);

        policy.RecordSuccess(ScanIntent.IncrementalReconcile);

        Assert.Null(policy.Read());
    }

    [Theory]
    [InlineData(Store.Persisted, ScanIntent.RootRebind)]
    [InlineData(Store.Persisted, ScanIntent.SchemaHeal)]
    [InlineData(Store.Persisted, ScanIntent.CorruptionHeal)]
    [InlineData(Store.InMemory, ScanIntent.RootRebind)]
    [InlineData(Store.InMemory, ScanIntent.SchemaHeal)]
    [InlineData(Store.InMemory, ScanIntent.CorruptionHeal)]
    public void RecordSuccess_AFullRebuild_ClearsAFailedRepairRecordRatherThanStrandingTheDowngrade(
        Store store, ScanIntent repair)
    {
        IScanFailurePolicy policy = New(store, priorArtifactUsable: true);
        policy.RecordFailure(repair, exitCode: 137, jobs: 4);

        policy.RecordSuccess(ScanIntent.UserFullRebuild);
        _now += ScanFailurePolicy.MaxJitteredBackoffFor(1);

        Assert.Null(policy.Read());
        Assert.False(policy.Evaluate(ScanIntent.UserFullRebuild).Downgraded);
    }

    [Theory]
    [InlineData(Store.Persisted)]
    [InlineData(Store.InMemory)]
    public void RecordFailure_AFailedDowngradedRetry_LeavesTheRecordOwingTheForceScanItSkipped(Store store)
    {
        IScanFailurePolicy policy = New(store, priorArtifactUsable: true);
        policy.RecordFailure(ScanIntent.UserFullRebuild, exitCode: 137, jobs: 4);
        _now += ScanFailurePolicy.MaxJitteredBackoffFor(1);

        ScanAttemptDecision retry = policy.Evaluate(ScanIntent.UserFullRebuild);
        Assert.True(retry.Downgraded);
        policy.RecordFailure(retry.EffectiveIntent, exitCode: 1, jobs: 1);

        policy.RecordSuccess(ScanIntent.IncrementalReconcile);

        Assert.Equal(2, policy.Read()?.ConsecutiveFailures);
        Assert.Equal(ScanIntent.UserFullRebuild, policy.Read()?.Intent);
    }

    private IScanFailurePolicy New(Store store, bool priorArtifactUsable = false) => store switch
    {
        Store.Persisted => PersistedScanFailurePolicy.ForTest(
            _dir, () => priorArtifactUsable, () => _now, static () => 0),
        _ => new InMemoryScanFailurePolicy(() => priorArtifactUsable, () => _now, static () => 0),
    };
}
