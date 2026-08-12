using System.Globalization;
using System.Text.Json;
using Miller.Server.Logging;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Miller.Tests.Logging;

/// <summary>
/// Pins the Serilog sink wiring (<see cref="MillerLoggingSetup"/>) — the thin infra <c>Program.cs</c> and this
/// test share so the production sink layout is exercised, not a copy. A logger configured to a temp dir and
/// emitting one event must write BOTH a shared daily human <c>.log</c> and a machine <c>.jsonl</c>; the JSONL
/// line must parse as JSON and carry the per-call <c>cid</c> (pushed via <see cref="LogContext"/>), the
/// <c>pid</c> property, the live <c>role</c>, and a message template. The file name is the SHARED daily form
/// (<c>miller-&lt;YYYYMMDD&gt;.log</c>) — NO per-pid segment — because all processes append to one daily pair
/// (the per-pid scheme that piled up files was removed). Temp-dir file I/O (Server-layer) → default suite.
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
    public void Configure_EmittingOneEvent_WritesBothAHumanLogAndAJsonl_TheJsonlCarriesCidPidAndLevel()
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

        // BOTH sinks wrote a single dated roll, resolved by the shared "miller-" prefix (no pid in the name).
        string humanFile = SingleRollFor(_dir, ".log");
        string jsonFile = SingleRollFor(_dir, ".jsonl");

        // The shared daily file name carries the date but NOT the pid (the consolidation invariant).
        string humanName = Path.GetFileName(humanFile);
        Assert.Matches(@"^miller-\d{8}\.log$", humanName);
        Assert.DoesNotContain(pid.ToString(CultureInfo.InvariantCulture), humanName);

        // The human log is readable text carrying the message and the rendered cid.
        string humanText = ReadShared(humanFile);
        Assert.Contains("sink probe ran", humanText);
        Assert.Contains(cid, humanText);

        // The JSONL line parses as JSON and carries the cid property + the structural template (log-viewer contract).
        string jsonLine = ReadShared(jsonFile).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        using var doc = JsonDocument.Parse(jsonLine);
        JsonElement root = doc.RootElement;

        // CompactJsonFormatter renders custom scalar properties at the top level; cid is one of them.
        Assert.True(root.TryGetProperty("cid", out JsonElement cidEl), "the jsonl line must carry a cid property");
        Assert.Equal(cid, cidEl.GetString());
        Assert.True(root.TryGetProperty("@mt", out _), "the jsonl line must carry the @mt message template");
        Assert.True(root.TryGetProperty("@t", out _), "the jsonl line must carry the @t timestamp");
        // pid is enriched as a process-wide property and must be machine-readable (attribution without a file-per-pid).
        Assert.True(root.TryGetProperty("pid", out JsonElement pidEl), "the jsonl line must carry the pid");
        Assert.Equal(pid, pidEl.GetInt32());
    }

    [Fact]
    public void Configure_RoleProperty_IsRenderedInBothSinks_AndTracksLeaderReaderTransitions()
    {
        // role (leader/reader) is a log PROPERTY, not part of the path, because leadership is won later
        // (IndexerService). Both the human .log and the machine .jsonl must carry it, and it must reflect the live
        // role when IndexerService flips it on lease acquire / step-down.
        const int pid = 5151;
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Debug);

        var previousRole = MillerRole.Current; // restore after so this test does not leak into others
        var logger = MillerLoggingSetup
            .Configure(new LoggerConfiguration(), _dir, pid, levelSwitch)
            .CreateLogger();
        try
        {
            MillerRole.SetReader();
            logger.Information("first line as reader");
            MillerRole.SetLeader();
            logger.Information("now the leader");
        }
        finally
        {
            logger.Dispose();
            MillerRole.Set(previousRole);
        }

        string humanText = ReadShared(SingleRollFor(_dir, ".log"));
        Assert.Contains("reader", humanText);
        Assert.Contains("leader", humanText);

        string[] jsonLines = ReadShared(SingleRollFor(_dir, ".jsonl"))
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

        string jsonLine = ReadShared(SingleRollFor(_dir, ".jsonl")).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        using var doc = JsonDocument.Parse(jsonLine); // must not throw — the line is well-formed
        Assert.True(doc.RootElement.TryGetProperty("@mt", out _));
        // No cid property is fine; the human template renders it as empty (asserted via the readable log).
        Assert.Contains("background tick", ReadShared(SingleRollFor(_dir, ".log")));
    }

    [Fact]
    public void LogFilePaths_BuildsTheSharedBaseNames_WithNoPidSegment()
    {
        var paths = MillerLoggingSetup.LogFilePaths(_dir);
        Assert.Equal("miller-.log", Path.GetFileName(paths.HumanLog));
        Assert.Equal("miller-.jsonl", Path.GetFileName(paths.JsonLog));
    }

    [Fact]
    public void SharedFileSink_WithSizeRolling_RollsWithinTheDayInsteadOfGrowingWithoutBound()
    {
        string basePath = Path.Combine(_dir, "sized-.log");
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                basePath,
                rollingInterval: RollingInterval.Day,
                shared: true,
                retainedFileCountLimit: MillerLoggingSetup.RetainedFileCountLimit,
                fileSizeLimitBytes: 2048,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Message:l}{NewLine}")
            .CreateLogger();
        try
        {
            for (int i = 0; i < 200; i++)
                logger.Information(new string('x', 200));
        }
        finally
        {
            logger.Dispose();
        }

        var rolls = Directory.EnumerateFiles(_dir, "sized-*.log").ToList();
        Assert.True(rolls.Count > 1, $"size rolling produced {rolls.Count} file(s); expected more than one");
        Assert.All(rolls, path => Assert.True(
            new FileInfo(path).Length < 32 * 1024,
            $"{Path.GetFileName(path)} grew past the size limit"));
    }

    [Fact]
    public void FileSizeLimit_IsBoundedSoOneBusyDayCannotFillTheDisk()
    {
        Assert.True(MillerLoggingSetup.FileSizeLimitBytes > 0);
        Assert.True(MillerLoggingSetup.FileSizeLimitBytes <= 128L * 1024 * 1024);
        Assert.True(MillerLoggingSetup.RetainedFileCountLimit > 0);
    }

    // --- helpers ---

    // The single dated roll Serilog wrote for an extension — resolved by the shared "miller-" prefix (the date is
    // inserted before the extension, e.g. miller-20260531.log). Asserts exactly one matched so a stray per-pid
    // regression (two files) would fail loudly here.
    private static string SingleRollFor(string dir, string ext)
    {
        var matches = Directory.EnumerateFiles(dir)
            .Where(p => Path.GetFileName(p).StartsWith("miller-", StringComparison.Ordinal)
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
