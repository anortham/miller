using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Miller.Indexing;

/// <summary>
/// Stops a DETACHED child process from inheriting the launcher's own standard handles on Windows.
///
/// <para><b>The defect this exists for.</b> A shell that runs <c>miller tests serve | anything</c> hands the
/// launcher a PIPE as its stdout. The detached daemon inherits a duplicate of that pipe's write end and
/// holds it for its whole life. The launcher prints one line and exits, but the reader never sees
/// end-of-file, so the pipeline hangs until the daemon stops. Any script or CI step that captures the
/// output blocks forever.</para>
///
/// <para><b>Why swapping the standard handles does not fix it.</b> Windows duplicates EVERY handle marked
/// inheritable into a child created with handle inheritance on — not only the three named in
/// <c>STARTUPINFO</c>. <c>SetStdHandle</c> changes which handle the child USES as stdout; it does not close
/// the original and it does not clear the original's inheritable flag, so the pipe rides along anyway.</para>
///
/// <para><b>Why dropping the redirection does not fix it either.</b> Measured on Windows 11 with .NET 10, by
/// timing a pipeline whose child sleeps 20 seconds: a child started with <c>RedirectStandardInput = true</c>
/// held the launcher's stdout pipe for the full 20 seconds, and so did a child started with NO stream
/// redirected at all. Clearing the flag returned the pipeline in 0 seconds. .NET creates the process with
/// handle inheritance on whichever streams the caller redirects, so "ask for no redirection" is not an
/// escape.</para>
///
/// <para><b>Order matters.</b> Take the scope BEFORE any <c>SetStdHandle</c> swap. After a swap the standard
/// handles are the ones the child is MEANT to inherit, and clearing those would break the child's own output
/// instead of releasing the caller's pipe.</para>
///
/// <para>A handle the OS refuses to report is left alone rather than treated as an error. The spawn must
/// still happen, and the worst case is the behaviour that existed before this guard.</para>
/// </summary>
public static class StandardHandleInheritance
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const int HandleFlagInherit = 1;

    private static readonly int[] StandardHandleIds = [StdInputHandle, StdOutputHandle, StdErrorHandle];

    /// <summary>
    /// Clears the inheritable flag on this process's three standard handles until the returned scope is
    /// disposed, and restores the previous flags then. A no-op on every non-Windows platform, where the
    /// launchers detach through a shell that redirects the child's streams itself.
    /// </summary>
    public static IDisposable SuppressForSpawn()
    {
        if (!OperatingSystem.IsWindows())
            return NoScope.Instance;

        return SuppressForSpawn(CurrentStandardHandles());
    }

    /// <summary>
    /// The same guard over an EXPLICIT handle list. Callers that already captured the handles pass them
    /// here; tests pass handles they created, which is the only way to assert the clear-and-restore
    /// behaviour — a test runner usually hands the test process standard handles that are already
    /// non-inheritable, so a test written against <see cref="SuppressForSpawn()"/> asserts nothing.
    /// </summary>
    public static IDisposable SuppressForSpawn(IReadOnlyList<IntPtr> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (!OperatingSystem.IsWindows())
            return NoScope.Instance;

        return new WindowsScope(handles);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<IntPtr> CurrentStandardHandles()
    {
        var handles = new IntPtr[StandardHandleIds.Length];
        for (int i = 0; i < StandardHandleIds.Length; i++)
            handles[i] = NativeMethods.GetStdHandle(StandardHandleIds[i]);
        return handles;
    }

    private sealed class NoScope : IDisposable
    {
        internal static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsScope : IDisposable
    {
        private readonly List<(IntPtr Handle, int Flags)> _cleared;
        private bool _disposed;

        internal WindowsScope(IReadOnlyList<IntPtr> handles)
        {
            _cleared = new List<(IntPtr, int)>(handles.Count);
            foreach (IntPtr handle in handles)
            {
                // Two standard-handle ids can name the SAME handle (a shell often gives one pipe to both
                // stdout and stderr). The second visit reads the flag this scope already cleared and skips,
                // so the handle is recorded — and restored — exactly once.
                if (!NativeMethods.GetHandleInformation(handle, out int flags))
                    continue;
                if ((flags & HandleFlagInherit) == 0)
                    continue;
                if (!NativeMethods.SetHandleInformation(handle, HandleFlagInherit, 0))
                    continue;

                _cleared.Add((handle, flags));
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach ((IntPtr handle, int flags) in _cleared)
                _ = NativeMethods.SetHandleInformation(handle, HandleFlagInherit, flags & HandleFlagInherit);
        }
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetStdHandle(int stdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetHandleInformation(IntPtr handle, out int flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);
    }
}
