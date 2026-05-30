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
            Status: status, Operation: "test", DbPath: "x", Root: null, SchemaVersion: 26,
            SchemaState: "current", ExtractContractVersion: 1, AnalysisState: null,
            FilesScanned: 0, SymbolsExtracted: 0, FilesTotal: 0, SymbolsTotal: 0,
            RelationshipsTotal: 0, IdentifiersTotal: 0, TypesTotal: 0, Errors: System.Array.Empty<ExtractError>(),
            Revision: 2, FilesUpdated: 1, FilesDeleted: 0);
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

    private static JulieExtractFailedException Failed(params string[] codes)
    {
        var errors = codes
            .Select(c => new ExtractError(Code: c, Message: $"{c} happened", Path: "/repo/x.cs"))
            .ToArray();
        return new JulieExtractFailedException($"exit 1: {string.Join(",", codes)}", errors, standardError: "");
    }

    [Theory]
    [InlineData("data_loss_guard")]
    [InlineData("flock_timeout")]
    public void ExecuteIsolated_TransientFailure_LogsAtInformation_AndContinuesTheBatch(string transientCode)
    {
        // decision-10: a data-loss guard (empty re-parse) or a flock timeout is EXPECTED/transient — keep the
        // prior index, log at Info (not Error/Warning), and the next scan reconciles. Must not abort siblings.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = Failed(transientCode);
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/good1.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));
        core.Queue.Enqueue(new WatchEvent("/repo/good2.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false);

        // All three ops ran (the failure was isolated, the batch continued).
        Assert.Equal(
            new[] { "update:/repo/good1.cs", "update:/repo/bad.cs", "update:/repo/good2.cs" }, ops.Calls);
        // Exactly one log entry, at Information, naming the transient code.
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(transientCode, entry.Message, StringComparison.Ordinal);
        Assert.IsType<JulieExtractFailedException>(entry.Exception);
    }

    [Theory]
    [InlineData("outside_root")]
    [InlineData("usage")]
    [InlineData("not_extract_root")]
    public void ExecuteIsolated_AbnormalFailure_LogsAtError(string abnormalCode)
    {
        // decision-10: usage / outside-root / operator errors must surface loudly (Error level), NOT be hidden
        // as a transient retry-later.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = Failed(abnormalCode);
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
        // A failed report with an empty errors array is NOT a known transient case → treat it as abnormal
        // (surface loudly). Silently downgrading the no-code case to Info would hide real failures.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = Failed();
        var logger = new RecordingLogger();
        var core = NewCore(ops, _ => true, logger);
        core.Queue.Enqueue(new WatchEvent("/repo/bad.cs", WatchEventKind.Modified));

        core.DrainAndProcess(headChanged: false);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public void ExecuteIsolated_MixedTransientAndAbnormalCodes_TreatedAsTransient_IfAnyCodeIsTransient()
    {
        // If the failure carries a transient code alongside others, the recoverable path wins (keep-prior,
        // retry later) — the next scan reconciles regardless, and we should not escalate a recoverable case.
        var ops = new RecordingOps();
        ops.ThrowExceptionOnUpdatePath["/repo/bad.cs"] = Failed("flock_timeout", "some_other_code");
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
}
