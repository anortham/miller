using System.Globalization;
using System.Text;

namespace Miller.Testing;

/// <summary>
/// Appends CT daemon lines to the shared <c>.miller/logs</c> daily pair with <c>role=ct</c>.
/// Path helpers do not create directories; <see cref="Write"/> does.
/// </summary>
public static class CtDaemonLog
{
    public const string Role = "ct";

    public static string LogsDirectory(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, CtDaemonProtocol.MillerDirectoryName, "logs");
    }

    public static (string HumanLog, string JsonLog) LogFilePaths(string logsDir, DateTimeOffset utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDir);
        string stamp = utcNow.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return (
            Path.Combine(logsDir, "miller-" + stamp + ".log"),
            Path.Combine(logsDir, "miller-" + stamp + ".jsonl"));
    }

    /// <summary>
    /// Appends one CT daemon line to the shared daily pair. Never throws for an I/O reason.
    ///
    /// The daemon calls this from the last-resort catch blocks of its <c>RunAsync</c> loop, so an
    /// escaping exception here would end the loop while the lease still holds the daemon lock —
    /// the same failure the guarded status writes in <c>ContinuousTestDaemonHost</c> exist to
    /// prevent. A log line is an observable signal, not the liveness path, so a failed append
    /// degrades to no log and nothing else. The two argument guards stay outside the try: a null,
    /// empty, or whitespace root or message is a caller bug and must still reach the caller.
    /// </summary>
    public static void Write(string workspaceRoot, string message, DateTimeOffset? utcNow = null, int? pid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        try
        {
            DateTimeOffset when = utcNow ?? DateTimeOffset.UtcNow;
            int processId = pid ?? Environment.ProcessId;
            // A log line must never RECREATE a workspace root that is gone. The daemon logs for every
            // adopted worktree, and a lifecycle line written while one is being torn down re-minted
            // <worktree>/.miller/logs/ under the root git had just removed — the same resurrect the
            // detach status record used to cause, and the same reason git worktree remove failed.
            // The check is on the ROOT, not on .miller: a worktree that inherits its opt-in from the
            // main checkout has no .miller of its own until something writes one, and refusing those
            // would silently drop that worktree's logs. Creating .miller/logs INSIDE a root that
            // exists stays allowed — a first run has no logs directory yet.
            if (!Directory.Exists(workspaceRoot))
                return;

            string logsDir = LogsDirectory(workspaceRoot);
            Directory.CreateDirectory(logsDir);
            (string humanPath, string jsonPath) = LogFilePaths(logsDir, when);

            // ONE record per call, whatever the caller passed. Flattening here rather than at each call site
            // is what makes that true: a caller interpolates values it does not control - a project path, a
            // provider's own output - and on any filesystem that permits a newline in a name, an un-flattened
            // value writes EXTRA lines that read exactly like genuine records. The cap bounds one wedged poll
            // loop from filling the disk with repeated stack traces; it is far above any real record, and the
            // marker says plainly that something was cut rather than letting the line end mid-word.
            message = Bound(Flatten(message));

            string human =
                when.UtcDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " [INF] (role:" + Role + " pid:" + processId.ToString(CultureInfo.InvariantCulture)
                + " cid:) CtDaemon: " + message;
            AppendLine(humanPath, human);

            var payload = new Dictionary<string, object?>
            {
                ["@t"] = when.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                ["@l"] = "Information",
                ["@m"] = message,
                ["role"] = Role,
                ["pid"] = processId,
            };
            AppendLine(jsonPath, TestingJson.Value(payload));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The whole failure on ONE log line: type, full message, flattened stack. Newlines become spaces so the
    /// line stays greppable in the shared daily log. <see cref="Write"/> applies the same flattening plus a
    /// length bound to every record, so a caller that interpolates an untrusted value cannot forge a record.
    ///
    /// <para>ONE copy, shared by the daemon's two failure lines: the queue's discovery failure and the
    /// host's poll error. It started as a private copy in each file, and nothing kept the two in step -
    /// teaching one of them to unwrap an InnerException or to cap the stack would have split the single
    /// format a reader greps for.</para>
    ///
    /// <para>This is NOT the <c>ct.db</c> status summary. That column deliberately keeps the first line
    /// only, and its own helper stays private in <c>ContinuousTestDaemonQueue</c>.</para>
    /// </summary>
    internal static string FailureDetail(Exception exception)
    {
        string message = Flatten(exception.Message);
        string stack = Flatten(exception.StackTrace ?? string.Empty);
        return stack.Length == 0
            ? $"type={exception.GetType().FullName} message={message}"
            : $"type={exception.GetType().FullName} message={message} stack={stack}";
    }

    /// <summary>
    /// The longest record written to the daily log. A .NET stack trace runs a few thousand characters, so
    /// this holds every real failure whole; what it stops is a provider that emits megabytes on every poll.
    /// </summary>
    private const int MaxRecordLength = 8192;

    private static string Bound(string text) =>
        text.Length <= MaxRecordLength
            ? text
            : text[..MaxRecordLength] + " ...[truncated]";

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
