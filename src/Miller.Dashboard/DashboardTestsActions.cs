using System.Diagnostics;

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
        startInfo.ArgumentList.Add(action);

        if (RunProcessOverride is { } run)
            return run(startInfo);
        return RunProcess(startInfo, action);
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
