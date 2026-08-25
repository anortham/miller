using System.Globalization;
using System.Text;

namespace Miller.Server.Logging;

/// <summary>
/// The last-resort record of a startup that died before, or despite, Serilog.
///
/// <para><b>Why this exists (load-bearing).</b> <c>Program.cs</c> assigns <c>Log.Logger</c> partway down its
/// startup path. Everything above that line — the CLI branch, the workspace resolve, the sensitive-root guard,
/// the logs-directory create — runs with the silent default logger, and everything below it ran with no
/// top-level catch. A failure in either region left <b>zero</b> lines in <c>&lt;workspace&gt;/.miller/logs</c>,
/// which is the only file a user knows to open. A Windows plugin launch failed exactly this way on 2026-08-25
/// and could not be diagnosed from Miller's own logs at all.</para>
///
/// <para><b>Two channels, on purpose.</b> stderr always receives the record: an MCP client captures a stdio
/// server's stderr even when the process never completes its handshake, so that copy survives a startup that
/// never reached a log file. The daily-log append is best effort on top of it. stdout is never touched — the
/// MCP protocol owns it.</para>
///
/// <para><b>It never throws.</b> A crash reporter that crashes replaces a diagnosable failure with an
/// undiagnosable one.</para>
/// </summary>
public static class StartupFailureLog
{
    /// <summary>The <c>role</c> property value that marks these lines in the shared daily log.</summary>
    public const string Role = "startup";

    /// <summary>The longest record written to the daily log; a real .NET stack trace fits whole.</summary>
    private const int MaxRecordLength = 8192;

    /// <summary>
    /// The directories to try, in order: the log directory this process had already resolved (when it got that
    /// far), then the machine-global <c>&lt;home&gt;/.miller/logs</c>, then the temp directory.
    /// </summary>
    public static IReadOnlyList<string> CandidateDirectories(string? resolvedLogsDirectory, string millerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerDirectory);

        var candidates = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(resolvedLogsDirectory))
            candidates.Add(resolvedLogsDirectory);

        string machineGlobal = Path.Combine(millerDirectory, "logs");
        if (!candidates.Contains(machineGlobal, StringComparer.Ordinal))
            candidates.Add(machineGlobal);

        candidates.Add(Path.GetTempPath());
        return candidates;
    }

    /// <summary>
    /// Writes the failure to <paramref name="standardError"/>, then appends one <c>role:startup</c> line to the
    /// shared daily pair in the first candidate that accepts it. Returns the directory that took the append, or
    /// <c>null</c> when none did.
    /// </summary>
    public static string? Write(
        Exception error,
        string stage,
        IReadOnlyList<string> candidateDirectories,
        TextWriter standardError,
        DateTimeOffset utcNow,
        int pid)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(candidateDirectories);
        ArgumentNullException.ThrowIfNull(standardError);

        string record = Bound(Flatten(
            $"startup failed at stage '{stage}' (miller {MillerVersion.Current}, pid {pid}, cwd {SafeCurrentDirectory()}): "
            + $"type={error.GetType().FullName} message={error.Message} stack={error.StackTrace ?? string.Empty}"));

        try
        {
            standardError.WriteLine("miller: " + record);
            standardError.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        foreach (string directory in candidateDirectories)
        {
            if (TryAppend(directory, record, utcNow, pid))
                return directory;
        }

        return null;
    }

    private static bool TryAppend(string directory, string record, DateTimeOffset utcNow, int pid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            // A crash record must never RECREATE a tree that is gone: a workspace root removed mid-run would
            // otherwise come back as an untracked .miller/logs. Creating the logs directory INSIDE a parent
            // that exists stays allowed, because a first run has no logs directory yet.
            string? parent = Path.GetDirectoryName(directory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is not null && !Directory.Exists(parent))
                return false;

            Directory.CreateDirectory(directory);
            string stamp = utcNow.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string time = utcNow.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string process = pid.ToString(CultureInfo.InvariantCulture);

            AppendLine(
                Path.Combine(directory, "miller-" + stamp + ".log"),
                $"{time} [FTL] (role:{Role} pid:{process} cid:) Miller.Startup: {record}");
            AppendLine(
                Path.Combine(directory, "miller-" + stamp + ".jsonl"),
                JsonRecord(record, utcNow, pid));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or PathTooLongException)
        {
            return false;
        }
    }

    private static string JsonRecord(string record, DateTimeOffset utcNow, int pid)
    {
        var builder = new StringBuilder("{\"@t\":\"");
        builder.Append(utcNow.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        builder.Append("\",\"@l\":\"Fatal\",\"@m\":");
        AppendJsonString(builder, record);
        builder.Append(",\"role\":\"").Append(Role).Append("\",\"pid\":");
        builder.Append(pid.ToString(CultureInfo.InvariantCulture));
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                default:
                    if (character < ' ')
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
    }

    private static string SafeCurrentDirectory()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "<unavailable>";
        }
    }

    private static string Bound(string text) =>
        text.Length <= MaxRecordLength ? text : text[..MaxRecordLength] + " ...[truncated]";

    private static string Flatten(string text) =>
        string.Join(
            " ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void AppendLine(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.WriteLine(line);
    }
}
