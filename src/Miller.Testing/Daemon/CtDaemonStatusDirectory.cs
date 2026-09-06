using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Miller.Testing;

internal static class CtDaemonStatusDirectory
{
    private const uint DefaultUnixDirectoryMode = 0x1FF;
    private const int UnixAlreadyExists = 17;
    private const int WindowsAlreadyExists = 183;

    internal static void Ensure(string workspaceRoot)
    {
        string root = Path.GetFullPath(workspaceRoot);
        CreateSingleDirectory(Path.Combine(root, CtDaemonProtocol.MillerDirectoryName));
        CreateSingleDirectory(CtDaemonProtocol.RootDirectory(root));
    }

    private static void CreateSingleDirectory(string path)
    {
        bool windows = OperatingSystem.IsWindows();
        bool created = windows
            ? CreateDirectoryWindows(WindowsExtendedPath(path), IntPtr.Zero)
            : CreateDirectoryUnix(path, DefaultUnixDirectoryMode) == 0;
        if (created)
            return;

        int error = Marshal.GetLastPInvokeError();
        if (error == (windows ? WindowsAlreadyExists : UnixAlreadyExists) && Directory.Exists(path))
            return;

        var cause = new Win32Exception(error);
        if (error == 2 || (windows && error == 3))
            throw new DirectoryNotFoundException($"The status directory parent disappeared: {path}", cause);
        if (error == (windows ? 5 : 13))
            throw new UnauthorizedAccessException($"Cannot create the status directory: {path}", cause);
        throw new IOException($"Cannot create the status directory: {path}", cause);
    }

    internal static string WindowsExtendedPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWindows(string path, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int CreateDirectoryUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
