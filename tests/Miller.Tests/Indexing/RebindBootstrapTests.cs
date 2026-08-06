using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server.Logging;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class RebindBootstrapTests : IDisposable
{
    private readonly string _work;
    private readonly string _targetRoot;
    private readonly string _targetDb;
    private readonly string _stagingDb;
    private readonly string _sourceRoot;
    private readonly string _sourceDb;
    private readonly InMemoryScanFailurePolicy _failurePolicy = new();

    private int _copyCalls;
    private int _rebindCalls;
    private int _scanCalls;
    private int _promoteCalls;
    private string? _rebindRoot;
    private ExtractIndexLevel? _scanLevel;

    public RebindBootstrapTests()
    {
        string raw = Path.Combine(Path.GetTempPath(), "miller-rebind-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raw);
        _work = PathCanonicalizer.CanonicalizeRoot(raw);
        _targetRoot = Path.Combine(_work, "worktree");
        _sourceRoot = Path.Combine(_work, "main");
        Directory.CreateDirectory(Path.Combine(_targetRoot, ".miller"));
        Directory.CreateDirectory(Path.Combine(_sourceRoot, ".miller"));
        _targetDb = Path.Combine(_targetRoot, ".miller", "symbols.db");
        _stagingDb = FullRebuildPromotion.RebuildDbPathFor(_targetDb);
        _sourceDb = Path.Combine(_sourceRoot, ".miller", "symbols.db");
        File.WriteAllText(_sourceDb, "source artifact");
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void TryRebind_WhenEverySeamSucceeds_PromotesAndReportsTheDeltaRevision()
    {
        RebindBootstrapOutcome outcome = Run();

        Assert.Equal(RebindBootstrapOutcome.Kind.Promoted, outcome.Result);
        Assert.Equal(41L, outcome.Revision);
        Assert.Equal(_sourceRoot, outcome.SourceRoot);
        Assert.Equal(1, _copyCalls);
        Assert.Equal(1, _rebindCalls);
        Assert.Equal(1, _scanCalls);
        Assert.Equal(1, _promoteCalls);
        Assert.Equal(_targetRoot, _rebindRoot);
        Assert.Null(_failurePolicy.Read());
    }

    [Fact]
    public void TryRebind_WhenTheSnapshotRecordsSymbolsLevel_ScansAtTheRecordedLevel()
    {
        Run(
            Seams() with
            {
                ReadSnapshotInputs = (_, sourceRoot, policy) =>
                    SnapshotInputs(sourceRoot, policy) with { RecordedIndexLevel = IndexLevels.SymbolsMetadataValue },
            });

        Assert.Equal(ExtractIndexLevel.Symbols, _scanLevel);
    }

    [Fact]
    public void TryRebind_WhenTheSnapshotRecordsNoLevel_ScansAtFullLevel()
    {
        Run(
            Seams() with
            {
                ReadSnapshotInputs = (_, sourceRoot, policy) =>
                    SnapshotInputs(sourceRoot, policy) with { RecordedIndexLevel = null },
            });

        Assert.Equal(ExtractIndexLevel.Full, _scanLevel);
    }

    [Fact]
    public void TryRebind_WhenTheRebindIsASameRootNoOp_StillPromotes()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                Rebind = (db, root, _) =>
                {
                    _rebindCalls++;
                    _rebindRoot = root;
                    return new RebindReport(root, root, "artifact-1", "artifact-1", Changed: false);
                },
            });

        Assert.Equal(RebindBootstrapOutcome.Kind.Promoted, outcome.Result);
        Assert.Equal(1, _promoteCalls);
    }

    [Fact]
    public void TryRebind_WhenTheTargetIsNotALinkedWorktree_IsIneligibleAndNeverCopies()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                ResolveLayout = _ => new GitWorktreeLayout(
                    Path.Combine(_targetRoot, ".git"), Path.Combine(_targetRoot, ".git"), _targetRoot),
            });

        Assert.Equal(RebindBootstrapOutcome.Kind.Ineligible, outcome.Result);
        Assert.Equal(0, _copyCalls);
        Assert.False(File.Exists(_stagingDb));
        Assert.Null(_failurePolicy.Read());
    }

    [Fact]
    public void TryRebind_WhenTheKillSwitchIsOff_IsIneligibleAndNeverCopies()
    {
        RebindBootstrapOutcome outcome = Run(Seams() with { ReadEnvironmentVariable = name => name == RebindBootstrap.EnabledEnvVar ? "off" : null });

        Assert.Equal(RebindBootstrapOutcome.Kind.Ineligible, outcome.Result);
        Assert.Equal(0, _copyCalls);
    }

    [Fact]
    public void TryRebind_WhenAScanFailureRecordStands_IsIneligibleAndNeverCopies()
    {
        _failurePolicy.RecordFailure(ScanIntent.IncrementalReconcile, exitCode: 1, jobs: 2);

        RebindBootstrapOutcome outcome = Run();

        Assert.Equal(RebindBootstrapOutcome.Kind.Ineligible, outcome.Result);
        Assert.Equal(0, _copyCalls);
    }

    [Fact]
    public void TryRebind_WhenNoMainCheckoutSiblingIsRegistered_IsIneligibleAndNeverCopies()
    {
        RebindBootstrapOutcome outcome = Run(Seams() with { FindMainCheckout = (_, _) => null });

        Assert.Equal(RebindBootstrapOutcome.Kind.Ineligible, outcome.Result);
        Assert.Equal(0, _copyCalls);
    }

    [Fact]
    public void TryRebind_WhenTheSourceHeartbeatIsFresh_IsIneligibleAndNeverCopies()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddDays(1);

        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                UtcNow = () => now,
                ReadSourceHeartbeatUtc = _ => now - TimeSpan.FromSeconds(5),
            });

        Assert.Equal(RebindBootstrapOutcome.Kind.Ineligible, outcome.Result);
        Assert.Contains("scan", outcome.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _copyCalls);
        Assert.Null(_failurePolicy.Read());
    }

    [Fact]
    public void TryRebind_WhenTheSourceHeartbeatIsStale_ProceedsToCopy()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddDays(1);

        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                UtcNow = () => now,
                ReadSourceHeartbeatUtc = _ => now - TimeSpan.FromMinutes(10),
            });

        Assert.Equal(RebindBootstrapOutcome.Kind.Promoted, outcome.Result);
        Assert.Equal(1, _copyCalls);
    }

    [Fact]
    public void TryRebind_WhenTheCopyBudgetIsExhausted_FailsAtCopyCleansStagingAndRecordsW8()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                CopySnapshot = (_, destination, _) =>
                {
                    _copyCalls++;
                    File.WriteAllText(destination, "partial");
                    return BackupOutcome.BudgetExhausted;
                },
            });

        AssertFailedAt(RebindStage.Copy, outcome, expectedExitCode: null);
        Assert.Equal(0, _rebindCalls);
    }

    [Fact]
    public void TryRebind_WhenTheCopyFails_FailsAtCopyCleansStagingAndRecordsW8()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                CopySnapshot = (_, destination, _) =>
                {
                    _copyCalls++;
                    File.WriteAllText(destination, "partial");
                    return BackupOutcome.Failed("disk on fire");
                },
            });

        AssertFailedAt(RebindStage.Copy, outcome, expectedExitCode: null);
        Assert.Contains("disk on fire", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRebind_WhenTheSnapshotFailsValidation_FailsAtValidateCleansStagingAndRecordsW8()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                ReadSnapshotInputs = (_, sourceRoot, policy) =>
                    SnapshotInputs(sourceRoot, policy) with { HasCommittedRevision = false },
            });

        AssertFailedAt(RebindStage.Validate, outcome, expectedExitCode: null);
        Assert.Equal(0, _rebindCalls);
    }

    [Fact]
    public void TryRebind_WhenTheRebindIsRefusedAsIncompatible_FailsAtRebindCleansStagingAndRecordsW8()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                Rebind = (_, _, _) =>
                {
                    _rebindCalls++;
                    throw new IncompatibleExtractException("exit 3: fingerprint_mismatch");
                },
            });

        AssertFailedAt(RebindStage.Rebind, outcome, expectedExitCode: null);
        Assert.Equal(0, _scanCalls);
    }

    [Fact]
    public void TryRebind_WhenTheRebindIsRefusedRecoverably_FailsAtRebindCleansStagingAndRecordsW8()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                Rebind = (_, _, _) =>
                {
                    _rebindCalls++;
                    throw new JulieExtractFailedException(
                        "artifact_changed", Array.Empty<ReportDiagnostic>(), standardError: "");
                },
            });

        AssertFailedAt(RebindStage.Rebind, outcome, expectedExitCode: 1);
        Assert.Equal(0, _scanCalls);
    }

    [Fact]
    public void TryRebind_WhenTheDeltaScanFails_FailsAtDeltaScanCleansStagingAndRecordsW8()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                RunDeltaScan = (_, _) =>
                {
                    _scanCalls++;
                    throw new JulieExtractException("killed", standardError: "", exitCode: 137);
                },
            });

        AssertFailedAt(RebindStage.DeltaScan, outcome, expectedExitCode: 137);
        Assert.Equal(0, _promoteCalls);
    }

    [Fact]
    public void TryRebind_WhenPromoteThrowsBeforeTheMove_FailsAtPromoteCleansStagingAndRecordsW8()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                Promote = _ =>
                {
                    _promoteCalls++;
                    throw new IOException("the live file stayed locked");
                },
            });

        AssertFailedAt(RebindStage.Promote, outcome, expectedExitCode: null);
    }

    [Fact]
    public void TryRebind_WhenPromoteThrowsAfterTheMove_AdoptsTheLiveArtifactAsSuccess()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                Promote = liveDb =>
                {
                    _promoteCalls++;
                    File.Move(_stagingDb, liveDb, overwrite: true);
                    throw new IOException("failed while clearing the rebuild sidecars");
                },
                LiveArtifactUsable = (liveDb, _) => File.Exists(liveDb),
            });

        Assert.Equal(RebindBootstrapOutcome.Kind.Promoted, outcome.Result);
        Assert.Equal(41L, outcome.Revision);
        Assert.Null(_failurePolicy.Read());
        Assert.True(File.Exists(_targetDb));
    }

    [Fact]
    public void TryRebind_WhenTheReconciledScanIsClean_PromotesWithNoWarning()
    {
        RebindBootstrapOutcome outcome = Run();

        Assert.Equal(RebindBootstrapOutcome.Kind.Promoted, outcome.Result);
        Assert.Null(outcome.Warning);
    }

    [Fact]
    public void TryRebind_WhenTheReconciledScanIsPartial_PromotesCarryingTheWarning()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                RunDeltaScan = (_, _) =>
                {
                    _scanCalls++;
                    return PartialReport(revision: 41);
                },
            });

        Assert.Equal(RebindBootstrapOutcome.Kind.Promoted, outcome.Result);
        Assert.Contains("PARTIAL", outcome.Warning ?? "", StringComparison.Ordinal);
        Assert.Contains("broken.cs", outcome.Warning ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void TryRebind_WhenPromoteThrowsAfterTheMoveOnAPartialScan_AdoptsAndStillCarriesTheWarning()
    {
        RebindBootstrapOutcome outcome = Run(
            Seams() with
            {
                RunDeltaScan = (_, _) =>
                {
                    _scanCalls++;
                    return PartialReport(revision: 41);
                },
                Promote = liveDb =>
                {
                    _promoteCalls++;
                    File.Move(_stagingDb, liveDb, overwrite: true);
                    throw new IOException("failed while clearing the rebuild sidecars");
                },
                LiveArtifactUsable = (liveDb, _) => File.Exists(liveDb),
            });

        Assert.Equal(RebindBootstrapOutcome.Kind.Promoted, outcome.Result);
        Assert.Contains("PARTIAL", outcome.Warning ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackAttemptAfterRebind_WhenTheRebindFailed_ReEvaluatesSoAPostSigkillClampIsNotLost()
    {
        var policy = new CountingScanFailurePolicy(Decision(jobs: ScanFailurePolicy.PostSigkillJobs));
        ScanAttemptDecision original = Decision(jobs: null);

        ScanAttemptDecision fallback = RebindBootstrap.FallbackAttemptAfterRebind(
            policy, ScanIntent.IncrementalReconcile, original,
            RebindBootstrapOutcome.Failed(RebindStage.DeltaScan, "killed", _sourceRoot, "ab12cd"));

        Assert.Equal(1, policy.Evaluations);
        Assert.Equal(ScanFailurePolicy.PostSigkillJobs, fallback.Jobs);
    }

    [Fact]
    public void FallbackAttemptAfterRebind_WhenTheRebindFailed_ReEvaluatesWithTheBackoffTimerBypassed()
    {
        var policy = new CountingScanFailurePolicy(Decision(jobs: ScanFailurePolicy.PostSigkillJobs));

        RebindBootstrap.FallbackAttemptAfterRebind(
            policy, ScanIntent.IncrementalReconcile, Decision(jobs: null),
            RebindBootstrapOutcome.Failed(RebindStage.DeltaScan, "killed", _sourceRoot, "ab12cd"));

        Assert.True(policy.LastBypassBackoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackAttemptAfterRebind_WhenTheRebindDidNotFail_KeepsTheOriginalWithoutReEvaluating(
        bool promoted)
    {
        var policy = new CountingScanFailurePolicy(Decision(jobs: ScanFailurePolicy.PostSigkillJobs));
        ScanAttemptDecision original = Decision(jobs: null);
        RebindBootstrapOutcome outcome = promoted
            ? RebindBootstrapOutcome.Promoted("rebound", 41L, _sourceRoot, "ab12cd", warning: null)
            : RebindBootstrapOutcome.Ineligible("not a linked worktree");

        ScanAttemptDecision fallback =
            RebindBootstrap.FallbackAttemptAfterRebind(policy, ScanIntent.IncrementalReconcile, original, outcome);

        Assert.Equal(0, policy.Evaluations);
        Assert.Same(original, fallback);
    }

    [Fact]
    public void TryRebind_WhenTheTargetAlreadyHasAnArtifact_IsIneligibleAndNeverCopies()
    {
        File.WriteAllText(_targetDb, "live artifact");

        RebindBootstrapOutcome outcome = Run();

        Assert.Equal(RebindBootstrapOutcome.Kind.Ineligible, outcome.Result);
        Assert.Equal(0, _copyCalls);
    }

    [Fact]
    public void TryRebind_WhenTheRootWasReplaced_IsIneligibleAndNeverCopies()
    {
        RebindBootstrapOutcome outcome = Run(request: Request() with { RootReplacementDetected = true });

        Assert.Equal(RebindBootstrapOutcome.Kind.Ineligible, outcome.Result);
        Assert.Equal(0, _copyCalls);
    }

    [Fact]
    public void TryRebind_AtEntry_ClearsStagingDebrisLeftByADeadRebind()
    {
        File.WriteAllText(_stagingDb, "debris");
        File.WriteAllText(_stagingDb + "-wal", "debris");

        Run(
            Seams() with
            {
                CopySnapshot = (_, destination, _) =>
                {
                    _copyCalls++;
                    Assert.False(File.Exists(destination));
                    Assert.False(File.Exists(destination + "-wal"));
                    File.WriteAllText(destination, "snapshot");
                    return BackupOutcome.Completed;
                },
            });

        Assert.Equal(1, _copyCalls);
    }

    [Fact]
    public void DiscardStaging_RemovesTheWholeRebuildTrio()
    {
        File.WriteAllText(_stagingDb, "debris");
        File.WriteAllText(_stagingDb + "-wal", "debris");
        File.WriteAllText(_stagingDb + "-shm", "debris");

        RebindBootstrap.DiscardStaging(_targetDb);

        Assert.False(File.Exists(_stagingDb));
        Assert.False(File.Exists(_stagingDb + "-wal"));
        Assert.False(File.Exists(_stagingDb + "-shm"));
    }

    private void AssertFailedAt(RebindStage stage, RebindBootstrapOutcome outcome, int? expectedExitCode)
    {
        Assert.Equal(RebindBootstrapOutcome.Kind.Failed, outcome.Result);
        Assert.Equal(stage, outcome.Stage);
        Assert.False(File.Exists(_stagingDb));
        Assert.False(File.Exists(_targetDb));
        ScanFailureRecord record = Assert.IsType<ScanFailureRecord>(_failurePolicy.Read());
        Assert.Equal(ScanIntent.IncrementalReconcile, record.Intent);
        Assert.Equal(expectedExitCode, record.ExitCode);
        Assert.Equal(3, record.Jobs);
    }

    private RebindBootstrapOutcome Run(
        RebindBootstrapSeams? seams = null, RebindBootstrapRequest? request = null) =>
        RebindBootstrap.TryRebind(request ?? Request(), seams ?? Seams(), TestContext.Current.CancellationToken);

    private RebindBootstrapRequest Request() => new()
    {
        TargetRoot = _targetRoot,
        TargetDbPath = _targetDb,
        RegistryDbPath = Path.Combine(_work, "workspaces.db"),
        RootReplacementDetected = false,
        TargetLevelPolicy = IndexLevelPolicy.Progressive,
        FailurePolicy = _failurePolicy,
        Jobs = 3,
    };

    private RebindBootstrapSeams Seams() => new()
    {
        ResolveLayout = _ => new GitWorktreeLayout(
            Path.Combine(_sourceRoot, ".git", "worktrees", "wt"),
            Path.Combine(_sourceRoot, ".git"),
            _sourceRoot),
        FindMainCheckout = (_, _) => new WorkspaceRegistryRow(
            "workspace-id", "ab12cd", _sourceRoot, _sourceDb, DateTimeOffset.UnixEpoch, null, null,
            WorkspaceRegistryState.Ready, null),
        ReadArtifactBinaryVersion = _ => MillerExtractContract.PinnedJulieExtractVersion,
        ReadEnvironmentVariable = _ => null,
        ReadSourceHeartbeatUtc = _ => null,
        UtcNow = () => DateTimeOffset.UnixEpoch,
        CopySnapshot = (_, destination, _) =>
        {
            _copyCalls++;
            File.WriteAllText(destination, "snapshot");
            return BackupOutcome.Completed;
        },
        ReadSnapshotInputs = (_, sourceRoot, policy) => SnapshotInputs(sourceRoot, policy),
        Rebind = (db, root, _) =>
        {
            _rebindCalls++;
            _rebindRoot = root;
            return new RebindReport(_sourceRoot, root, "artifact-1", "artifact-2", Changed: true);
        },
        RunDeltaScan = (_, level) =>
        {
            _scanCalls++;
            _scanLevel = level;
            return Report(revision: 41);
        },
        Promote = liveDb =>
        {
            _promoteCalls++;
            File.Move(_stagingDb, liveDb, overwrite: true);
        },
        LiveArtifactUsable = (_, _) => false,
        DescribeScanWarning = ExtractReportLog.DescribeWarning,
    };

    private static RebindSnapshotInputs SnapshotInputs(string sourceRoot, IndexLevelPolicy policy) => new()
    {
        SchemaCompatible = true,
        HashAlgorithm = MillerExtractContract.ExpectedHashAlgorithm,
        RecordedRootPath = sourceRoot,
        SourceRoot = sourceRoot,
        HasCommittedRevision = true,
        BinaryVersion = MillerExtractContract.PinnedJulieExtractVersion,
        PinnedExtractorVersion = MillerExtractContract.PinnedJulieExtractVersion,
        RecordedIndexLevel = IndexLevels.FullMetadataValue,
        TargetLevelPolicy = policy,
    };

    private static ExtractReport Report(long revision) => new(
        ReportSchemaVersion: 1, Status: "ok", Operation: "scan", Mode: "incremental",
        Input: null, Artifact: null, Tool: null,
        RevisionBlock: new ExtractRevision(revision, revision),
        Counts: null,
        Errors: Array.Empty<ReportDiagnostic>(), Warnings: Array.Empty<ReportDiagnostic>());

    private static ExtractReport PartialReport(long revision) => Report(revision) with
    {
        Status = "partial",
        Counts = new ExtractCounts(
            FilesScanned: 2, FilesChanged: 1, FilesUnchanged: 0, FilesUnsupported: 0, FilesDeleted: 0,
            FilesFailed: 1, RowsWritten: null, Totals: null),
        Errors = new[]
        {
            new ReportDiagnostic("parse_failed", "unbalanced braces", "broken.cs", "broken.cs", Recoverable: true),
        },
    };

    private static ScanAttemptDecision Decision(int? jobs) => new(
        Attempt: true, ScanIntent.IncrementalReconcile, jobs, Downgraded: false, RetryAtUtc: null,
        ConsecutiveFailures: 0);

    private sealed class CountingScanFailurePolicy(ScanAttemptDecision decision) : IScanFailurePolicy
    {
        public int Evaluations { get; private set; }

        public bool LastBypassBackoff { get; private set; }

        public ScanAttemptDecision Evaluate(ScanIntent intent, bool bypassBackoff = false)
        {
            Evaluations++;
            LastBypassBackoff = bypassBackoff;
            return decision;
        }

        public void RecordSuccess(ScanIntent completed)
        {
        }

        public void RecordDowngradedServe()
        {
        }

        public void RecordFailure(ScanIntent intent, int? exitCode, int jobs)
        {
        }

        public ScanFailureRecord? Read() => null;
    }
}
