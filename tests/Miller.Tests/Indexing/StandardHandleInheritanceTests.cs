using System.Runtime.InteropServices;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Proves the clear-and-restore behaviour of <see cref="StandardHandleInheritance"/> against handles the
/// test creates.
///
/// <para><b>Why not test the parameterless overload.</b> A test runner normally hands the test process
/// standard handles whose inheritable flag is ALREADY clear. A test written against
/// <c>SuppressForSpawn()</c> therefore asserts <c>0 == 0</c> and passes just as happily with the guard
/// removed — measured: an earlier version of this test stayed green after the production call was commented
/// out. The explicit-handle overload exists so the assertions have something real to observe.</para>
/// </summary>
public sealed class StandardHandleInheritanceTests : IDisposable
{
    private const int HandleFlagInherit = 1;
    private const int StdOutputHandle = -11;

    private readonly string _directory;

    public StandardHandleInheritanceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "miller-handle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void SuppressForSpawn_ClearsAnInheritableHandle_AndRestoresItOnDispose()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Handle inheritance is a Windows concern.");

        using FileStream file = OpenLog("inheritable.log");
        IntPtr handle = file.SafeFileHandle.DangerousGetHandle();
        Assert.True(Native.SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit));
        Assert.Equal(HandleFlagInherit, ReadInheritFlag(handle));

        using (StandardHandleInheritance.SuppressForSpawn([handle]))
        {
            Assert.Equal(0, ReadInheritFlag(handle));
        }

        Assert.Equal(HandleFlagInherit, ReadInheritFlag(handle));
    }

    [Fact]
    public void SuppressForSpawn_LeavesAHandleThatWasNotInheritable_Untouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Handle inheritance is a Windows concern.");

        using FileStream file = OpenLog("plain.log");
        IntPtr handle = file.SafeFileHandle.DangerousGetHandle();
        Assert.True(Native.SetHandleInformation(handle, HandleFlagInherit, 0));

        using (StandardHandleInheritance.SuppressForSpawn([handle]))
        {
            Assert.Equal(0, ReadInheritFlag(handle));
        }

        // Restoring must not GRANT inheritance to a handle that never had it.
        Assert.Equal(0, ReadInheritFlag(handle));
    }

    [Fact]
    public void SuppressForSpawn_ClearsEveryInheritableHandleInTheList()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Handle inheritance is a Windows concern.");

        using FileStream first = OpenLog("first.log");
        using FileStream second = OpenLog("second.log");
        IntPtr a = first.SafeFileHandle.DangerousGetHandle();
        IntPtr b = second.SafeFileHandle.DangerousGetHandle();
        Assert.True(Native.SetHandleInformation(a, HandleFlagInherit, HandleFlagInherit));
        Assert.True(Native.SetHandleInformation(b, HandleFlagInherit, HandleFlagInherit));

        // The same handle listed twice stands in for a shell that gave one pipe to both stdout and stderr.
        using (StandardHandleInheritance.SuppressForSpawn([a, b, a]))
        {
            Assert.Equal(0, ReadInheritFlag(a));
            Assert.Equal(0, ReadInheritFlag(b));
        }

        Assert.Equal(HandleFlagInherit, ReadInheritFlag(a));
        Assert.Equal(HandleFlagInherit, ReadInheritFlag(b));
    }

    [Fact]
    public void SuppressForSpawn_ToleratesAHandleTheOsWillNotReport()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Handle inheritance is a Windows concern.");

        // A spawn must still happen when a standard handle is absent — a detached process has no console,
        // and GetStdHandle answers with a handle nothing can report on. Failing here would refuse to start
        // the daemon over a diagnostic detail.
        using (StandardHandleInheritance.SuppressForSpawn([IntPtr.Zero, new IntPtr(-1)]))
        {
        }
    }

    [Fact]
    public void SuppressForSpawn_RejectsANullHandleList()
    {
        Assert.Throws<ArgumentNullException>(() => StandardHandleInheritance.SuppressForSpawn(handles: null!));
    }

    [Fact]
    public void SuppressForSpawn_OverStandardHandles_IsSafeToTakeAndRelease()
    {
        // The parameterless overload cannot assert a cleared flag (see the class remarks), but it must not
        // throw and must leave the process's own handles exactly as it found them. A runner that gives the
        // test process an unreportable stdout is not a failure here — that path is covered by
        // SuppressForSpawn_ToleratesAHandleTheOsWillNotReport.
        if (!OperatingSystem.IsWindows())
        {
            using (StandardHandleInheritance.SuppressForSpawn())
            {
            }

            return;
        }

        IntPtr stdout = Native.GetStdHandle(StdOutputHandle);
        bool readable = Native.GetHandleInformation(stdout, out int before);

        using (StandardHandleInheritance.SuppressForSpawn())
        {
        }

        if (!readable)
            return;

        Assert.True(Native.GetHandleInformation(stdout, out int after));
        Assert.Equal(before & HandleFlagInherit, after & HandleFlagInherit);
    }

    private FileStream OpenLog(string name) =>
        new(Path.Combine(_directory, name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    private static int ReadInheritFlag(IntPtr handle)
    {
        Assert.True(Native.GetHandleInformation(handle, out int flags));
        return flags & HandleFlagInherit;
    }

    private static class Native
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
