using Microsoft.Extensions.Logging;
using Miller.Core.Freshness;
using Miller.Indexing;
using Miller.Server.Hosting;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the indexer's pure dispatch loop (the testable seam of <c>IndexerService</c>): drain the queue, route
/// drained events through <see cref="WatchEventRouter"/> with an injected exists-stat and the
/// rescan/HEAD flag, then execute each <see cref="ExtractOp"/> through an injected <see cref="IExtractOps"/>.
/// No FileSystemWatcher, no subprocess, no SQLite — the runner is faked, the stat is a predicate, the queue is
/// the real Core queue. Covers: created/modified -> update, delete -> delete, rename -> delete+update,
/// overflow/HEAD -> a single scan (events dropped + NeedsRescan cleared), the empty-drain no-op, a vanished
/// create routed to delete, and the failure-isolation contract (one op throwing does not abort the batch).
/// </summary>
public sealed class IndexerCoreTests
{
    /// <summary>A fake <see cref="IExtractOps"/> that records calls and can be told to throw for a path.</summary>
    private sealed class RecordingOps : IExtractOps
    {
        public List<string> Calls { get; } = new();
        public HashSet<string> ThrowOnUpdatePath { get; } = new(StringComparer.Ordinal);

        /// <summary>Per-path exception override: throw THIS exception for the given update path (finding-6).</summary>
        public Dictionary<string, Exception> ThrowExceptionOnUpdatePath { get; } = new(StringComparer.Ordinal);

        public ExtractReport Update(string canonicalFile)
        {
            Calls.Add($"update:{canonicalFile}");
            if (ThrowExceptionOnUpdatePath.TryGetValue(canonicalFile, out var custom))
                throw custom;
            if (ThrowOnUpdatePath.Contains(canonicalFile))
                throw new JulieExtractException("boom", standardError: string.Empty);
            return Stub("changed");
        }

        public ExtractReport Delete(string canonicalFile)
        {
            Calls.Add($"delete:{canonicalFile}");
            return Stub("deleted");
        }

        /// <summary>Makes the next (and every) whole-repo scan fail, to pin the rescan-latch commit point.</summary>
        public Exception? ThrowOnScan { get; set; }

        /// <summary>The force flag of every scan dispatched, in order — the scan-intent contract.</summary>
        public List<bool> ScanForce { get; } = new();

        /// <summary>The intent of every scan dispatched, in order.</summary>
        public List<ScanIntent> ScanIntents { get; } = new();

        /// <summary>The explicit --jobs cap of every scan dispatched, in order (null = ambient policy).</summary>
        public List<int?> ScanJobs { get; } = new();

        public bool BlockScan { get; set; }
        public ManualResetEventSlim ScanStarted { get; } = new();
        public ManualResetEventSlim ReleaseScan { get; } = new();

        public ExtractReport Scan(ScanIntent intent = ScanIntent.IncrementalReconcile, int? jobs = null)
        {
            bool force = ScanIntentPolicy.RequiresForce(intent);
            Calls.Add(force ? "scan:force" : "scan");
            ScanForce.Add(force);
            ScanIntents.Add(intent);
            ScanJobs.Add(jobs);
            if (BlockScan)
            {
                ScanStarted.Set();
                ReleaseScan.Wait();
            }
            if (ThrowOnScan is { } failure)
                throw failure;
            return Stub("scanned");
        }

        private static ExtractReport Stub(string status) => new(
            ReportSchemaVersion: 1, Status: status, Operation: "test", Mode: "single_file", Input: null,
            Artifact: new ExtractArtifact(
                DbPath: "x", RootPath: "/abs/r", ArtifactId: "a",
                SchemaVersion: MillerExtractContract.ExpectedSchemaVersion,
                ExtractContractVersion: MillerExtractContract.ExpectedExtractContractVersion,
                SqliteSchemaVersion: MillerExtractContract.ExpectedSqliteSchemaVersion,
                JsonlSchemaVersion: 1, HashAlgorithm: MillerExtractContract.ExpectedHashAlgorithm,
                ParserInventoryFingerprint: "p", CapabilitySnapshotFingerprint: "c"),
            Tool: new ExtractTool("julie-extract", "2.0.0"),
            RevisionBlock: new ExtractRevision(2, 2),
            Counts: new ExtractCounts(0, 1, 0, 0, 0, 0, RowsWritten: null, Totals: null),
            Errors: System.Array.Empty<ReportDiagnostic>(), Warnings: System.Array.Empty<ReportDiagnostic>());
    }

    /// <summary>One captured log call (the level + the formatted message + the exception, if any).</summary>
    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    /// <summary>A minimal recording <see cref="ILogger"/> so the outcome-aware log LEVEL can be asserted.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class TestClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    // The scan-failure policy owns the whole-repo retry timer, so the test clock is injected there. Jitter is
    // drawn as zero so the schedule is exactly ScanFailurePolicy's documented one.
    private static IndexerCore NewCore(
        RecordingOps ops,
        Func<string, bool> exists,
        ILogger? logger = null,
        TestClock? clock = null,
        IScanFailurePolicy? failurePolicy = null) =>
        new(new WatchEventQueue(), ops, exists, logger,
            failurePolicy ?? NewFailurePolicy(clock));

    private static InMemoryScanFailurePolicy NewFailurePolicy(
        TestClock? clock, Func<bool>? priorArtifactUsable = null) =>
        new(priorArtifactUsable,
            clock is null ? null : () => clock.UtcNow,
            jitter: static () => 0);

    [Fact]
    public void DrainAndProcess_EmptyQueueNoFlag_DoesNothing()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        bool didWork = core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.False(didWork);
        Assert.Empty(ops.Calls);
    }

    [Fact]
    public void HasPendingWork_IncludesSignaledRescan_AndClearsAfterDrain()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        core.SignalRescan();

        Assert.True(core.HasPendingWork);

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { "scan" }, ops.Calls);
        Assert.False(core.HasPendingWork);
    }

    [Fact]
    public async Task HasPendingWork_ReturnsTrueWhileDrainExecutesBlockingScan()
    {
        var ops = new RecordingOps { BlockScan = true };
        var core = NewCore(ops, _ => true);
        core.SignalRescan();
        var cancellationToken = TestContext.Current.CancellationToken;

        Task drain = Task.Run(() => core.DrainAndProcess(
            headChanged: false, wholeRepoScanAdmitted: true, out _), cancellationToken);
        Task<bool>? pendingWork = null;

        try
        {
            Assert.True(ops.ScanStarted.Wait(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.False(drain.IsCompleted);

            pendingWork = Task.Run(() => core.HasPendingWork, cancellationToken);

            await pendingWork.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.True(await pendingWork);
            Assert.False(ops.ReleaseScan.IsSet);
        }
        finally
        {
            ops.ReleaseScan.Set();
            await drain.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (pendingWork is not null)
                await pendingWork.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    [Fact]
    public void DrainAndProcess_CreatedAndModified_ExistingFiles_BecomeUpdates()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Created));
        core.Queue.Enqueue(new WatchEvent("/repo/b.cs", WatchEventKind.Modified));

        bool didWork = core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.True(didWork);
        Assert.Equal(new[] { "update:/repo/a.cs", "update:/repo/b.cs" }, ops.Calls);
        Assert.Equal(0, core.Queue.Count);
    }

    [Fact]
    public void DrainAndProcess_DeletedEvent_BecomesDelete()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => false);
        core.Queue.Enqueue(new WatchEvent("/repo/gone.cs", WatchEventKind.Deleted));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { "delete:/repo/gone.cs" }, ops.Calls);
    }

    [Fact]
    public void DrainAndProcess_CreatedButVanished_RoutesToDelete()
    {
        var ops = new RecordingOps();
        // exists==false for the created path: the create/modify raced a removal -> delete (router rule).
        var core = NewCore(ops, _ => false);
        core.Queue.Enqueue(new WatchEvent("/repo/flicker.cs", WatchEventKind.Created));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { "delete:/repo/flicker.cs" }, ops.Calls);
    }

    [Fact]
    public void DrainAndProcess_Rename_BecomesDeleteOldThenUpdateNew()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, path => path == "/repo/new.cs"); // only the destination exists
        core.Queue.Enqueue(WatchEvent.Renamed("/repo/old.cs", "/repo/new.cs"));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { "delete:/repo/old.cs", "update:/repo/new.cs" }, ops.Calls);
    }

    [Fact]
    public void DrainAndProcess_HeadChanged_ForcesSingleScan_AndDropsPerFileEvents()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/b.cs", WatchEventKind.Modified));

        bool didWork = core.DrainAndProcess(headChanged: true, wholeRepoScanAdmitted: true, out _);

        Assert.True(didWork);
        Assert.Equal(new[] { "scan" }, ops.Calls); // exactly one scan, per-file events dropped
        Assert.Equal(0, core.Queue.Count);
    }

    [Fact]
    public void DrainAndProcess_OverflowNeedsRescan_ForcesScan_AndClearsTheFlag()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        // Push past MaxQueue with distinct paths to trip the overflow drain (sets NeedsRescan).
        for (int i = 0; i <= WatchEventQueue.MaxQueue; i++)
            core.Queue.Enqueue(new WatchEvent($"/repo/f{i}.cs", WatchEventKind.Modified));
        Assert.True(core.Queue.NeedsRescan);

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { "scan" }, ops.Calls);
        // The core must clear NeedsRescan after scheduling the scan (so the next drain does not re-scan).
        Assert.False(core.Queue.NeedsRescan);
    }

    [Fact]
    public void DrainAndProcess_HeadChangedButEmptyQueue_StillScans()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        bool didWork = core.DrainAndProcess(headChanged: true, wholeRepoScanAdmitted: true, out _);

        Assert.True(didWork);
        Assert.Equal(new[] { "scan" }, ops.Calls);
    }

    [Fact]
    public void DrainAndProcess_OneOpThrows_DoesNotAbortTheRestOfTheBatch()
    {
        var ops = new RecordingOps();
        ops.ThrowOnUpdatePath.Add("/repo/bad.cs");
        var core = NewCore(ops, _ => true);
        core.Queue.Enqueue(new WatchEvent("/repo/good1.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/good2.cs", WatchEventKind.Modified));

        // The bad op throws inside the loop; the core must isolate it (decision-10: keep going, don't abort).
        bool didWork = core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.True(didWork);
        Assert.Equal(
            new[] { "update:/repo/good1.cs", "update:/repo/bad.cs", "update:/repo/good2.cs" },
            ops.Calls);
    }

    [Fact]
    public void Enqueue_CoalescesSamePath_SoOnlyOneOpIsEmitted()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        // Two modifies of the same path coalesce to one queue entry -> one update op.
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { "update:/repo/a.cs" }, ops.Calls);
    }

    // ---- finding-6: outcome-aware error handling on JulieExtractFailedException (decision-10) ----

    private static JulieExtractFailedException Failed(params (string code, bool recoverable)[] diags)
    {
        var errors = diags
            .Select(d => new ReportDiagnostic(
                Code: d.code, Message: $"{d.code} happened", Path: "/repo/x.cs",
                RootRelativePath: "x.cs", Recoverable: d.recoverable))
            .ToArray();
        return new JulieExtractFailedException(
            $"exit 1: {string.Join(",", diags.Select(d => d.code))}", errors, standardError: "");
    }

    private static JulieExtractFailedException FailedNoDiagnostics() =>
        new("exit 1: (no diagnostics)", System.Array.Empty<ReportDiagnostic>(), standardError: "");

    [Theory]
    [InlineData("lock_timeout", true)]          // julie marks a lock timeout recoverable
    [InlineData("data_loss_guard", false)]      // v1 emits recoverable:false, but Miller keeps-prior on it
    public void ExecuteIsolated_RecoverableFailure_LogsAtInformation_AndContinuesTheBatch(
        string code, bool recoverable)
    {
        // decision-10: a recoverable diagnostic (a lock timeout) or the keep-prior data-loss guard is EXPECTED —
        // keep the prior index, log at Info (not Error/Warning), and the next scan reconciles. No sibling abort.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = Failed((code, recoverable));
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/good1.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/good2.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        // All three ops ran (the failure was isolated, the batch continued).
        Assert.Equal(
            new[] { "update:/repo/good1.cs", "update:/repo/bad.cs", "update:/repo/good2.cs" }, ops.Calls);
        // Exactly ONE failure entry (the two successes now also log a per-file outcome at Debug, M8 §D4), at
        // Information, carrying the failed exception and naming the recoverable code.
        var entry = Assert.Single(logger.Entries, e => e.Exception is JulieExtractFailedException);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(code, entry.Message, StringComparison.Ordinal);
        // The successful siblings' Debug outcome lines are the only OTHER entries — no stray failure/warn lines.
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Debug));
    }

    [Theory]
    [InlineData("file_outside_root")]   // real v1 wire code (ReportCode::FileOutsideRoot, snake_case); recoverable:false, NOT keep-prior
    [InlineData("usage_error")]
    [InlineData("root_mismatch")]
    public void ExecuteIsolated_NonRecoverableFailure_LogsAtError(string abnormalCode)
    {
        // decision-10: usage / outside-root / root-mismatch operator errors must surface loudly (Error level),
        // NOT be hidden as a recoverable retry-later.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = Failed((abnormalCode, recoverable: false));
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(abnormalCode, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteIsolated_FailedWithNoStructuredErrors_LogsAtError_NotInformation()
    {
        // A failed report with an empty errors array is NOT a known recoverable case → treat it as abnormal
        // (surface loudly). Silently downgrading the no-code case to Info would hide real failures.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = FailedNoDiagnostics();
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public void ExecuteIsolated_MixedRecoverableAndAbnormal_TreatedAsRecoverable_IfAnyIsRecoverable()
    {
        // If the failure carries a recoverable diagnostic alongside others, the recoverable path wins (keep-prior,
        // retry later) — the next scan reconciles regardless, and we should not escalate a recoverable case.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] =
            Failed(("lock_timeout", recoverable: true), ("some_other_code", recoverable: false));
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    [Fact]
    public void ExecuteIsolated_UnexpectedException_LogsAtWarning()
    {
        // A non-JulieExtractFailedException (a base JulieExtractException for an unexpected exit code, an exec
        // failure, a JSON parse error) is truly unexpected → a generic Warning (keep-prior, continue).
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] =
            new JulieExtractException("crashed (exit 137)", standardError: "");
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    // ---- M8 §D3: surface julie's raw stderr tail next to the codes at the catch sites ----

    [Fact]
    public void ExecuteIsolated_AbnormalFailure_LogsCodes_AndJuliesStderrTail()
    {
        // The abnormal (Error) branch must now render BOTH the structured codes AND julie's raw stderr (which
        // Exception.ToString() drops) — the daemon-debugging payoff of D3.
        var failed = new JulieExtractFailedException(
            "exit 1: file_outside_root",
            new[] { new ReportDiagnostic("file_outside_root", "the path is outside the extract root", "/repo/x.cs",
                RootRelativePath: "x.cs", Recoverable: false) },
            standardError: "ERROR julie::extract: path '/repo/x.cs' is outside root '/repo'");
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = failed;
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("file_outside_root", entry.Message, StringComparison.Ordinal);
        Assert.Contains("is outside root", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteIsolated_RecoverableFailure_LogsCodes_AndJuliesStderrTail_AtInformation()
    {
        // The recoverable (Info) branch also surfaces the stderr tail so a "retry later" still shows julie's words.
        var failed = new JulieExtractFailedException(
            "exit 1: lock_timeout",
            new[] { new ReportDiagnostic("lock_timeout", "another writer held the lock", "/repo/x.cs",
                RootRelativePath: "x.cs", Recoverable: true) },
            standardError: "WARN julie::lock: timed out waiting for the write lock after 5s");
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = failed;
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("lock_timeout", entry.Message, StringComparison.Ordinal);
        Assert.Contains("timed out waiting for the write lock", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteIsolated_UnexpectedJulieException_LogsItsStderrTail_AtWarning()
    {
        // The generic catch routes through the helper too: a base JulieExtractException's stderr (a Rust panic)
        // is surfaced even though it carries no structured codes.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] =
            new JulieExtractException("crashed (exit 134)", standardError: "thread 'main' panicked at index.rs:42");
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("panicked at index.rs:42", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteIsolated_GenericNonJulieException_DoesNotEmitADanglingJulieStderrLabel()
    {
        // finding-7: the generic catch fires for non-julie exceptions too (a JSON parse error, an exec failure).
        // For those, the helper returns an EMPTY stderr tail, so a hardcoded "julie stderr:" label would render
        // as a dangling "julie stderr:" with nothing after it — asserting a julie context that is false. The label
        // must only appear when there IS a stderr tail to show.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] =
            new InvalidOperationException("the extract report JSON was unparseable");
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        // No stderr to show for a non-julie exception => the "julie stderr" label must be absent entirely.
        Assert.DoesNotContain("julie stderr", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecuteIsolated_PerFileOutcome_IsLoggedAtDebug_OnTheSuccessPath()
    {
        // M8 §D4: a successful per-file extract emits a Debug outcome line (path + resulting revision/status),
        // silent at the default Information level but invaluable when MILLER_LOG_LEVEL=Debug.
        var ops = new RecordingOps();
        var logger = new RecordingLogger(); // IsEnabled(Debug)==true, so the Debug line is captured here
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        var debug = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
        Assert.Contains("/repo/a.cs", debug.Message, StringComparison.Ordinal);
    }

    // ---- W3: the whole-repo rescan latch and machine-wide scan admission ----
    // A transient rescan signal (queue overflow, FSW overflow, a .git/HEAD move) used to be consumed by the drain
    // that observed it. Under the machine-wide governor a drain can be REFUSED admission, and a scan that does run
    // can still fail, so the signal has to survive both — otherwise a branch switch that lost a race is never
    // reconciled and the workspace serves a stale index until the next unrelated event.

    [Fact]
    public void WouldRunWholeRepoScan_IsFalse_ForAPerFileOnlyTick()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));

        Assert.False(core.WouldRunWholeRepoScan(headChanged: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WouldRunWholeRepoScan_ReflectsHeadChanged_WithoutConsumingAnySignal(bool headChanged)
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        Assert.Equal(headChanged, core.WouldRunWholeRepoScan(headChanged));
        Assert.Equal(headChanged, core.WouldRunWholeRepoScan(headChanged));
    }

    [Fact]
    public void WouldRunWholeRepoScan_IsTrue_AfterAnOverflowSignal()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        core.SignalRescan();

        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));
    }

    [Fact]
    public void RequestWholeRepoScan_ArmsTheLatch_AndTheNextAdmittedDrainScans()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        core.RequestWholeRepoScan(ScanIntent.IncrementalReconcile);

        Assert.True(core.HasPendingWork);
        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.Equal(new[] { "scan" }, ops.Calls);
        Assert.False(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_RefusedAdmission_RunsNothing_AndKeepsTheLatchArmed()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.SignalRescan();

        bool didWork = core.DrainAndProcess(
            headChanged: false, wholeRepoScanAdmitted: false, out bool usedWholeRepoScan);

        Assert.False(didWork);
        Assert.False(usedWholeRepoScan);
        Assert.Empty(ops.Calls);
        Assert.True(core.HasPendingWork);
        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));
    }

    [Fact]
    public void DrainAndProcess_RefusedAdmission_StillAppliesTheQueuedPerFileEvents()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/b.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: true, wholeRepoScanAdmitted: false, out bool usedWholeRepoScan);

        Assert.Equal(new[] { "update:/repo/a.cs", "update:/repo/b.cs" }, ops.Calls);
        Assert.False(usedWholeRepoScan);
        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));
    }

    [Fact]
    public void DrainAndProcess_RefusedAdmissionEveryTick_KeepsApplyingEdits_AndStillOwesAScan()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.SignalRescan();

        for (int tick = 0; tick < 3; tick++)
        {
            core.Queue.Enqueue(new WatchEvent($"/repo/{tick}.cs", WatchEventKind.Modified));
            core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: false, out _);
        }

        Assert.Equal(
            new[] { "update:/repo/0.cs", "update:/repo/1.cs", "update:/repo/2.cs" }, ops.Calls);

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
    }

    [Fact]
    public void DrainAndProcess_RefusedAdmissionWithABatchOverTheBound_RunsNoExtracts_AndKeepsItsEvents()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.SignalRescan();
        for (int i = 0; i <= IndexerCore.MaxDeferredScanDrain; i++)
            core.Queue.Enqueue(new WatchEvent($"/repo/{i}.cs", WatchEventKind.Modified));

        bool didWork = core.DrainAndProcess(
            headChanged: false, wholeRepoScanAdmitted: false, out bool usedWholeRepoScan);

        Assert.False(didWork);
        Assert.False(usedWholeRepoScan);
        Assert.Empty(ops.Calls);
        Assert.Equal(IndexerCore.MaxDeferredScanDrain + 1, core.Queue.Count);
        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));
    }

    [Fact]
    public void DrainAndProcess_RefusedAdmissionAtTheDrainBound_StillAppliesEveryQueuedEdit()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.SignalRescan();
        for (int i = 0; i < IndexerCore.MaxDeferredScanDrain; i++)
            core.Queue.Enqueue(new WatchEvent($"/repo/{i}.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: false, out _);

        Assert.Equal(IndexerCore.MaxDeferredScanDrain, ops.Calls.Count);
        Assert.Equal(0, core.Queue.Count);
    }

    [Fact]
    public void DrainAndProcess_ABatchLeftQueuedByARefusal_IsReconciledByOneScanOnceAdmitted()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.SignalRescan();
        for (int i = 0; i <= IndexerCore.MaxDeferredScanDrain; i++)
            core.Queue.Enqueue(new WatchEvent($"/repo/{i}.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: false, out _);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.Equal(new[] { "scan" }, ops.Calls);
        Assert.False(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_SmallBatchInsideTheScanFailureBackoff_StillAppliesEveryQueuedEdit()
    {
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: new TestClock());
        core.SignalRescan();
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        for (int i = 0; i < IndexerCore.MaxDeferredScanDrain; i++)
            core.Queue.Enqueue(new WatchEvent($"/repo/{i}.cs", WatchEventKind.Modified));
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(IndexerCore.MaxDeferredScanDrain + 1, ops.Calls.Count);
        Assert.Equal(0, core.Queue.Count);
    }

    [Fact]
    public void DrainAndProcess_LargeBatchInsideTheScanFailureBackoff_IsLeftForTheLatchedScan()
    {
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: new TestClock());
        core.SignalRescan();
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        ops.Calls.Clear();

        for (int i = 0; i <= IndexerCore.MaxDeferredScanDrain; i++)
            core.Queue.Enqueue(new WatchEvent($"/repo/{i}.cs", WatchEventKind.Modified));
        bool didWork = core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.False(didWork);
        Assert.Empty(ops.Calls);
        Assert.Equal(IndexerCore.MaxDeferredScanDrain + 1, core.Queue.Count);
    }

    [Fact]
    public void NoteWholeRepoScanCompleted_WhenTheLatchWasRearmedMidScan_LeavesTheBackoffToTheCallerThatScanned()
    {
        var clock = new TestClock();
        var policy = NewFailurePolicy(clock);
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock, failurePolicy: policy);
        core.SignalRescan();
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        Assert.False(core.WouldRunWholeRepoScan(headChanged: false));

        long generation = core.WholeRepoScanArmingGeneration;
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.NoteWholeRepoScanCompleted(ScanIntent.UserFullRebuild, armingGeneration: generation);

        Assert.Equal(1, core.ConsecutiveScanFailures);
        Assert.False(core.WouldRunWholeRepoScan(headChanged: false));

        policy.RecordSuccess(ScanIntent.UserFullRebuild);

        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));
    }

    [Fact]
    public void DrainAndProcess_RefusedAdmissionThenAdmitted_StillReconciles()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        core.DrainAndProcess(headChanged: true, wholeRepoScanAdmitted: false, out _);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.Equal(new[] { "scan" }, ops.Calls);
        Assert.False(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_PerFileOnlyBatch_RunsEvenWithoutScanAdmission()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));

        bool didWork = core.DrainAndProcess(
            headChanged: false, wholeRepoScanAdmitted: false, out bool usedWholeRepoScan);

        Assert.True(didWork);
        Assert.False(usedWholeRepoScan);
        Assert.Equal(new[] { "update:/repo/a.cs" }, ops.Calls);
    }

    [Fact]
    public void DrainAndProcess_FailedScan_LeavesTheLatchArmed()
    {
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true);
        core.SignalRescan();

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.False(usedWholeRepoScan);
        Assert.Equal(new[] { "scan" }, ops.Calls);
        Assert.True(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_HeadChangedTickWhoseScanFails_RescansOnTheNextTickAfterTheBackoff()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock);

        core.DrainAndProcess(headChanged: true, wholeRepoScanAdmitted: true, out _);
        ops.ThrowOnScan = null;
        clock.Advance(ScanFailurePolicy.FirstBackoff);

        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.Equal(new[] { "scan", "scan" }, ops.Calls);
        Assert.False(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_AfterAFailedScan_SuppressesTheRetryUntilTheBackoffElapses()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock);
        core.SignalRescan();

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.False(core.WouldRunWholeRepoScan(headChanged: false));
        Assert.False(core.WouldRunWholeRepoScan(headChanged: true));

        clock.Advance(ScanFailurePolicy.FirstBackoff - IndexerService.DebounceInterval);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        core.DrainAndProcess(headChanged: true, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { "scan" }, ops.Calls);
        Assert.True(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_AFailingScan_IsNotRespawnedOnEveryDebounceTick()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock);
        core.SignalRescan();

        int admissionsTaken = 0;
        for (int tick = 0; tick < 240; tick++)
        {
            if (core.WouldRunWholeRepoScan(headChanged: false))
            {
                admissionsTaken++;
                core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
            }

            clock.Advance(IndexerService.DebounceInterval);
        }

        Assert.True(admissionsTaken <= 7, $"took the machine-wide lease {admissionsTaken} times in 60s of ticks");
        Assert.Equal(admissionsTaken, ops.Calls.Count);
        Assert.True(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_BackoffGrowsWithEachConsecutiveFailure()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock);
        core.SignalRescan();

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);

        Assert.Equal(2, core.ConsecutiveScanFailures);
        Assert.False(core.WouldRunWholeRepoScan(headChanged: false));

        clock.Advance(ScanFailurePolicy.SecondBackoff - ScanFailurePolicy.FirstBackoff);

        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));
    }

    [Fact]
    public void DrainAndProcess_AfterASigkilledScan_ClampsTheNextAttemptToOneJob()
    {
        var clock = new TestClock();
        var ops = new RecordingOps
        {
            ThrowOnScan = new JulieExtractException("crashed", "", ScanFailurePolicy.SigkillExitCode),
        };
        var core = NewCore(ops, _ => true, clock: clock);
        core.SignalRescan();

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new int?[] { null, ScanFailurePolicy.PostSigkillJobs }, ops.ScanJobs);
    }

    [Fact]
    public void DrainAndProcess_AfterANonSigkillFailure_LeavesTheJobsCapToTheAmbientPolicy()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("exited 1", "", exitCode: 1) };
        var core = NewCore(ops, _ => true, clock: clock);
        core.SignalRescan();

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new int?[] { null, null }, ops.ScanJobs);
    }

    [Fact]
    public void DrainAndProcess_AUserFullRebuildRetry_DowngradesToADeltaAgainstAServableArtifact()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(
            ops, _ => true, clock: clock,
            failurePolicy: NewFailurePolicy(clock, priorArtifactUsable: static () => true));
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(
            new[] { ScanIntent.UserFullRebuild, ScanIntent.IncrementalReconcile }, ops.ScanIntents);
        Assert.True(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_AfterADowngradedSuccess_ScansOncePerBackoffWindowNotOncePerTick()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(
            ops, _ => true, clock: clock,
            failurePolicy: NewFailurePolicy(clock, priorArtifactUsable: static () => true));
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        for (int tick = 0; tick < 100; tick++)
        {
            clock.Advance(IndexerService.DebounceInterval);
            core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        }

        Assert.Equal(
            new[] { ScanIntent.UserFullRebuild, ScanIntent.IncrementalReconcile }, ops.ScanIntents);
        Assert.True(core.HasPendingWork);

        clock.Advance(ScanFailurePolicy.FirstBackoff);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(
            new[]
            {
                ScanIntent.UserFullRebuild,
                ScanIntent.IncrementalReconcile,
                ScanIntent.IncrementalReconcile,
            },
            ops.ScanIntents);
    }

    [Fact]
    public void DrainAndProcess_AUserFullRebuildRetry_NeverDowngradesWithoutAServableArtifact()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(
            ops, _ => true, clock: clock,
            failurePolicy: NewFailurePolicy(clock, priorArtifactUsable: static () => false));
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { ScanIntent.UserFullRebuild, ScanIntent.UserFullRebuild }, ops.ScanIntents);
        Assert.False(core.WouldRunWholeRepoScan(headChanged: false));
    }

    [Fact]
    public void DrainAndProcess_ADowngradedRetryThatSucceeds_KeepsTheBackoffAndThePendingRebuild()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(
            ops, _ => true, clock: clock,
            failurePolicy: NewFailurePolicy(clock, priorArtifactUsable: static () => true));
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.Equal(1, core.ConsecutiveScanFailures);
        Assert.True(core.HasPendingWork);
    }

    [Fact]
    public void RequestWholeRepoScan_AnExtractorUpgradeIsDischargedByACompletedUserFullRebuild()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.RequestWholeRepoScan(ScanIntent.ExtractorUpgrade);
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);

        core.NoteWholeRepoScanCompleted(ScanIntent.UserFullRebuild, core.WholeRepoScanArmingGeneration);

        Assert.False(core.WouldRunWholeRepoScan(headChanged: false));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Empty(ops.ScanIntents);
    }

    [Fact]
    public void RequestWholeRepoScan_AnExtractorUpgradeIsNotDischargedByADeltaReconcile()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.RequestWholeRepoScan(ScanIntent.ExtractorUpgrade);

        core.NoteWholeRepoScanCompleted(ScanIntent.IncrementalReconcile, core.WholeRepoScanArmingGeneration);

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { ScanIntent.ExtractorUpgrade }, ops.ScanIntents);
    }

    [Fact]
    public void RequestWholeRepoScan_ACorruptionHealIsNotDischargedByACompletedUserFullRebuild()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.RequestWholeRepoScan(ScanIntent.CorruptionHeal);
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);

        core.NoteWholeRepoScanCompleted(ScanIntent.UserFullRebuild, core.WholeRepoScanArmingGeneration);

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { ScanIntent.CorruptionHeal }, ops.ScanIntents);
    }

    [Fact]
    public void RequestWholeRepoScan_AHealIntentFoldedWithAUserRebuild_IsNeverRetriedDowngradable()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(
            ops, _ => true, clock: clock,
            failurePolicy: NewFailurePolicy(clock, priorArtifactUsable: static () => true));
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        core.RequestWholeRepoScan(ScanIntent.ExtractorUpgrade);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(
            new[] { ScanIntent.UserFullRebuild, ScanIntent.ExtractorUpgrade }, ops.ScanIntents);
    }

    [Fact]
    public void DrainAndProcess_DuringScanBackoff_StillRunsPerFileWork()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock);
        core.SignalRescan();
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));
        bool didWork = core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: false, out _);

        Assert.True(didWork);
        Assert.Equal(new[] { "scan", "update:/repo/a.cs" }, ops.Calls);
        Assert.True(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_ASuccessfulScanOfTheRecordedStrength_ResetsTheFailureBackoff()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock);
        core.SignalRescan();
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.Equal(0, core.ConsecutiveScanFailures);

        core.SignalRescan();

        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));
    }

    [Fact]
    public void DrainAndProcess_ADeltaThatSucceeds_LeavesAForcedScansFailureBackoffInPlace()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock);
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;
        core.NoteWholeRepoScanCompleted(ScanIntent.UserFullRebuild, core.WholeRepoScanArmingGeneration);
        core.SignalRescan();

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { ScanIntent.UserFullRebuild, ScanIntent.IncrementalReconcile }, ops.ScanIntents);
        Assert.Equal(1, core.ConsecutiveScanFailures);
    }

    [Fact]
    public void RequestWholeRepoScan_WithForce_RetriesAsAForcedScan()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.Equal(new[] { true }, ops.ScanForce);
    }

    [Fact]
    public void RequestWholeRepoScan_ForceSurvivesARefusedAdmissionAndAFailedAttempt()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock);

        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: false, out _);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        clock.Advance(ScanFailurePolicy.FirstBackoff);
        ops.ThrowOnScan = null;
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { true, true }, ops.ScanForce);
    }

    [Fact]
    public void RequestWholeRepoScan_AnUnforcedSignalNeverDowngradesAPendingForcedScan()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.SignalRescan();
        core.RequestWholeRepoScan(ScanIntent.IncrementalReconcile);
        core.DrainAndProcess(headChanged: true, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { true }, ops.ScanForce);
    }

    [Fact]
    public void RequestWholeRepoScan_ForceIsClearedOnceTheForcedScanSucceeds()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);
        core.SignalRescan();
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Equal(new[] { true, false }, ops.ScanForce);
    }

    [Fact]
    public void DrainAndProcess_SucceededScan_ReportsUsedWholeRepoScan_AndClearsTheLatch()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        core.DrainAndProcess(headChanged: true, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.False(core.HasPendingWork);
    }

    // IndexerService runs whole-repo scans of its own (startup delta, extractor upgrade, leader-requested full,
    // on-demand). Without a completion signal the latch that would have run them stays armed and the very next
    // tick rebuilds the same repo again — with the force bit, a duplicated from-scratch extract.

    [Fact]
    public void NoteWholeRepoScanCompleted_ClearsAnUnforcedLatch()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.RequestWholeRepoScan(ScanIntent.IncrementalReconcile);

        core.NoteWholeRepoScanCompleted(ScanIntent.IncrementalReconcile, core.WholeRepoScanArmingGeneration);

        Assert.False(core.WouldRunWholeRepoScan(headChanged: false));
        Assert.False(core.HasPendingWork);

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Empty(ops.Calls);
    }

    [Fact]
    public void NoteWholeRepoScanCompleted_WithForce_ClearsAPendingForcedLatch()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);

        core.NoteWholeRepoScanCompleted(ScanIntent.UserFullRebuild, core.WholeRepoScanArmingGeneration);

        Assert.False(core.WouldRunWholeRepoScan(headChanged: false));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        Assert.Empty(ops.Calls);
    }

    [Fact]
    public void NoteWholeRepoScanCompleted_WithoutForce_NeverClearsAPendingForcedLatch()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);

        core.NoteWholeRepoScanCompleted(ScanIntent.IncrementalReconcile, core.WholeRepoScanArmingGeneration);

        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.Equal(new[] { true }, ops.ScanForce);
    }

    [Fact]
    public void NoteWholeRepoScanCompleted_DischargesTheLatchWithoutTouchingTheFailureHistory()
    {
        var clock = new TestClock();
        var ops = new RecordingOps { ThrowOnScan = new JulieExtractException("crashed (exit 137)", "") };
        var core = NewCore(ops, _ => true, clock: clock);
        core.SignalRescan();
        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out _);

        core.NoteWholeRepoScanCompleted(ScanIntent.IncrementalReconcile, core.WholeRepoScanArmingGeneration);

        Assert.Equal(1, core.ConsecutiveScanFailures);
        Assert.False(core.HasPendingWork);
    }

    [Fact]
    public void NoteWholeRepoScanCompleted_WithTheGenerationCapturedBeforeTheScan_ClearsTheLatch()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        long generationAtScanStart = core.WholeRepoScanArmingGeneration;

        core.NoteWholeRepoScanCompleted(ScanIntent.UserFullRebuild, generationAtScanStart);

        Assert.False(core.HasPendingWork);
    }

    [Fact]
    public void NoteWholeRepoScanCompleted_WhenTheLatchWasRearmedAfterTheScanStarted_LeavesItArmed()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        long generationAtScanStart = core.WholeRepoScanArmingGeneration;

        core.RequestWholeRepoScan(ScanIntent.UserFullRebuild);
        core.NoteWholeRepoScanCompleted(ScanIntent.UserFullRebuild, generationAtScanStart);

        Assert.True(core.WouldRunWholeRepoScan(headChanged: false));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.True(usedWholeRepoScan);
        Assert.Equal(new[] { true }, ops.ScanForce);
    }

    [Fact]
    public void DrainAndProcess_PerFileOnlyBatch_DoesNotReportAWholeRepoScan()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false, wholeRepoScanAdmitted: true, out bool usedWholeRepoScan);

        Assert.False(usedWholeRepoScan);
    }
}
