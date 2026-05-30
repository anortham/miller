using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Formatting.Compact;

namespace Miller.Server.Logging;

/// <summary>
/// The thin infra that wires Miller's Serilog sinks for the multi-process reality M3 introduced (one leader + N
/// reader processes sharing <c>&lt;root&gt;/.miller/logs</c>). It is the ONE place the sink layout lives so both
/// <c>Program.cs</c> and the sink-config test build the SAME configuration (m8-design §D1/§D4/§D5/§D6):
///
/// <list type="bullet">
/// <item><b>D1 per-process files.</b> Each process writes its OWN <c>miller-&lt;pid&gt;-.log</c> (human) so there
/// is no cross-process file contention; Serilog appends the date and daily-rolls. Role (leader/reader) is a log
/// PROPERTY (the live <see cref="MillerRole"/> enricher), not part of the path — leadership is won later, in
/// <c>IndexerService</c>, which flips <see cref="MillerRole"/> on the lease-won / step-down transitions.</item>
/// <item><b>D4 level switch.</b> A <see cref="LoggingLevelSwitch"/> initialised from <c>MILLER_LOG_LEVEL</c> (via
/// the pure <see cref="LogLevelParse"/>) controls the minimum level — an operator dials verbosity at startup with
/// no recompile.</item>
/// <item><b>D5 machine sink.</b> A SECOND sink writes compact JSON lines to <c>miller-&lt;pid&gt;-.jsonl</c> (the
/// M9 log-viewer's first input). The human <c>.log</c> stays for eyeballs.</item>
/// </list>
///
/// <para><b>STDIO purity.</b> The console sink is routed to <b>stderr</b> for every level so nothing but the MCP
/// protocol ever touches stdout. The <c>cid</c> (per-call correlation id, pushed via <c>LogContext</c> by the
/// telemetry filter), the constant <c>pid</c>, and the live <c>role</c> (<see cref="MillerRole"/>) are enriched
/// onto every event and rendered by both the human <c>.log</c> and the machine <c>.jsonl</c>; a background log
/// with no <c>cid</c> still renders cleanly (the field is simply empty).</para>
/// </summary>
public static class MillerLoggingSetup
{
    /// <summary>How many dated rolls to retain per per-pid file (matches the prior single-file retention).</summary>
    public const int RetainedFileCountLimit = 14;

    /// <summary>How many non-current pids' log files the startup sweep keeps (m8-design §D6).</summary>
    public const int ReapKeepPids = 5;

    // The human template: timestamp, level, role + pid + cid (cid empty for background logs), source context,
    // message, then the exception. role (leader/reader) distinguishes processes in the shared logs dir (D2); cid
    // is empty whenever no per-call correlation id is present (a background log).
    private const string HumanOutputTemplate =
        "{Timestamp:HH:mm:ss.fff} [{Level:u3}] (role:{role} pid:{pid} cid:{cid}) {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Apply Miller's full sink layout to <paramref name="config"/>: the stderr console (D1 STDIO purity), the
    /// per-pid human <c>.log</c> and machine <c>.jsonl</c> file sinks (D1/D5), the <see cref="LoggingLevelSwitch"/>
    /// (D4), and the <c>cid</c>/<c>pid</c>/<c>role</c> enrichment both templates render (D2). The caller adds
    /// nothing else.
    /// </summary>
    /// <param name="config">The configuration to mutate (typically a fresh <see cref="LoggerConfiguration"/>).</param>
    /// <param name="logsDir">The <c>&lt;root&gt;/.miller/logs</c> directory (must already exist).</param>
    /// <param name="pid">This process's id — both the file-name discriminant and the constant <c>pid</c> property.</param>
    /// <param name="levelSwitch">The level switch controlling the minimum level (initialised from the env by the caller).</param>
    /// <returns>The same <paramref name="config"/> for fluent chaining.</returns>
    public static LoggerConfiguration Configure(
        LoggerConfiguration config, string logsDir, int pid, LoggingLevelSwitch levelSwitch)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDir);
        ArgumentNullException.ThrowIfNull(levelSwitch);

        var paths = LogFilePaths(logsDir, pid);

        return config
            .MinimumLevel.ControlledBy(levelSwitch)
            // pid is a process constant; cid arrives per-call via LogContext (the telemetry filter pushes it);
            // role is the LIVE leader/reader value (D2) re-read per event so a leadership transition is reflected.
            .Enrich.FromLogContext()
            .Enrich.WithProperty("pid", pid)
            .Enrich.With(MillerRole.Enricher)
            .WriteTo.Console(
                standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose,
                outputTemplate: HumanOutputTemplate)
            .WriteTo.File(
                paths.HumanLog,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                outputTemplate: HumanOutputTemplate)
            .WriteTo.File(
                new CompactJsonFormatter(),
                paths.JsonLog,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit);
    }

    /// <summary>
    /// The per-pid file paths for <paramref name="pid"/> under <paramref name="logsDir"/>: the human
    /// <c>miller-&lt;pid&gt;-.log</c> and the machine <c>miller-&lt;pid&gt;-.jsonl</c> (Serilog appends the date
    /// before the extension when it daily-rolls). The trailing <c>-</c> before the extension is the slot Serilog
    /// fills with <c>YYYYMMDD</c>.
    /// </summary>
    public static (string HumanLog, string JsonLog) LogFilePaths(string logsDir, int pid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDir);
        string stem = Prefix + pid.ToString(CultureInfo.InvariantCulture) + "-";
        return (
            Path.Combine(logsDir, stem + ".log"),
            Path.Combine(logsDir, stem + ".jsonl"));
    }

    // The common file-name prefix of every Miller per-pid log file.
    private const string Prefix = "miller-";

    /// <summary>
    /// Sweep stale log files at startup (m8-design §D6). Enumerate the existing <c>miller-&lt;pid&gt;-*.log/.jsonl</c>
    /// in <paramref name="logsDir"/>, plan the deletions with the pure <see cref="LogFileReaper.Plan"/> (keep the
    /// newest <see cref="ReapKeepPids"/> non-current DEAD pids; never the current pid OR a still-running peer —
    /// finding-1/-8), also remove any PRE-M8 legacy <c>miller-&lt;date&gt;.log/.jsonl</c> shared files (finding-4),
    /// and delete the resulting set best-effort.
    ///
    /// <para><b>Never fails startup (finding-6).</b> <c>Program.cs</c> calls this BEFORE the logger exists, so the
    /// whole discovery + plan body is guarded: a hostile logs dir (exists but unreadable) cannot throw out of
    /// startup. Because no logger is available yet, a discovery/plan fault is returned as a DEFERRED message the
    /// caller logs ONCE after <c>Log.Logger</c> is built (mirroring the unknown-<c>MILLER_LOG_LEVEL</c> deferral);
    /// a per-file delete fault is logged at Debug (when a logger is supplied) and skipped. When
    /// <paramref name="logsDir"/> does not exist the sweep is a silent no-op.</para>
    /// </summary>
    /// <param name="logsDir">The logs directory to sweep.</param>
    /// <param name="currentPid">This process's pid — its live files are never deleted.</param>
    /// <param name="logger">An optional logger for the per-file delete-failure Debug lines.</param>
    /// <returns>
    /// A deferred warning message describing a discovery/plan fault (for the caller to log after the logger is
    /// built), or <c>null</c> when the sweep planned cleanly (whether or not it found anything to delete).
    /// </returns>
    public static string? ReapStaleLogs(
        string logsDir, int currentPid, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDir);
        if (!Directory.Exists(logsDir))
            return null;

        List<string> toDelete;
        try
        {
            IReadOnlyList<LogFileInfo> discovered = EnumerateExisting(logsDir);

            // finding-1/-8: probe which OTHER pids are still running so the plan never targets a live peer's open
            // file (on macOS/Linux File.Delete unlinks an open file silently — the never-current guard alone does
            // not protect a running sibling, which is the entire multi-process point). Liveness is computed here
            // (infra) and passed into the pure planner, keeping LogFileReaper.Plan I/O-free.
            IReadOnlySet<int> livePids = ProbeLivePids(discovered, currentPid);
            toDelete = new List<string>(
                LogFileReaper.Plan(discovered, keep: ReapKeepPids, currentPid: currentPid, livePids: livePids));

            // finding-4: the pre-M8 single shared file (miller-<date>.log/.jsonl) is not in the per-pid scheme, so
            // the planner never sees it; sweep it here on first M8 startup so it does not orphan forever (D6).
            toDelete.AddRange(EnumerateLegacyShared(logsDir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // finding-6: discovery/plan failed on a hostile dir (unreadable, stale mount). The logger does not
            // exist yet (Program.cs runs this before building it), so we cannot log — return a deferred message
            // for the caller to log once Log.Logger is up, and NEVER let the fault escape and fail startup.
            return $"could not sweep stale log files in '{logsDir}': {ex.Message}";
        }

        foreach (string path in toDelete)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort hygiene: a file we could not remove (held, vanished, no permission) is left for the
                // next sweep. Never fail startup over a stale log file.
                logger?.LogDebug(ex, "Could not delete stale log file {Path}; leaving it for the next sweep.", path);
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerate the PRE-M8 legacy shared log files (<c>miller-&lt;date&gt;.log/.jsonl</c>) in
    /// <paramref name="logsDir"/> — the date-only form the old <c>Program.cs</c> wrote before per-pid files
    /// (finding-4). Recognized via the pure <see cref="IsLegacySharedLogName"/>. Isolated so it is the only place
    /// the legacy name shape is matched; the caller (<see cref="ReapStaleLogs"/>) deletes them best-effort.
    /// </summary>
    private static IReadOnlyList<string> EnumerateLegacyShared(string logsDir)
    {
        var legacy = new List<string>();
        foreach (string path in Directory.EnumerateFiles(logsDir))
        {
            if (IsLegacySharedLogName(Path.GetFileName(path)))
                legacy.Add(path);
        }

        return legacy;
    }

    /// <summary>
    /// Probe which of the discovered (non-current) pids are STILL RUNNING so the reaper never unlinks a live
    /// peer's open log file (finding-1/-8). Each distinct owning pid is checked with
    /// <see cref="Process.GetProcessById(int)"/>: a returned (non-exited) process means the pid is alive;
    /// an <see cref="ArgumentException"/> means no such process (dead). Any other probe failure is treated
    /// conservatively as ALIVE — when liveness is uncertain it is safer to keep the file than to silently destroy
    /// a peer's logs. The current pid is omitted (the planner guards it independently). Infra-only; the pure
    /// <see cref="LogFileReaper.Plan"/> consumes the result.
    /// </summary>
    private static IReadOnlySet<int> ProbeLivePids(IReadOnlyList<LogFileInfo> discovered, int currentPid)
    {
        var live = new HashSet<int>();
        foreach (int pid in discovered.Select(f => f.Pid).Where(p => p != currentPid).Distinct())
        {
            if (IsPidAlive(pid))
                live.Add(pid);
        }

        return live;
    }

    /// <summary>
    /// True when <paramref name="pid"/> names a process that is currently running. Returns false only when the OS
    /// reports no such process (<see cref="ArgumentException"/> from <see cref="Process.GetProcessById(int)"/>);
    /// any other failure is treated as alive (keep-the-file is the safe default for an uncertain probe). A
    /// process that has exited but whose <see cref="Process"/> object lingers is reported dead via
    /// <see cref="Process.HasExited"/>.
    /// </summary>
    private static bool IsPidAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with this id is running — the owning miller has exited; its files are reapable.
            return false;
        }
        catch (Exception)
        {
            // An unexpected probe failure (e.g. access denied querying HasExited): err on the side of keeping the
            // file rather than risk unlinking a live peer's open log. Never fail startup over a liveness probe.
            return true;
        }
    }

    /// <summary>
    /// Enumerate the per-pid log files under <paramref name="logsDir"/>, parsing the owning pid from each
    /// <c>miller-&lt;pid&gt;-*.log</c>/<c>.jsonl</c> name and reading its last-write timestamp. Files whose name
    /// does not parse to a pid (an unrelated file) are skipped. Pure-ish read step feeding the pure
    /// <see cref="LogFileReaper.Plan"/>; isolated here so it is the only place the name format is parsed.
    /// </summary>
    public static IReadOnlyList<LogFileInfo> EnumerateExisting(string logsDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDir);
        if (!Directory.Exists(logsDir))
            return Array.Empty<LogFileInfo>();

        var infos = new List<LogFileInfo>();
        foreach (string path in Directory.EnumerateFiles(logsDir))
        {
            string ext = Path.GetExtension(path);
            if (!ext.Equals(".log", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryParsePid(Path.GetFileName(path), out int pid))
                infos.Add(new LogFileInfo(path, pid, File.GetLastWriteTimeUtc(path)));
        }

        return infos;
    }

    /// <summary>
    /// True when <paramref name="fileName"/> is a PRE-M8 legacy shared log of the form
    /// <c>miller-&lt;YYYYMMDD&gt;.log</c>/<c>.jsonl</c> — the single daily-rolled file the old <c>Program.cs</c>
    /// wrote before per-pid files (m8-design §D1). It is distinguished from the per-pid scheme
    /// (<c>miller-&lt;pid&gt;-&lt;date&gt;.ext</c>) by having NO second <c>-</c> after the prefix: the whole
    /// post-prefix stem (minus the extension) is a single all-digit date segment. These are swept ONCE on the
    /// first M8 startup so an upgraded workspace's orphaned shared logs do not accumulate forever (D6). Pure.
    /// </summary>
    public static bool IsLegacySharedLogName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        string ext = Path.GetExtension(fileName);
        if (!ext.Equals(".log", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The stem between "miller-" and the extension. The per-pid scheme always has a second '-' here (the
        // date slot after the pid); the legacy date-only form has none — and the date segment must be all digits.
        string stem = Path.GetFileNameWithoutExtension(fileName)[Prefix.Length..];
        return stem.Length > 0
            && !stem.Contains('-', StringComparison.Ordinal)
            && stem.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Parse the owning pid from a per-pid log file name of the form <c>miller-&lt;pid&gt;-...</c> (Serilog
    /// appends <c>YYYYMMDD</c> after the trailing <c>-</c>). Returns false for any name that does not start with
    /// the <c>miller-</c> prefix or whose pid segment is not an integer (an unrelated file).
    /// </summary>
    public static bool TryParsePid(string fileName, out int pid)
    {
        pid = 0;
        if (string.IsNullOrEmpty(fileName) || !fileName.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        // After "miller-", the pid runs up to the next '-' (the date/extension slot).
        int start = Prefix.Length;
        int dash = fileName.IndexOf('-', start);
        if (dash <= start)
            return false;

        string pidSegment = fileName[start..dash];
        return int.TryParse(pidSegment, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
    }
}
