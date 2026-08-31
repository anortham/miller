using System.Diagnostics;
using System.Text.Json;

namespace Miller.Dashboard;

/// <summary>
/// The outcome of one tests-panel action: whether the verb succeeded, and the first line of what
/// it printed — shown as the panel notice.
/// </summary>
public sealed record DashboardTestsActionOutcome(bool Success, string Message);

/// <summary>
/// Triggers continuous-testing lifecycle actions for the Tests panel by running the public
/// <c>miller tests</c> CLI verbs as a subprocess. The dashboard owns no CT decision logic: going
/// through the CLI keeps the family-daemon anchoring, the never-decided refusal rules, and the
/// version-aware daemon replacement in the one place they already live. Spawning also matters for
/// <c>start</c> specifically — an in-process daemon spawn would resolve the CURRENT executable,
/// which here is Miller.Dashboard, not miller.
/// </summary>
internal static class DashboardTestsActions
{
    private static readonly string[] AllowedActions = ["enable", "start", "run"];

    /// <summary>
    /// A run can legitimately execute tests inline when the daemon is not up, so the bound is
    /// generous; a subprocess still alive at the deadline is killed with its tree and reported.
    /// </summary>
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(120);

    internal static Func<ProcessStartInfo, DashboardTestsActionOutcome>? RunProcessOverride;

    internal static bool IsAllowed(string action) =>
        AllowedActions.Contains(action, StringComparer.Ordinal);

    internal static DashboardTestsActionOutcome Run(string toolsRoot, string workspaceRoot, string action)
    {
        if (!IsAllowed(action))
            return new DashboardTestsActionOutcome(false, $"unknown tests action '{action}'");
        string? executable = LocateMillerExecutable(toolsRoot);
        if (executable is null && RunProcessOverride is null)
        {
            return new DashboardTestsActionOutcome(
                false,
                $"the miller executable was not found beside the tools root '{toolsRoot}'");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable ?? "miller",
            WorkingDirectory = workspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("tests");
        startInfo.ArgumentList.Add(string.Equals(action, "start", StringComparison.Ordinal) ? "serve" : action);
        bool isRun = string.Equals(action, "run", StringComparison.Ordinal);
        if (isRun)
            startInfo.ArgumentList.Add("--json");

        DashboardTestsActionOutcome outcome = RunProcessOverride is { } run
            ? run(startInfo)
            : RunProcess(startInfo, action);
        return isRun ? TranslateRunOutcome(outcome) : outcome;
    }

    /// <summary>
    /// The run verb submits and returns the STANDING verdict — right after a click that reads
    /// "verdict=unknown", and a daemon whose loop is busy inside a drain misses the five-second ack
    /// and exits 3 even though it holds the request and will run it (observed 2026-08-26: the
    /// button reported failure while the daemon executed the submitted runs). So the run action
    /// asks for <c>--json</c> and rewrites the two known submit shapes into what the panel reader
    /// needs; anything else passes through, honest and unstyled.
    /// </summary>
    internal static DashboardTestsActionOutcome TranslateRunOutcome(DashboardTestsActionOutcome raw)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw.Message);
            JsonElement root = doc.RootElement;
            string? execution = root.TryGetProperty("execution", out JsonElement e) ? e.GetString() : null;
            string? verdict = root.TryGetProperty("verdict", out JsonElement v) ? v.GetString() : null;
            string? reason = root.TryGetProperty("reason", out JsonElement r) ? r.GetString() : null;
            if (raw.Success && string.Equals(execution, "daemon", StringComparison.Ordinal))
            {
                return new DashboardTestsActionOutcome(
                    true,
                    "run submitted to the test daemon — results appear here as they land");
            }

            if (raw.Success)
                return new DashboardTestsActionOutcome(true, $"tests ran — verdict {verdict ?? "unknown"}");
            if (string.Equals(reason, "not acknowledged", StringComparison.Ordinal)
                || string.Equals(reason, "unacked", StringComparison.Ordinal))
            {
                return new DashboardTestsActionOutcome(
                    true,
                    "run submitted; the daemon did not confirm within 5 seconds — it is likely busy "
                        + "executing. Watch this panel.");
            }

            return new DashboardTestsActionOutcome(false, reason ?? raw.Message);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>
    /// The miller binary the dashboard belongs to sits one directory above the tools root it was
    /// handed at launch (<c>MILLER_TOOLS_ROOT</c> is miller's <c>&lt;out&gt;/.tools</c>). Null when
    /// nothing is there — a dashboard launched standalone in development.
    /// </summary>
    internal static string? LocateMillerExecutable(string toolsRoot)
    {
        if (string.IsNullOrWhiteSpace(toolsRoot))
            return null;
        string? binDir = Path.GetDirectoryName(Path.GetFullPath(toolsRoot));
        if (binDir is null)
            return null;
        string candidate = Path.Combine(binDir, OperatingSystem.IsWindows() ? "miller.exe" : "miller");
        return File.Exists(candidate) ? candidate : null;
    }

    private static DashboardTestsActionOutcome RunProcess(ProcessStartInfo startInfo, string action)
    {
        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
                return new DashboardTestsActionOutcome(false, $"tests {action} did not start");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)ActionTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                }

                return new DashboardTestsActionOutcome(
                    false,
                    $"tests {action} was still running after {ActionTimeout.TotalSeconds:0}s and was stopped");
            }

            process.WaitForExit();
            string output = FirstLine(stdout.Result) ?? FirstLine(stderr.Result) ?? $"tests {action} finished";
            return new DashboardTestsActionOutcome(process.ExitCode == 0, output);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidOperationException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            return new DashboardTestsActionOutcome(false, ex.Message);
        }
    }

    private static string? FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return null;
    }
}
