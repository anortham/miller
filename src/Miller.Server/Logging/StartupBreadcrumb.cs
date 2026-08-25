using System.Globalization;

namespace Miller.Server.Logging;

/// <summary>
/// The one line that says which file holds this process's log.
///
/// <para><b>Why this exists.</b> <c>Program.cs</c> picks the log directory once, before the logger exists: an
/// eager launch logs into <c>&lt;workspace&gt;/.miller/logs</c>, and a deferred one (no usable workspace cwd)
/// logs into <c>&lt;home&gt;/.miller/logs</c> for its whole life. Nothing moves it afterwards. A user who opens
/// the workspace log and finds it empty cannot tell a healthy process logging elsewhere from a process that
/// never started. This line answers that, and it is written to stderr unconditionally so a
/// <c>MILLER_LOG_LEVEL</c> of <c>Error</c> or <c>Fatal</c> cannot hide it.</para>
/// </summary>
public static class StartupBreadcrumb
{
    /// <summary>Formats the breadcrumb as a single line with no embedded newline.</summary>
    public static string Format(
        string version, int pid, string logsDirectory, string workingDirectory, bool eagerBootstrap, string logLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logLevel);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"miller {version} pid {pid} logging to {Single(logsDirectory)} "
            + $"(cwd {Single(workingDirectory)}, binding {(eagerBootstrap ? "cwd" : "deferred-mcp-roots")}, level {logLevel})");
    }

    private static string Single(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
