using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Miller.Indexing;

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
        (string stdoutPath, string stderrPath) = PrepareDaemonLogPaths(root);
        ProcessStartInfo startInfo = BuildStartInfo(executable, root, stdoutPath, stderrPath);
        Func<ProcessStartInfo, Process?> starter = startProcess ?? Process.Start;

        Process? process;
        try
        {
            // Both branches hand the daemon the two log FILES as its own stdout and stderr. Windows needs
            // the extra step because it has no /bin/sh to redirect through; see StartWithFileBackedStreams.
            process = OperatingSystem.IsWindows()
                ? StartWithFileBackedStreams(startInfo, stdoutPath, stderrPath, starter)
                : starter(startInfo);
        }
        // Win32Exception is the shape a real spawn failure takes (missing image, denied by policy). Without it
        // the exception escaped to the CLI catch-all and reported exit 1 "unexpected", while tests-cli-v1 calls
        // a failed spawn an operational refusal (3) — the same code every other Failed result already produces.
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
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

        ReleaseDaemonStandardInput(process);
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

    /// <summary>
    /// Both daemon log files, with their directory created. The daemon writes its own diagnostics nowhere
    /// else: <c>ct-daemon</c> is a CLI verb, and the CLI path starts no Serilog file sink.
    /// </summary>
    private static (string StdoutPath, string StderrPath) PrepareDaemonLogPaths(string workspaceRoot)
    {
        string ctDir = CtDaemonProtocol.RootDirectory(workspaceRoot);
        Directory.CreateDirectory(ctDir);
        return (
            Path.Combine(ctDir, "daemon.out.log"),
            Path.Combine(ctDir, "daemon.err.log"));
    }

    private static ProcessStartInfo BuildStartInfo(
        string executable, string workspaceRoot, string stdoutPath, string stderrPath)
    {
        bool isDll = string.Equals(Path.GetExtension(executable), ".dll", StringComparison.OrdinalIgnoreCase);
        string fileName = isDll ? "dotnet" : executable;
        string? dllArgument = isDll ? executable : null;

        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? WindowsStartInfo(fileName, dllArgument)
            : UnixDetachedStartInfo(fileName, dllArgument, stdoutPath, stderrPath);
        startInfo.Environment[CtEnvironment.DaemonWorkspaceRoot] = workspaceRoot;

        // A live process holds its working directory open, and on Windows that handle refuses a rename or a
        // delete of the directory — so a daemon started in the workspace root pins the very tree Miller
        // indexes for its whole life. The daemon never needs that root as its cwd: DaemonWorkspaceRoot above
        // carries it explicitly and beats the CLI context, and every provider command names its own
        // WorkingDirectory. Both branches use the same directory so the caller sees one behaviour.
        string workingDirectory = ResolveDaemonWorkingDirectory(workspaceRoot, DaemonWorkingDirectoryCandidates());
        Directory.CreateDirectory(workingDirectory);
        startInfo.WorkingDirectory = workingDirectory;
        return startInfo;
    }

    // stdout and stderr are deliberately NOT redirected. .NET only builds pipes for the streams the caller
    // asks for; for the others it passes the launcher's CURRENT standard handles to CreateProcess under
    // STARTF_USESTDHANDLES. StartWithFileBackedStreams swaps those two handles to the log files for the
    // length of the spawn, so the daemon is born writing straight into daemon.out.log / daemon.err.log.
    //
    // Redirecting them instead would put the capture in a pipe that only the LAUNCHER can drain, which is
    // the defect this shape replaces: `miller tests serve` prints one line and exits about a millisecond
    // later, long before the daemon has finished starting, and a launcher-side pump dies with it.
    //
    // Asking for stdin alone is what keeps the other two on the swapped handles AND mirrors the Unix
    // `</dev/null`: the launcher closes the write end at once, so the daemon reads EOF instead of stealing
    // bytes from the launcher's own stdin (an MCP server's stdin is its protocol channel).
    [SupportedOSPlatform("windows")]
    private static ProcessStartInfo WindowsStartInfo(string fileName, string? dllArgument)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            CreateNewProcessGroup = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        if (dllArgument is not null)
            startInfo.ArgumentList.Add(dllArgument);
        startInfo.ArgumentList.Add(DaemonVerb);
        return startInfo;
    }

    private static ProcessStartInfo UnixDetachedStartInfo(
        string fileName, string? dllArgument, string stdoutPath, string stderrPath)
    {
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

    /// <summary>
    /// Closes the launcher's end of the daemon's stdin pipe so the daemon reads EOF, exactly as the Unix
    /// branch's <c>&lt;/dev/null</c> does. An injected test starter may have started its process with no
    /// redirection at all, which shows up here as "no stream", so a missing stream is tolerated.
    /// </summary>
    private static void ReleaseDaemonStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        // ObjectDisposedException is an InvalidOperationException, so both shapes land here.
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
    }

    /// <summary>
    /// Starts the daemon with the two log FILES as its own stdout and stderr, so the capture belongs to the
    /// files and survives a launcher that exits at once. This is the Windows equivalent of the Unix branch's
    /// <c>&gt;&gt;"$stdout_path" 2&gt;&gt;"$stderr_path"</c>, and it replaces a pump thread that ran in the
    /// LAUNCHER: <c>miller tests serve</c> prints one line and exits about a millisecond after the spawn, so
    /// the pump and the pipe read handles died before the daemon had finished starting and the documented
    /// start path produced no diagnostic at all.
    ///
    /// <para><b>How the handles reach the child.</b> .NET builds a pipe only for a stream the caller asked to
    /// redirect; for the rest it copies the launcher's current <c>GetStdHandle</c> values into
    /// <c>STARTUPINFO</c> under <c>STARTF_USESTDHANDLES</c> and creates the process with handle inheritance
    /// on. Swapping this process's stdout/stderr to the log files across that one call therefore hands the
    /// child duplicated FILE handles. The launcher closes its own copies on the way out; the child keeps
    /// writing for its whole life. The handles must be marked inheritable for CreateProcess to duplicate
    /// them, which is what <see cref="OpenInheritableLog"/> does.</para>
    ///
    /// <para><b>Why it cannot hang the daemon.</b> There is no pipe on the output side at all — a write to a
    /// file never blocks waiting for a reader, so no unread buffer can ever fill. stdin IS a pipe, and
    /// <see cref="ReleaseDaemonStandardInput"/> closes the launcher's write end immediately, so a read
    /// returns EOF rather than blocking.</para>
    ///
    /// <para><b>The swap window.</b> <c>SetStdHandle</c> is process-wide, so the swap is held for the single
    /// <c>Process.Start</c> call, restored in a <c>finally</c>, and serialized by a lock. It does not disturb
    /// <c>Console.Out</c>/<c>Console.Error</c>, which cache their own handle on first use — an MCP server has
    /// long since bound its stdio transport. A failed swap is reported as a failed spawn rather than ignored:
    /// starting the daemon on the launcher's real stdout is exactly how stray daemon text would corrupt an
    /// MCP protocol channel.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Process? StartWithFileBackedStreams(
        ProcessStartInfo startInfo,
        string stdoutPath,
        string stderrPath,
        Func<ProcessStartInfo, Process?> starter)
    {
        using FileStream stdout = OpenInheritableLog(stdoutPath);
        using FileStream stderr = OpenInheritableLog(stderrPath);

        lock (StandardHandleGate)
        {
            IntPtr savedOutput = NativeMethods.GetStdHandle(StdOutputHandle);
            IntPtr savedError = NativeMethods.GetStdHandle(StdErrorHandle);
            bool swappedOutput = false;
            bool swappedError = false;
            try
            {
                swappedOutput = NativeMethods.SetStdHandle(
                    StdOutputHandle, stdout.SafeFileHandle.DangerousGetHandle());
                if (!swappedOutput)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                swappedError = NativeMethods.SetStdHandle(
                    StdErrorHandle, stderr.SafeFileHandle.DangerousGetHandle());
                if (!swappedError)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                return starter(startInfo);
            }
            finally
            {
                if (swappedOutput)
                    _ = NativeMethods.SetStdHandle(StdOutputHandle, savedOutput);
                if (swappedError)
                    _ = NativeMethods.SetStdHandle(StdErrorHandle, savedError);
            }
        }
    }

    /// <summary>
    /// Opens one daemon log file for append with a handle CreateProcess is allowed to duplicate into the
    /// child. Sharing stays wide (<c>ReadWrite | Delete</c>) so `tests status`, an editor, or a log reader
    /// can read the file — and delete or rename it — while the daemon holds it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FileStream OpenInheritableLog(string path)
    {
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (!NativeMethods.SetHandleInformation(
                    stream.SafeFileHandle, HandleFlagInherit, HandleFlagInherit))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The first candidate directory that is not inside the workspace. The last candidate is the user
    /// profile, which no legal workspace root can contain: <c>WorkspaceRootSafety</c> already refuses the
    /// home directory and every drive root, so no allowed root is an ancestor of the profile.
    /// </summary>
    internal static string ResolveDaemonWorkingDirectory(string workspaceRoot, IReadOnlyList<string> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(candidates);

        string root = Normalize(workspaceRoot);
        string? last = null;
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string full = Path.GetFullPath(candidate);
            last = full;
            if (!IsInside(Normalize(full), root))
                return full;
        }

        return last ?? Path.GetTempPath();
    }

    private static IReadOnlyList<string> DaemonWorkingDirectoryCandidates() =>
        [
            // Same choice the dashboard launcher makes for its own detached process.
            MillerHome.ResolveMillerDirectory(),
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];

    private static bool IsInside(string normalizedCandidate, string normalizedRoot)
    {
        if (string.Equals(normalizedCandidate, normalizedRoot, PathComparison))
            return true;

        string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, PathComparison);
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

    // STD_OUTPUT_HANDLE / STD_ERROR_HANDLE / HANDLE_FLAG_INHERIT from winbase.h.
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const int HandleFlagInherit = 0x00000001;

    /// <summary>
    /// Serializes the process-wide standard-handle swap in <see cref="StartWithFileBackedStreams"/>. Two
    /// concurrent spawns from one MCP server would otherwise interleave save and restore and leave the
    /// launcher's stdout pointed at a daemon log.
    /// </summary>
    private static readonly object StandardHandleGate = new();

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int stdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetStdHandle(int stdHandle, IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, int mask, int flags);
    }

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
