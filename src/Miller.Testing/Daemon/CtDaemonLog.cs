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

    public static void Write(string workspaceRoot, string message, DateTimeOffset? utcNow = null, int? pid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        DateTimeOffset when = utcNow ?? DateTimeOffset.UtcNow;
        int processId = pid ?? Environment.ProcessId;
        string logsDir = LogsDirectory(workspaceRoot);
        Directory.CreateDirectory(logsDir);
        (string humanPath, string jsonPath) = LogFilePaths(logsDir, when);

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

    private static void AppendLine(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.WriteLine(line);
    }
}
