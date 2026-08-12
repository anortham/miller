using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Formatting.Compact;

namespace Miller.Server.Logging;

/// <summary>
/// The thin infra that wires Miller's Serilog sinks. It is the ONE place the sink layout lives so both
/// <c>Program.cs</c> and the sink-config test build the SAME configuration:
///
/// <list type="bullet">
/// <item><b>Shared daily files.</b> Every process appends to ONE daily-rolled pair —
/// <c>miller-&lt;YYYYMMDD&gt;.log</c> (human) and <c>miller-&lt;YYYYMMDD&gt;.jsonl</c> (machine) — via Serilog's
/// cross-process <c>shared</c> sink. One pair per day keeps <c>.miller/logs</c> small and predictable (the old
/// per-pid scheme created two files per process LAUNCH, which piled up fast). Which process wrote a line, and its
/// leader/reader role, are LOG PROPERTIES (<c>pid</c> + the live <see cref="MillerRole"/> enricher), not part of
/// the file name — so attribution survives without a file-per-process. Retention keeps
/// <see cref="RetainedFileCountLimit"/> rolls per sink, and a day that exceeds
/// <see cref="FileSizeLimitBytes"/> rolls within the day so one busy day cannot grow without bound.</item>
/// <item><b>Level switch.</b> A <see cref="LoggingLevelSwitch"/> initialised from <c>MILLER_LOG_LEVEL</c> (via
/// the pure <see cref="LogLevelParse"/>) controls the minimum level — an operator dials verbosity at startup with
/// no recompile.</item>
/// <item><b>Machine sink.</b> The second sink writes compact JSON lines to the <c>.jsonl</c> (the log-viewer's
/// input). The human <c>.log</c> stays for eyeballs.</item>
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
    /// <summary>How many rolls to retain for each shared file.</summary>
    public const int RetainedFileCountLimit = 14;

    /// <summary>
    /// Per-file size cap before a same-day roll. The daily roll alone bounds how many FILES accumulate, not how
    /// large one day can get: a multi-process fleet under a busy writer shares one pair, so a single day is
    /// unbounded without this. Size rolling turns that into the same retention promise the day count already
    /// makes — at most <see cref="RetainedFileCountLimit"/> files per sink, each at most this large.
    /// </summary>
    public const long FileSizeLimitBytes = 32L * 1024 * 1024;

    // The common file-name prefix of every Miller log file. Serilog's daily roll appends YYYYMMDD where the
    // trailing '-' sits, so the on-disk name is miller-<YYYYMMDD>.log / .jsonl.
    private const string Prefix = "miller-";

    // The human template: timestamp, level, role + pid + cid (cid empty for background logs), source context,
    // message, then the exception. role (leader/reader) and pid distinguish the processes sharing the daily file;
    // cid is empty whenever no per-call correlation id is present (a background log).
    private const string HumanOutputTemplate =
        "{Timestamp:HH:mm:ss.fff} [{Level:u3}] (role:{role} pid:{pid} cid:{cid}) {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Apply Miller's full sink layout to <paramref name="config"/>: the stderr console (STDIO purity), the
    /// shared daily human <c>.log</c> and machine <c>.jsonl</c> file sinks, the <see cref="LoggingLevelSwitch"/>,
    /// and the <c>cid</c>/<c>pid</c>/<c>role</c> enrichment both templates render. The caller adds nothing else.
    /// </summary>
    /// <param name="config">The configuration to mutate (typically a fresh <see cref="LoggerConfiguration"/>).</param>
    /// <param name="logsDir">The <c>&lt;root&gt;/.miller/logs</c> directory (must already exist).</param>
    /// <param name="pid">This process's id — enriched as the constant <c>pid</c> property on every line.</param>
    /// <param name="levelSwitch">The level switch controlling the minimum level (initialised from the env by the caller).</param>
    /// <returns>The same <paramref name="config"/> for fluent chaining.</returns>
    public static LoggerConfiguration Configure(
        LoggerConfiguration config, string logsDir, int pid, LoggingLevelSwitch levelSwitch)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDir);
        ArgumentNullException.ThrowIfNull(levelSwitch);

        var paths = LogFilePaths(logsDir);

        return config
            .MinimumLevel.ControlledBy(levelSwitch)
            // pid is a process constant; cid arrives per-call via LogContext (the telemetry filter pushes it);
            // role is the LIVE leader/reader value re-read per event so a leadership transition is reflected.
            .Enrich.FromLogContext()
            .Enrich.WithProperty("pid", pid)
            .Enrich.With(MillerRole.Enricher)
            .WriteTo.Console(
                standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose,
                outputTemplate: HumanOutputTemplate)
            // shared:true — every Miller process appends to the same daily file safely (Serilog takes a global
            // mutex per file). One pair per day across the whole multi-process fleet; pid/role on each line keep
            // attribution. retainedFileCountLimit prunes old days on roll.
            .WriteTo.File(
                paths.HumanLog,
                rollingInterval: RollingInterval.Day,
                shared: true,
                retainedFileCountLimit: RetainedFileCountLimit,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                outputTemplate: HumanOutputTemplate)
            .WriteTo.File(
                new CompactJsonFormatter(),
                paths.JsonLog,
                rollingInterval: RollingInterval.Day,
                shared: true,
                retainedFileCountLimit: RetainedFileCountLimit,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true);
    }

    /// <summary>
    /// The shared file base paths under <paramref name="logsDir"/>: the human <c>miller-.log</c> and the machine
    /// <c>miller-.jsonl</c>. Serilog appends the date before the extension when it daily-rolls, so the on-disk
    /// names are <c>miller-&lt;YYYYMMDD&gt;.log</c> / <c>.jsonl</c>. The trailing <c>-</c> before the extension is
    /// the slot Serilog fills with <c>YYYYMMDD</c>.
    /// </summary>
    public static (string HumanLog, string JsonLog) LogFilePaths(string logsDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDir);
        return (
            Path.Combine(logsDir, Prefix + ".log"),
            Path.Combine(logsDir, Prefix + ".jsonl"));
    }
}
