using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Miller.Indexing.Semantic;

internal sealed record WindowsBrokerJobAttachment(
    WindowsBrokerJob? Job,
    bool IsAttached,
    string? FailureReason)
{
    public static WindowsBrokerJobAttachment NotRequired { get; } = new(null, false, null);

    public static WindowsBrokerJobAttachment Attached(WindowsBrokerJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new(job, true, null);
    }

    public static WindowsBrokerJobAttachment Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(null, false, reason);
    }
}

internal sealed partial class WindowsBrokerJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly SafeFileHandle _handle;

    private WindowsBrokerJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static WindowsBrokerJobAttachment Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return WindowsBrokerJobAttachment.NotRequired;
        }

        SafeFileHandle handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            return WindowsBrokerJobAttachment.Failed(
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        var job = new WindowsBrokerJob(handle);
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

            return WindowsBrokerJobAttachment.Attached(job);
        }
        catch (Win32Exception ex)
        {
            job.Dispose();
            return WindowsBrokerJobAttachment.Failed(ex.Message);
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
