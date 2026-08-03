using System.Text.Json;
using Miller.Core.Freshness;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins <c>&lt;workspace&gt;/.miller/scan-failure.json</c>: the record survives a process boundary (that is the
/// whole reason it is on disk rather than in a field), it is written atomically, and every damaged shape degrades
/// to "no recorded failure" instead of throwing inside the indexing path.
/// </summary>
public sealed class ScanFailureJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "miller-scan-failure-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset T0 = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    public ScanFailureJournalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string RecordPath => Path.Combine(_dir, ScanFailureJournal.FileName);

    [Fact]
    public void TryRead_WithNothingRecorded_IsNull() => Assert.Null(ScanFailureJournal.TryRead(_dir));

    [Fact]
    public void TryWrite_ThenTryRead_RoundTripsEveryRecordedField()
    {
        var record = new ScanFailureRecord(
            ScanIntent.ExtractorUpgrade, ExitCode: 137, ConsecutiveFailures: 3, Jobs: 4,
            LastFailureAtUtc: T0, NextAttemptAtUtc: T0 + TimeSpan.FromMinutes(10));

        ScanFailureJournal.TryWrite(_dir, record);

        Assert.Equal(record, ScanFailureJournal.TryRead(_dir));
    }

    [Fact]
    public void TryWrite_StoresTheIntentAsAName_SoReorderingTheEnumCannotReinterpretAnOldRecord()
    {
        ScanFailureJournal.TryWrite(
            _dir,
            new ScanFailureRecord(ScanIntent.CorruptionHeal, 1, 1, 2, T0, T0));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(RecordPath));

        Assert.Equal("CorruptionHeal", document.RootElement.GetProperty("intent").GetString());
    }

    [Fact]
    public void TryWrite_LeavesNoTempFileBehind()
    {
        ScanFailureJournal.TryWrite(_dir, new ScanFailureRecord(ScanIntent.SchemaHeal, 1, 1, 2, T0, T0));

        Assert.Equal(new[] { ScanFailureJournal.FileName }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void TryWrite_OverAnExistingRecord_Replaces()
    {
        ScanFailureJournal.TryWrite(_dir, new ScanFailureRecord(ScanIntent.SchemaHeal, 1, 1, 2, T0, T0));
        ScanFailureJournal.TryWrite(_dir, new ScanFailureRecord(ScanIntent.RootRebind, 2, 5, 1, T0, T0));

        ScanFailureRecord? read = ScanFailureJournal.TryRead(_dir);

        Assert.Equal(ScanIntent.RootRebind, read?.Intent);
        Assert.Equal(5, read?.ConsecutiveFailures);
    }

    [Fact]
    public void TryWrite_IntoAMissingMillerDirectory_CreatesItRatherThanLosingTheRecord()
    {
        string nested = Path.Combine(_dir, "nested", ".miller");

        ScanFailureJournal.TryWrite(nested, new ScanFailureRecord(ScanIntent.SchemaHeal, 1, 1, 2, T0, T0));

        Assert.Equal(ScanIntent.SchemaHeal, ScanFailureJournal.TryRead(nested)?.Intent);
    }

    [Fact]
    public void TryClear_RemovesTheRecord()
    {
        ScanFailureJournal.TryWrite(_dir, new ScanFailureRecord(ScanIntent.SchemaHeal, 1, 1, 2, T0, T0));

        ScanFailureJournal.TryClear(_dir);

        Assert.Null(ScanFailureJournal.TryRead(_dir));
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void TryClear_WithNothingRecorded_DoesNotThrow() => ScanFailureJournal.TryClear(_dir);

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("{\"intent\":\"SchemaHeal\",\"consecutive_failures\":1,\"jobs\":2")]
    [InlineData("{\"intent\":\"NotAnIntent\",\"consecutive_failures\":1,\"jobs\":2," +
        "\"last_failure_at_utc\":\"2026-08-02T12:00:00.0000000+00:00\"," +
        "\"next_attempt_at_utc\":\"2026-08-02T12:00:30.0000000+00:00\"}")]
    [InlineData("{\"intent\":\"9\",\"consecutive_failures\":1,\"jobs\":2," +
        "\"last_failure_at_utc\":\"2026-08-02T12:00:00.0000000+00:00\"," +
        "\"next_attempt_at_utc\":\"2026-08-02T12:00:30.0000000+00:00\"}")]
    [InlineData("{\"intent\":\"SchemaHeal\",\"consecutive_failures\":0,\"jobs\":2," +
        "\"last_failure_at_utc\":\"2026-08-02T12:00:00.0000000+00:00\"," +
        "\"next_attempt_at_utc\":\"2026-08-02T12:00:30.0000000+00:00\"}")]
    [InlineData("{\"intent\":\"SchemaHeal\",\"consecutive_failures\":1,\"jobs\":2," +
        "\"last_failure_at_utc\":\"never\",\"next_attempt_at_utc\":\"never\"}")]
    public void TryRead_ADamagedRecord_DegradesToNoRecordedFailureInsteadOfThrowing(string contents)
    {
        File.WriteAllText(RecordPath, contents);

        Assert.Null(ScanFailureJournal.TryRead(_dir));
    }

    [Fact]
    public void TryRead_ARecordTruncatedMidWrite_DegradesToNoRecordedFailure()
    {
        ScanFailureJournal.TryWrite(_dir, new ScanFailureRecord(ScanIntent.SchemaHeal, 1, 1, 2, T0, T0));
        string full = File.ReadAllText(RecordPath);
        File.WriteAllText(RecordPath, full[..(full.Length / 2)]);

        Assert.Null(ScanFailureJournal.TryRead(_dir));
    }

    [Fact]
    public void TryRead_ARecordBeingReplacedConcurrently_NeverObservesAHalfWrittenFile()
    {
        var written = new ScanFailureRecord(ScanIntent.UserFullRebuild, 137, 2, 4, T0, T0);
        bool stop = false;
        var writer = new Thread(
            () =>
            {
                while (!Volatile.Read(ref stop))
                    ScanFailureJournal.TryWrite(_dir, written);
            });
        writer.Start();

        try
        {
            for (int i = 0; i < 400; i++)
            {
                ScanFailureRecord? read = ScanFailureJournal.TryRead(_dir);
                if (read is not null)
                    Assert.Equal(written, read);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            writer.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void PersistedPolicy_SharesTheRecordAcrossInstances_SoARestartInheritsTheBackoff()
    {
        DateTimeOffset now = T0;
        PersistedScanFailurePolicy first = NewPolicy(() => now, priorArtifactUsable: false);

        first.RecordFailure(ScanIntent.UserFullRebuild, exitCode: 137, jobs: 4);

        PersistedScanFailurePolicy afterRestart = NewPolicy(() => now, priorArtifactUsable: false);
        ScanAttemptDecision deferred = afterRestart.Evaluate(ScanIntent.UserFullRebuild);

        Assert.False(deferred.Attempt);
        Assert.Equal(1, deferred.ConsecutiveFailures);

        now += ScanFailurePolicy.FirstBackoff;
        ScanAttemptDecision due = afterRestart.Evaluate(ScanIntent.UserFullRebuild);

        Assert.True(due.Attempt);
        Assert.Equal(1, due.Jobs);
    }

    [Fact]
    public void PersistedPolicy_RecordSuccess_ClearsTheSharedRecord()
    {
        PersistedScanFailurePolicy policy = NewPolicy(() => T0, priorArtifactUsable: false);
        policy.RecordFailure(ScanIntent.UserFullRebuild, exitCode: 1, jobs: 4);

        policy.RecordSuccess(ScanIntent.UserFullRebuild);

        Assert.Null(ScanFailureJournal.TryRead(_dir));
        Assert.True(NewPolicy(() => T0, priorArtifactUsable: false).Evaluate(ScanIntent.UserFullRebuild).Attempt);
    }

    [Fact]
    public void PersistedPolicy_ADeltaSuccess_LeavesAForceIntentRecordIntactAcrossProcesses()
    {
        DateTimeOffset now = T0;
        NewPolicy(() => now, priorArtifactUsable: false)
            .RecordFailure(ScanIntent.UserFullRebuild, exitCode: 137, jobs: 4);

        NewPolicy(() => now, priorArtifactUsable: false).RecordSuccess(ScanIntent.IncrementalReconcile);

        ScanFailureRecord? survived = ScanFailureJournal.TryRead(_dir);
        Assert.Equal(1, survived?.ConsecutiveFailures);
        Assert.False(NewPolicy(() => now, priorArtifactUsable: false).Evaluate(ScanIntent.UserFullRebuild).Attempt);
    }

    [Fact]
    public void PersistedPolicy_RecordDowngradedServe_KeepsTheStreakButConsumesTheAttemptSlot()
    {
        DateTimeOffset now = T0;
        PersistedScanFailurePolicy policy = NewPolicy(() => now, priorArtifactUsable: true);
        policy.RecordFailure(ScanIntent.UserFullRebuild, exitCode: 137, jobs: 4);
        now += ScanFailurePolicy.FirstBackoff;

        Assert.True(policy.Evaluate(ScanIntent.UserFullRebuild).Downgraded);

        policy.RecordDowngradedServe();

        Assert.Equal(1, ScanFailureJournal.TryRead(_dir)?.ConsecutiveFailures);
        Assert.False(NewPolicy(() => now, priorArtifactUsable: true).Evaluate(ScanIntent.UserFullRebuild).Attempt);

        now += ScanFailurePolicy.FirstBackoff;

        Assert.True(NewPolicy(() => now, priorArtifactUsable: true).Evaluate(ScanIntent.UserFullRebuild).Attempt);
    }

    [Fact]
    public void PersistedPolicy_RecordPath_IsTheWorkspaceRecord() =>
        Assert.Equal(RecordPath, NewPolicy(() => T0, priorArtifactUsable: false).RecordPath);

    [Fact]
    public void PersistedPolicy_For_DerivesTheRecordPathFromTheArtifactsMillerDirectory() =>
        Assert.Equal(
            RecordPath,
            PersistedScanFailurePolicy.For(Path.Combine(_dir, "symbols.db"), _dir).RecordPath);

    private PersistedScanFailurePolicy NewPolicy(Func<DateTimeOffset> utcNow, bool priorArtifactUsable) =>
        PersistedScanFailurePolicy.ForTest(_dir, () => priorArtifactUsable, utcNow, static () => 0);
}
