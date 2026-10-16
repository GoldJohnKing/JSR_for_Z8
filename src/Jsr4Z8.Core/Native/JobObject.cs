using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jsr4Z8.Core.Native;

/// <summary>
/// Crash net: a Windows Job Object created with
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE that the app process joins at startup.
/// Every descendant (jsr_backend.exe, its self-respawned child and any unnamed
/// grandchild) inherits the membership, so the OS terminates the whole tree the
/// moment our last job handle closes — which happens on process death, whatever
/// the failure mode. Normal cancellation still goes through the logged
/// "taskkill /T /F" tree kill (BackendRunner); this job is purely the
/// abnormal-exit safety net. Windows 8+ nested jobs make assigning ourselves
/// safe even when a launcher already holds us in its own job.
/// </summary>
public static class JobObject
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    /// <summary>App-lifetime job handle. MUST be a static field: a job held only
    /// by a local SafeHandle would be finalized early, and its premature close
    /// would kill-on-close the whole process tree mid-run.</summary>
    private static SafeFileHandle? _job;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle hJob, int jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, int cbJobObjectInfoLength);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        // Field order per winnt.h (verified against the installed Windows SDK
        // 10.0.28000.0 header): LimitFlags is the THIRD field and the working-set
        // fields are SIZE_T. The widely-copied pinvoke.net layout (DWORD working
        // sets, LimitFlags last) is 8 bytes short on x64 and silently writes the
        // flags into PriorityClass — SetInformationJobObject then either rejects
        // the size (ERROR_BAD_LENGTH 24) or arms nothing. Marshal inserts the
        // inter-field padding; SizeOf == 144 on x64.
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary>
    /// Arms the crash net for the CURRENT process: creates the kill-on-close job
    /// (see <see cref="TryCreateKillOnCloseJob"/>), assigns this process to it and
    /// retains the job handle for the app's lifetime. Best-effort hardening —
    /// returns false silently on any failure (missing rights, exotic job policy)
    /// and never blocks startup.
    /// </summary>
    public static bool TryEnableKillOnCloseForCurrentProcess()
    {
        try
        {
            if (TryCreateKillOnCloseJob() is null)
                return false;
            if (TryAssign(Process.GetCurrentProcess()))
                return true;
            // Assignment failed: release the handle instead of keeping an empty job.
            _job?.Dispose();
            _job = null;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a kill-on-close job object, retains it in the static field and
    /// returns the handle so tests can control its lifetime (dispose → members
    /// die). Test seam: assign a disposable child via <see cref="TryAssign"/>,
    /// never the testhost itself. Returns null when creation or limit
    /// configuration fails.
    /// </summary>
    internal static SafeHandle? TryCreateKillOnCloseJob()
    {
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
            return null;

        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            job.Dispose();
            return null;
        }

        _job = job;
        return job;
    }

    /// <summary>Assigns <paramref name="process"/> to the job created by the most
    /// recent <see cref="TryCreateKillOnCloseJob"/> call. Returns false on any
    /// failure (no job yet, handle closed, native call refused).</summary>
    internal static bool TryAssign(Process process)
    {
        try
        {
            SafeFileHandle? job = _job;
            if (job is null || job.IsClosed || job.IsInvalid)
                return false;
            return AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            return false;
        }
    }
}
