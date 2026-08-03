using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Miller.Indexing;

/// <summary>
/// The outcome of putting a child process in a job object that kills its members when the handle closes.
/// <see cref="NotRequired"/> off Windows, where the caller has a portable mechanism instead: the semantic
/// broker is reference-counted, and julie-extract self-terminates on <c>--parent-pid</c>.
/// </summary>
internal sealed record WindowsKillOnCloseJobAttachment(
    WindowsKillOnCloseJob? Job,
    bool IsAttached,
    string? FailureReason)
{
    public static WindowsKillOnCloseJobAttachment NotRequired { get; } = new(null, false, null);

    public static WindowsKillOnCloseJobAttachment Attached(WindowsKillOnCloseJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job, true, null);
    }

    public static WindowsKillOnCloseJobAttachment Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(null, false, reason);
    }
}

/// <summary>
/// A Windows job object carrying <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: every process assigned to it dies
/// when the last handle closes, including when the owning process is killed outright. That handle-close is the
/// point — it is the only Windows mechanism that survives a <c>kill -9</c> equivalent of the parent, which is
/// exactly the case a cooperative watchdog cannot cover.
///
/// <para>Shared by the two subsystems that spawn long-lived children: the semantic broker, and every
/// <c>julie-extract</c> scan (whose <c>--parent-pid</c> watchdog is Unix-only, because <c>std</c> exposes no
/// Windows counterpart for <c>parent_id</c>).</para>
///
/// <para>Best-effort everywhere: a failure to create, configure, or assign returns a reason rather than
/// throwing, because containment hygiene must never break the work it was protecting.</para>
/// </summary>
internal sealed partial class WindowsKillOnCloseJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly SafeFileHandle _handle;

    private WindowsKillOnCloseJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static WindowsKillOnCloseJobAttachment Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return WindowsKillOnCloseJobAttachment.NotRequired;
        }

        SafeFileHandle handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            return WindowsKillOnCloseJobAttachment.Failed(
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        var job = new WindowsKillOnCloseJob(handle);
        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, buffer, false);
                if (!NativeMethods.SetInformationJobObject(
                        handle,
                        JobObjectExtendedLimitInformationClass,
                        buffer,
                        (uint)size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (!NativeMethods.AssignProcessToJobObject(handle, process.SafeHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return WindowsKillOnCloseJobAttachment.Attached(job);
        }
        catch (Win32Exception ex)
        {
            job.Dispose();
            return WindowsKillOnCloseJobAttachment.Failed(ex.Message);
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AssignProcessToJobObject(
            SafeFileHandle job,
            SafeProcessHandle process);
    }
}
