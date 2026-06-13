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

        public ExtractReport Scan(bool force = false)
        {
            Calls.Add(force ? "scan:force" : "scan");
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

    private static IndexerCore NewCore(RecordingOps ops, Func<string, bool> exists, ILogger? logger = null) =>
        new(new WatchEventQueue(), ops, exists, logger);

    [Fact]
    public void DrainAndProcess_EmptyQueueNoFlag_DoesNothing()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        bool didWork = core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

        Assert.Equal(new[] { "scan" }, ops.Calls);
        Assert.False(core.HasPendingWork);
    }

    [Fact]
    public void DrainAndProcess_CreatedAndModified_ExistingFiles_BecomeUpdates()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Created));
        core.Queue.Enqueue(new WatchEvent("/repo/b.cs", WatchEventKind.Modified));

        bool didWork = core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

        Assert.Equal(new[] { "delete:/repo/gone.cs" }, ops.Calls);
    }

    [Fact]
    public void DrainAndProcess_CreatedButVanished_RoutesToDelete()
    {
        var ops = new RecordingOps();
        // exists==false for the created path: the create/modify raced a removal -> delete (router rule).
        var core = NewCore(ops, _ => false);
        core.Queue.Enqueue(new WatchEvent("/repo/flicker.cs", WatchEventKind.Created));

        core.DrainAndProcess(headChanged: false);

        Assert.Equal(new[] { "delete:/repo/flicker.cs" }, ops.Calls);
    }

    [Fact]
    public void DrainAndProcess_Rename_BecomesDeleteOldThenUpdateNew()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, path => path == "/repo/new.cs"); // only the destination exists
        core.Queue.Enqueue(WatchEvent.Renamed("/repo/old.cs", "/repo/new.cs"));

        core.DrainAndProcess(headChanged: false);

        Assert.Equal(new[] { "delete:/repo/old.cs", "update:/repo/new.cs" }, ops.Calls);
    }

    [Fact]
    public void DrainAndProcess_HeadChanged_ForcesSingleScan_AndDropsPerFileEvents()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);
        core.Queue.Enqueue(new WatchEvent("/repo/a.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/b.cs", WatchEventKind.Modified));

        bool didWork = core.DrainAndProcess(headChanged: true);

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

        core.DrainAndProcess(headChanged: false);

        Assert.Equal(new[] { "scan" }, ops.Calls);
        // The core must clear NeedsRescan after scheduling the scan (so the next drain does not re-scan).
        Assert.False(core.Queue.NeedsRescan);
    }

    [Fact]
    public void DrainAndProcess_HeadChangedButEmptyQueue_StillScans()
    {
        var ops = new RecordingOps();
        var core = NewCore(ops, _ => true);

        bool didWork = core.DrainAndProcess(headChanged: true);

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
        bool didWork = core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

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

        core.DrainAndProcess(headChanged: false);

        var debug = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
        Assert.Contains("/repo/a.cs", debug.Message, StringComparison.Ordinal);
    }
}
