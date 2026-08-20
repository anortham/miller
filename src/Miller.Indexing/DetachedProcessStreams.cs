using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Miller.Indexing;

/// <summary>
/// Starts a detached child with two log FILES as its stdout and stderr, so its output belongs to the files
/// and survives a launcher that exits at once.
///
/// <para>This is the Windows equivalent of the Unix launchers' <c>&gt;&gt;"$stdout_path"
/// 2&gt;&gt;"$stderr_path"</c>, and it replaces a pump thread that ran in the LAUNCHER: a launcher that
/// prints one line and exits about a millisecond after the spawn takes the pipe read handles down with it,
/// and the documented start path produced no diagnostic at all.</para>
///
/// <para><b>How the handles reach the child.</b> .NET builds a pipe only for a stream the caller asked to
/// redirect; for the rest it copies the launcher's current <c>GetStdHandle</c> values into
/// <c>STARTUPINFO</c> under <c>STARTF_USESTDHANDLES</c> and creates the process with handle inheritance on.
/// Swapping this process's stdout/stderr to the log files across that one call therefore hands the child
/// duplicated FILE handles. The launcher closes its own copies on the way out; the child keeps writing for
/// its whole life. The handles must be marked inheritable for CreateProcess to duplicate them, which is what
/// <see cref="OpenInheritableLog"/> does.</para>
///
/// <para><b>Why the launcher's OWN handles are suppressed.</b> "Handle inheritance on" is not selective:
/// Windows duplicates EVERY inheritable handle into the child, not only the three in <c>STARTUPINFO</c>. A
/// shell that pipes the launching command's stdout therefore had its pipe duplicated into the child, which
/// held it for the child's whole life — the command printed its line, the launcher exited, and the pipeline
/// still hung. <see cref="StandardHandleInheritance"/> clears the inheritable flag on the launcher's own
/// handles for the length of the spawn. Asking for no redirection does NOT avoid this: measured on Windows
/// 11, .NET creates the child with inheritance on either way.</para>
///
/// <para><b>Why it cannot hang the child.</b> There is no pipe on the output side at all — a write to a file
/// never blocks waiting for a reader, so no unread buffer can ever fill. stdin is the caller's concern: a
/// launcher that redirects it must close its own write end so the child reads end-of-file rather than
/// blocking.</para>
///
/// <para><b>The swap window.</b> <c>SetStdHandle</c> is process-wide, so the swap is held for the single
/// <c>Process.Start</c> call, restored in a <c>finally</c>, and serialized by a lock. It does not disturb
/// <c>Console.Out</c>/<c>Console.Error</c>, which cache their own handle on first use — an MCP server has
/// long since bound its stdio transport. A failed swap is reported as a failed spawn rather than ignored:
/// starting a child on the launcher's real stdout is exactly how stray child text would corrupt an MCP
/// protocol channel.</para>
/// </summary>
public static class DetachedProcessStreams
{
    // STD_OUTPUT_HANDLE / STD_ERROR_HANDLE / HANDLE_FLAG_INHERIT from winbase.h.
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const int HandleFlagInherit = 0x00000001;

    /// <summary>
    /// Serializes the process-wide standard-handle swap. Two concurrent spawns from one MCP server would
    /// otherwise interleave save and restore and leave the launcher's stdout pointed at a child's log.
    /// </summary>
    private static readonly object StandardHandleGate = new();

    /// <summary>
    /// Starts <paramref name="startInfo"/> with the two log files as the child's stdout and stderr.
    ///
    /// <para>Off Windows this is just <paramref name="starter"/>: the Unix launchers redirect through
    /// <c>/bin/sh</c> inside the start info itself, so the paths here are already in the argv and a second
    /// redirection would be wrong.</para>
    /// </summary>
    public static Process? Start(
        ProcessStartInfo startInfo,
        string stdoutPath,
        string stderrPath,
        Func<ProcessStartInfo, Process?> starter)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(stdoutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stderrPath);
        ArgumentNullException.ThrowIfNull(starter);

        return OperatingSystem.IsWindows()
            ? StartOnWindows(startInfo, stdoutPath, stderrPath, starter)
            : starter(startInfo);
    }

    [SupportedOSPlatform("windows")]
    private static Process? StartOnWindows(
        ProcessStartInfo startInfo,
        string stdoutPath,
        string stderrPath,
        Func<ProcessStartInfo, Process?> starter)
    {
        using FileStream stdout = OpenInheritableLog(stdoutPath);
        using FileStream stderr = OpenInheritableLog(stderrPath);

        lock (StandardHandleGate)
        {
            // Taken BEFORE the swap, and held only for this spawn: the LAUNCHER's own standard handles lose
            // their inheritable flag, so the child cannot keep the caller's stdout pipe open for its whole
            // life. After the swap those handles are the log FILES, which the child must inherit.
            using IDisposable inheritance = StandardHandleInheritance.SuppressForSpawn();

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
    /// Opens one log file for append with a handle CreateProcess is allowed to duplicate into the child.
    /// Sharing stays wide (<c>ReadWrite | Delete</c>) so a status command, an editor, or a log reader can
    /// read the file — and delete or rename it — while the child holds it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FileStream OpenInheritableLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
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
}
