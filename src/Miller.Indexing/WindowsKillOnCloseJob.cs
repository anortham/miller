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
///
/// <para><b>The residual creation window.</b> <see cref="Process.Start()"/> gives no way to create a child
/// suspended, so the child is already running when <c>AssignProcessToJobObject</c> lands - a grandchild spawned
/// in between would not be a job member. The window is the tens of microseconds between <c>CreateProcessW</c>
/// returning and the next managed call, during which the child has not finished mapping its own imports, so no
/// real program reaches <c>CreateProcess</c> inside it. Closing it completely needs <c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>
/// on a hand-rolled <c>CreateProcess</c>, which would mean giving up <see cref="Process"/>'s pipe plumbing and
/// exit handling on all four spawn paths. What IS reachable is an assign that quietly did not take effect, so
/// <see cref="Attach"/> PROVES membership with <c>IsProcessInJob</c> rather than inferring it from a success
/// return, and <see cref="Dispose"/> terminates the job explicitly instead of relying on being the last handle.</para>
/// </summary>
internal sealed partial class WindowsKillOnCloseJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    /// <summary>Exit code stamped on job members killed by <see cref="Dispose"/>. Distinct from any real one.</summary>
    private const uint ContainmentExitCode = 0x4D4C4C43; // "MLLC"

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

            // Prove membership rather than infer it. A success return from AssignProcessToJobObject is not the
            // same claim: the child may already belong to a job that refuses nesting, and the caller would then
            // hold a job object with no members while believing the tree is contained - the exact silent
            // degradation this type exists to prevent.
            if (!NativeMethods.IsProcessInJob(process.SafeHandle, handle, out int isMember))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            // A child that exited between the assign and the check leaves the job legitimately empty. That is
            // not a containment failure - there is nothing left to contain - so it must not be reported as one.
            if (isMember == 0 && !HasExited(process))
            {
                job.Dispose();
                return WindowsKillOnCloseJobAttachment.Failed(
                    "the process is not a member of the job object after a successful assignment");
            }

            return WindowsKillOnCloseJobAttachment.Attached(job);
        }
        catch (Win32Exception ex)
        {
            job.Dispose();
            return WindowsKillOnCloseJobAttachment.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Kill every member, then close the handle. <c>KILL_ON_JOB_CLOSE</c> already kills when the LAST handle
    /// closes, so this is that same outcome made explicit: it no longer depends on this being the only handle in
    /// existence, and it happens at a point in the code rather than whenever the runtime gets round to the close.
    /// </summary>
    public void Dispose()
    {
        if (!_handle.IsInvalid && !_handle.IsClosed)
        {
            // Best-effort, and the result is genuinely uninteresting: a job whose members have all exited refuses
            // the call, and the handle close below still carries the kill-on-close limit either way.
            _ = NativeMethods.TerminateJobObject(_handle, ContainmentExitCode);
        }

        _handle.Dispose();
    }

    /// <summary><see cref="Process.HasExited"/> without the throw, for deciding whether containment still matters.</summary>
    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
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

        // `isMember` is a Win32 BOOL out-parameter. It is declared as int, not bool, because LibraryImport marshals
        // blittable types only and BOOL is a 4-byte int on the native side.
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsProcessInJob(
            SafeProcessHandle process,
            SafeFileHandle job,
            out int isMember);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
