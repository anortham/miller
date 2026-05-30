using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Miller.Server.Logging;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Miller.Tests.Logging;

/// <summary>
/// Pins the Serilog sink wiring (<see cref="MillerLoggingSetup"/>, m8-design §D1/§D5/§D6) — the thin infra
/// <c>Program.cs</c> and this test share so the production sink layout is exercised, not a copy. A logger
/// configured to a temp dir and emitting one event must write BOTH a per-pid human <c>.log</c> and a machine
/// <c>.jsonl</c>; the JSONL line must parse as JSON and carry the per-call <c>cid</c> (pushed via
/// <see cref="LogContext"/>) plus a level; the per-pid file name must contain the supplied pid. The D6 startup
/// sweep must delete stale non-current pids' files while never touching the current pid's.
/// Temp-dir file I/O (Server-layer), mirroring the TelemetryLedger tests → default suite.
/// </summary>
public sealed class MillerLoggingSetupTests : IDisposable
{
    private readonly string _dir;

    public MillerLoggingSetupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-logsetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Configure_EmittingOneEvent_WritesBothAHumanLogAndAJsonl_TheJsonlCarriesCidAndLevel()
    {
        const int pid = 4242;
        const string cid = "0193abcd-aaaa-7bbb-8ccc-deadbeef0001";
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Debug);

        // Build the REAL production sink config against the temp dir, emit one event carrying a pushed cid, then
        // dispose the logger so the file sinks flush + release their handles before we read the files back.
        var logger = MillerLoggingSetup
            .Configure(new LoggerConfiguration(), _dir, pid, levelSwitch)
            .CreateLogger();
        try
        {
            using (LogContext.PushProperty("cid", cid))
                logger.Information("sink probe ran for {Thing}", "x");
        }
        finally
        {
            logger.Dispose();
        }

        var paths = MillerLoggingSetup.LogFilePaths(_dir, pid);

        // The per-pid file names must embed the pid (D1) so concurrent processes never share a file.
        Assert.Contains(pid.ToString(CultureInfo.InvariantCulture), Path.GetFileName(paths.HumanLog));
        Assert.Contains(pid.ToString(CultureInfo.InvariantCulture), Path.GetFileName(paths.JsonLog));

        // BOTH sinks wrote a dated roll: resolve them by the pid prefix (Serilog inserts YYYYMMDD before the ext).
        string humanFile = SingleRollFor(_dir, pid, ".log");
        string jsonFile = SingleRollFor(_dir, pid, ".jsonl");

        // The human log is readable text carrying the message and the rendered cid.
        string humanText = ReadShared(humanFile);
        Assert.Contains("sink probe ran", humanText);
        Assert.Contains(cid, humanText);

        // The JSONL line parses as JSON and carries the cid property + a level (the M9 log-viewer contract).
        string jsonLine = ReadShared(jsonFile).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        using var doc = JsonDocument.Parse(jsonLine);
        JsonElement root = doc.RootElement;

        // CompactJsonFormatter renders custom scalar properties at the top level; cid is one of them.
        Assert.True(root.TryGetProperty("cid", out JsonElement cidEl), "the jsonl line must carry a cid property");
        Assert.Equal(cid, cidEl.GetString());
        // The compact format stores the level under "@l" (Information is omitted as the default, so emit Debug
        // is not relied on here — instead assert the structural message template token "@mt" is present).
        Assert.True(root.TryGetProperty("@mt", out _), "the jsonl line must carry the @mt message template");
        Assert.True(root.TryGetProperty("@t", out _), "the jsonl line must carry the @t timestamp");
        // pid was enriched as a process-wide property and must be machine-readable too.
        Assert.True(root.TryGetProperty("pid", out JsonElement pidEl), "the jsonl line must carry the pid");
        Assert.Equal(pid, pidEl.GetInt32());
    }

    [Fact]
    public void Configure_RoleProperty_IsRenderedInBothSinks_AndTracksLeaderReaderTransitions()
    {
        // finding-2/-3 (m8-design §D2): role (leader/reader) is a log PROPERTY, not part of the path, because
        // leadership is won later (IndexerService). Both the human .log and the machine .jsonl must carry it, and
        // it must reflect the live role when IndexerService flips it on lease acquire / step-down.
        const int pid = 5151;
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Debug);

        var previousRole = MillerRole.Current; // restore after so this test does not leak into others
        var logger = MillerLoggingSetup
            .Configure(new LoggerConfiguration(), _dir, pid, levelSwitch)
            .CreateLogger();
        try
        {
            // Default before leadership is resolved: a reader.
            MillerRole.SetReader();
            logger.Information("first line as reader");
            // The leader-won transition flips the live role; subsequent lines must carry role=leader.
            MillerRole.SetLeader();
            logger.Information("now the leader");
        }
        finally
        {
            logger.Dispose();
            MillerRole.Set(previousRole);
        }

        // Human .log carries both rendered roles.
        string humanText = ReadShared(SingleRollFor(_dir, pid, ".log"));
        Assert.Contains("reader", humanText);
        Assert.Contains("leader", humanText);

        // The .jsonl carries role as a machine-readable property; the two lines carry reader then leader.
        string[] jsonLines = ReadShared(SingleRollFor(_dir, pid, ".jsonl"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, jsonLines.Length);

        using var first = JsonDocument.Parse(jsonLines[0]);
        Assert.True(first.RootElement.TryGetProperty("role", out JsonElement firstRole),
            "the jsonl line must carry a role property");
        Assert.Equal("reader", firstRole.GetString());

        using var second = JsonDocument.Parse(jsonLines[1]);
        Assert.True(second.RootElement.TryGetProperty("role", out JsonElement secondRole));
        Assert.Equal("leader", secondRole.GetString());
    }

    [Fact]
    public void Configure_BackgroundLog_WithNoCid_StillRendersCleanly()
    {
        const int pid = 909;
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        var logger = MillerLoggingSetup
            .Configure(new LoggerConfiguration(), _dir, pid, levelSwitch)
            .CreateLogger();
        try
        {
            // No LogContext cid pushed — a hosted-service/background log. It must still write a valid JSON line.
            logger.Information("background tick");
        }
        finally
        {
            logger.Dispose();
        }

        string jsonFile = SingleRollFor(_dir, pid, ".jsonl");
        string jsonLine = ReadShared(jsonFile).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        using var doc = JsonDocument.Parse(jsonLine); // must not throw — the line is well-formed
        Assert.True(doc.RootElement.TryGetProperty("@mt", out _));
        // No cid property is fine; the human template renders it as empty (asserted via the readable log).
        string humanFile = SingleRollFor(_dir, pid, ".log");
        Assert.Contains("background tick", ReadShared(humanFile));
    }

    [Fact]
    public void ReapStaleLogs_DeletesOlderNonCurrentPids_ButNeverTheCurrentPid()
    {
        const int currentPid = 1000;
        // ReapKeepPids non-current pids are retained; this many older pids beyond that must be deleted.
        int olderPidCount = MillerLoggingSetup.ReapKeepPids + 3;

        // The current pid's live files (must survive).
        string currentLog = TouchLogFile(currentPid, ".log", DateTime.UtcNow);
        string currentJson = TouchLogFile(currentPid, ".jsonl", DateTime.UtcNow);

        // The newest ReapKeepPids non-current pids (must survive) — give them recent mtimes.
        var survivors = new List<string>();
        for (int i = 0; i < MillerLoggingSetup.ReapKeepPids; i++)
        {
            int pid = 2000 + i;
            survivors.Add(TouchLogFile(pid, ".log", DateTime.UtcNow.AddMinutes(-i)));
            survivors.Add(TouchLogFile(pid, ".jsonl", DateTime.UtcNow.AddMinutes(-i)));
        }

        // Older non-current pids beyond the keep window (must be deleted) — older mtimes.
        var doomed = new List<string>();
        for (int i = 0; i < (olderPidCount - MillerLoggingSetup.ReapKeepPids); i++)
        {
            int pid = 3000 + i;
            doomed.Add(TouchLogFile(pid, ".log", DateTime.UtcNow.AddDays(-10 - i)));
            doomed.Add(TouchLogFile(pid, ".jsonl", DateTime.UtcNow.AddDays(-10 - i)));
        }

        MillerLoggingSetup.ReapStaleLogs(_dir, currentPid);

        Assert.True(File.Exists(currentLog), "the current pid's .log must never be deleted");
        Assert.True(File.Exists(currentJson), "the current pid's .jsonl must never be deleted");
        foreach (string s in survivors)
            Assert.True(File.Exists(s), $"a kept newest pid's file was wrongly deleted: {s}");
        foreach (string d in doomed)
            Assert.False(File.Exists(d), $"a stale older pid's file should have been deleted: {d}");
    }

    [Fact]
    public void ReapStaleLogs_NeverDeletesAStillRunningPeersFiles_EvenWhenOldAndOutOfTheKeepWindow()
    {
        // finding-1/-8: in M3's multi-process reality a still-running PEER owns an open log file. An idle reader
        // writes almost nothing after its banner, so its mtime freezes near start; if more short-lived pids wrote
        // afterward it falls out of the newest-keep window — and on macOS/Linux File.Delete would silently UNLINK
        // its open file. The sweep must probe liveness and never target a running process. The CURRENT test
        // process is a guaranteed-live "peer" (its pid is real and running) whose log we make the OLDEST on disk.
        int livePeerPid = Environment.ProcessId;
        const int currentPid = 999_999; // a distinct (almost certainly dead) "this process" for the sweep
        string livePeerLog = TouchLogFile(livePeerPid, ".log", DateTime.UtcNow.AddDays(-30)); // oldest of all
        string livePeerJson = TouchLogFile(livePeerPid, ".jsonl", DateTime.UtcNow.AddDays(-30));

        // Fill the keep window AND beyond with newer, dead pids so the live peer is well outside the newest-keep
        // set by recency alone (it would be reaped if liveness were ignored).
        for (int i = 0; i < MillerLoggingSetup.ReapKeepPids + 2; i++)
        {
            int pid = 4000 + i; // dead pids, all newer than the live peer
            TouchLogFile(pid, ".log", DateTime.UtcNow.AddMinutes(-i));
            TouchLogFile(pid, ".jsonl", DateTime.UtcNow.AddMinutes(-i));
        }

        MillerLoggingSetup.ReapStaleLogs(_dir, currentPid);

        Assert.True(File.Exists(livePeerLog), "a still-running peer's .log must never be unlinked");
        Assert.True(File.Exists(livePeerJson), "a still-running peer's .jsonl must never be unlinked");
    }

    [Fact]
    public void ReapStaleLogs_DeletesThePreM8LegacyDateOnlySharedFiles()
    {
        // finding-4: the pre-M8 single shared file is named miller-<YYYYMMDD>.log (no per-pid segment). The per-pid
        // parser skips it (no second '-'), so it would never be swept and would orphan forever on every upgraded
        // workspace, defeating D6's "don't accumulate forever". The sweep must one-time delete the legacy form.
        string legacyLog = Path.Combine(_dir, "miller-20260529.log");
        string legacyJson = Path.Combine(_dir, "miller-20260529.jsonl");
        File.WriteAllText(legacyLog, "old shared log\n");
        File.WriteAllText(legacyJson, "{}\n");
        // A current per-pid file that must survive (proves the legacy sweep does not touch the new scheme).
        string keepLog = TouchLogFile(4242, ".log", DateTime.UtcNow);

        MillerLoggingSetup.ReapStaleLogs(_dir, currentPid: 4242);

        Assert.False(File.Exists(legacyLog), "the pre-M8 legacy miller-<date>.log must be reaped");
        Assert.False(File.Exists(legacyJson), "the pre-M8 legacy miller-<date>.jsonl must be reaped");
        Assert.True(File.Exists(keepLog), "the current per-pid file must never be touched by the legacy sweep");
    }

    [Fact]
    public void ReapStaleLogs_MissingDirectory_IsASilentNoOp()
    {
        string missing = Path.Combine(_dir, "does-not-exist");
        // Must not throw even though the directory is absent, and there is nothing to defer-report.
        string? deferred = null;
        var ex = Record.Exception(() => deferred = MillerLoggingSetup.ReapStaleLogs(missing, currentPid: 1));
        Assert.Null(ex);
        Assert.Null(deferred);
    }

    [Fact]
    public void ReapStaleLogs_OnAnUnreadableLogsDir_DoesNotThrow_AndReturnsADeferredMessage()
    {
        // finding-6: Program.cs calls ReapStaleLogs BEFORE the logger exists, under a "never fails startup"
        // contract. The discovery step (Directory.EnumerateFiles) can throw on a dir that exists but is
        // unreadable (permissions, a stale mount). That throw must be caught — not propagated out of startup —
        // and surfaced as a DEFERRED message the caller logs once the logger is built (mirroring the
        // unknown-MILLER_LOG_LEVEL deferral). chmod 000 makes the dir unenumerable on macOS/Linux.
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "POSIX chmod permissions are required for this case.");
        // SkipUnless already exits on Windows, but CA1416's flow analysis can't see that — the explicit
        // guard below tells the analyzer the POSIX-only File.SetUnixFileMode calls never run on Windows.
        if (OperatingSystem.IsWindows())
            return;

        string hostile = Path.Combine(_dir, "hostile");
        Directory.CreateDirectory(hostile);
        // Seed a file so an enumeration that DID succeed would have work — then make the dir unreadable.
        File.WriteAllText(Path.Combine(hostile, "miller-7-.log"), "x");
        File.SetUnixFileMode(hostile, UnixFileMode.None);

        try
        {
            string? deferred = null;
            var ex = Record.Exception(() => deferred = MillerLoggingSetup.ReapStaleLogs(hostile, currentPid: 1));

            Assert.Null(ex); // never throws out of startup
            Assert.False(string.IsNullOrEmpty(deferred), "a hostile-dir reap must return a deferred message");
            Assert.Contains(hostile, deferred!, StringComparison.Ordinal);
        }
        finally
        {
            // Restore permissions so the temp-dir cleanup in Dispose can remove it.
            File.SetUnixFileMode(hostile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void EnumerateExisting_ParsesPidFromName_AndSkipsUnrelatedFiles()
    {
        TouchLogFile(77, ".log", DateTime.UtcNow);
        TouchLogFile(77, ".jsonl", DateTime.UtcNow);
        // Unrelated files: a non-miller name and a miller name with a non-numeric pid segment.
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "miller-abc-.log"), "x");

        var infos = MillerLoggingSetup.EnumerateExisting(_dir);

        Assert.Equal(2, infos.Count); // only the two valid miller-77-* files
        Assert.All(infos, i => Assert.Equal(77, i.Pid));
    }

    [Theory]
    [InlineData("miller-4242-.log", true, 4242)]
    [InlineData("miller-4242-20260530.jsonl", true, 4242)]
    [InlineData("miller-1-.log", true, 1)]
    [InlineData("notes.txt", false, 0)]
    [InlineData("miller-abc-.log", false, 0)]
    [InlineData("miller-.log", false, 0)]
    public void TryParsePid_HandlesValidAndInvalidNames(string fileName, bool expectedOk, int expectedPid)
    {
        bool ok = MillerLoggingSetup.TryParsePid(fileName, out int pid);
        Assert.Equal(expectedOk, ok);
        if (expectedOk)
            Assert.Equal(expectedPid, pid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4242)]
    [InlineData(int.MaxValue)]
    public void NameBuiltByLogFilePaths_RoundTripsBackThroughTryParsePid_AfterSerilogInsertsTheDate(int pid)
    {
        // finding-5: the WRITE pattern (LogFilePaths builds miller-<pid>- + ext) and the READ pattern (TryParsePid
        // recovers the pid) share the "pid between the first and second dash" contract as DUPLICATED logic. Pin a
        // round-trip so a future stem change (e.g. a role token in the name) cannot silently desync write/read and
        // turn the reaper into a no-op. Serilog inserts YYYYMMDD before the extension when it daily-rolls, so we
        // simulate that here to exercise the EXACT on-disk shape, not just the pre-roll stem.
        var paths = MillerLoggingSetup.LogFilePaths("/logs", pid);

        // The pre-roll stem (no date yet) must already round-trip.
        Assert.True(MillerLoggingSetup.TryParsePid(Path.GetFileName(paths.HumanLog), out int prePid));
        Assert.Equal(pid, prePid);

        // The dated roll Serilog actually writes: insert YYYYMMDD before the extension of each per-pid path.
        foreach (string rolled in new[] { RolledName(paths.HumanLog), RolledName(paths.JsonLog) })
        {
            Assert.True(MillerLoggingSetup.TryParsePid(rolled, out int rolledPid),
                $"the dated roll '{rolled}' must parse back to a pid");
            Assert.Equal(pid, rolledPid);
        }
    }

    // Reproduce Serilog's daily-roll naming: insert a YYYYMMDD date immediately before the extension, exactly
    // where the trailing '-' slot in the stem sits (miller-<pid>-.log -> miller-<pid>-20260530.log).
    private static string RolledName(string perPidPath)
    {
        string name = Path.GetFileName(perPidPath);
        string ext = Path.GetExtension(name);
        return name[..^ext.Length] + "20260530" + ext;
    }

    // --- helpers ---

    // Create a per-pid log file with a forced last-write time so the reap recency ranking is deterministic.
    private string TouchLogFile(int pid, string ext, DateTime lastWriteUtc)
    {
        var paths = MillerLoggingSetup.LogFilePaths(_dir, pid);
        string path = ext == ".log" ? paths.HumanLog : paths.JsonLog;
        File.WriteAllText(path, "stub\n");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    // The single dated roll Serilog wrote for a pid+extension (the date is inserted before the extension).
    private static string SingleRollFor(string dir, int pid, string ext)
    {
        string prefix = "miller-" + pid.ToString(CultureInfo.InvariantCulture) + "-";
        var matches = Directory.EnumerateFiles(dir)
            .Where(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.Ordinal)
                && Path.GetExtension(p).Equals(ext, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(matches);
        return matches[0];
    }

    // Read a file Serilog may still hold a (shared) handle on — open with full FileShare so the read never races
    // a not-yet-released sink handle.
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
