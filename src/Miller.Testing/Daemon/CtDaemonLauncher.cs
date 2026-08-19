using System.Diagnostics;
using System.Runtime.Versioning;

namespace Miller.Testing;

public enum CtDaemonSpawnStatus
{
    Started,
    AlreadyRunning,
    Failed,
    Refused,
}

public sealed record CtDaemonSpawnResult(
    CtDaemonSpawnStatus Status,
    int? ProcessId,
    string? Executable,
    string? Reason);

public enum CtRunExecution
{
    ForegroundOneShot,
    Daemon,
}

public sealed record CtRunDisposition(CtRunExecution Execution, CtDaemonLeaseRecord? Lease);

/// <summary>
/// Starts at most one detached CT daemon per workspace. A <c>run</c> with no live daemon is a
/// foreground one-shot in the calling process — this type exposes that disposition for Task 12
/// and does not start the engine.
/// </summary>
public static class CtDaemonLauncher
{
    public const string DaemonVerb = "ct-daemon";

    public static CtRunDisposition ResolveRun(string workspaceRoot)
    {
        CtDaemonLeaseRecord? live = CtDaemonLease.TryReadLive(workspaceRoot);
        return live is null
            ? new CtRunDisposition(CtRunExecution.ForegroundOneShot, null)
            : new CtRunDisposition(CtRunExecution.Daemon, live);
    }

    public static string ResolveCurrentExecutable()
    {
        if (Environment.ProcessPath is { Length: > 0 } processPath && File.Exists(processPath))
            return Path.GetFullPath(processPath);

        using var process = Process.GetCurrentProcess();
        string? module = process.MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(module) && File.Exists(module))
            return Path.GetFullPath(module);

        string[] args = Environment.GetCommandLineArgs();
        if (args.Length > 0 && File.Exists(args[0]))
            return Path.GetFullPath(args[0]);

        throw new InvalidOperationException("Cannot resolve the current executable for the CT daemon.");
    }

    public static CtDaemonSpawnResult SpawnDetached(
        string workspaceRoot,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        RejectSensitiveRoot(workspaceRoot);

        if (CtDaemonLease.TryReadLive(workspaceRoot) is { } live)
        {
            return new CtDaemonSpawnResult(
                CtDaemonSpawnStatus.AlreadyRunning,
                live.Identity.Pid,
                Executable: null,
                "daemon already running");
        }

        string executable = ResolveCurrentExecutable();
        string root = Path.GetFullPath(workspaceRoot);
        ProcessStartInfo startInfo = BuildStartInfo(executable, root);
        Func<ProcessStartInfo, Process?> starter = startProcess ?? Process.Start;

        Process? process;
        try
        {
            process = starter(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new CtDaemonSpawnResult(CtDaemonSpawnStatus.Failed, null, executable, ex.Message);
        }

        if (process is null)
        {
            return new CtDaemonSpawnResult(
                CtDaemonSpawnStatus.Failed,
                null,
                executable,
                "daemon process did not start");
        }

        int? pid = TryReadProcessId(process);
        return new CtDaemonSpawnResult(CtDaemonSpawnStatus.Started, pid, executable, "started");
    }

    internal static void RejectSensitiveRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!IsSensitiveRoot(workspaceRoot))
            return;

        string full = Path.GetFullPath(workspaceRoot);
        throw new InvalidOperationException(
            $"Refusing to use sensitive system path '{full}' as a CT daemon workspace root.");
    }

    internal static bool IsSensitiveRoot(string candidate) =>
        IsSensitiveRoot(candidate, SensitiveRootCandidates());

    internal static bool IsSensitiveRoot(string candidate, IReadOnlyCollection<string> forbidden)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentNullException.ThrowIfNull(forbidden);

        string normCandidate = Normalize(candidate);
        if (Path.GetDirectoryName(normCandidate) is null)
            return true;

        foreach (string entry in forbidden)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            if (string.Equals(normCandidate, Normalize(entry), PathComparison))
                return true;
        }

        return false;
    }

    private static ProcessStartInfo BuildStartInfo(string executable, string workspaceRoot)
    {
        bool isDll = string.Equals(Path.GetExtension(executable), ".dll", StringComparison.OrdinalIgnoreCase);
        string fileName = isDll ? "dotnet" : executable;
        string? dllArgument = isDll ? executable : null;

        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? WindowsStartInfo(fileName, dllArgument)
            : UnixDetachedStartInfo(fileName, dllArgument, workspaceRoot);
        startInfo.Environment[CtEnvironment.DaemonWorkspaceRoot] = workspaceRoot;
        return startInfo;
    }

    [SupportedOSPlatform("windows")]
    private static ProcessStartInfo WindowsStartInfo(string fileName, string? dllArgument)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            CreateNewProcessGroup = true,
        };
        if (dllArgument is not null)
            startInfo.ArgumentList.Add(dllArgument);
        startInfo.ArgumentList.Add(DaemonVerb);
        return startInfo;
    }

    private static ProcessStartInfo UnixDetachedStartInfo(
        string fileName, string? dllArgument, string workspaceRoot)
    {
        string ctDir = CtDaemonProtocol.RootDirectory(workspaceRoot);
        Directory.CreateDirectory(ctDir);
        string stdoutPath = Path.Combine(ctDir, "daemon.out.log");
        string stderrPath = Path.Combine(ctDir, "daemon.err.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(UnixLaunchScript);
        startInfo.ArgumentList.Add("sh");
        startInfo.ArgumentList.Add(fileName);
        startInfo.ArgumentList.Add(dllArgument ?? string.Empty);
        startInfo.ArgumentList.Add(DaemonVerb);
        startInfo.ArgumentList.Add(stdoutPath);
        startInfo.ArgumentList.Add(stderrPath);
        return startInfo;
    }

    private static int? TryReadProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SensitiveRootCandidates()
    {
        var forbidden = new List<string>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            forbidden.Add(home);

        if (OperatingSystem.IsMacOS())
        {
            forbidden.Add("/Users");
            forbidden.Add("/var/root");
            forbidden.Add("/private/var/root");
        }
        else if (OperatingSystem.IsLinux())
        {
            forbidden.Add("/home");
            forbidden.Add("/root");
        }
        else if (OperatingSystem.IsWindows())
        {
            string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") is { Length: > 0 } sd
                ? sd
                : "C:";
            string driveRoot = systemDrive.TrimEnd('\\') + "\\";
            forbidden.Add(driveRoot + "Users");
            forbidden.Add(driveRoot + "Windows");
            forbidden.Add(driveRoot + "Windows\\System32");
            forbidden.Add(driveRoot + "Program Files");
            forbidden.Add(driveRoot + "Program Files (x86)");
            forbidden.Add(driveRoot + "ProgramData");
            foreach (string key in new[]
                     {
                         "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "PUBLIC",
                     })
            {
                if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value)
                    forbidden.Add(value);
            }
        }

        return forbidden;
    }

    private static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private const string UnixLaunchScript = """
exe="$1"
arg="$2"
verb="$3"
stdout_path="$4"
stderr_path="$5"
if [ -n "$arg" ]; then
  nohup "$exe" "$arg" "$verb" >>"$stdout_path" 2>>"$stderr_path" </dev/null &
else
  nohup "$exe" "$verb" >>"$stdout_path" 2>>"$stderr_path" </dev/null &
fi
""";
}
