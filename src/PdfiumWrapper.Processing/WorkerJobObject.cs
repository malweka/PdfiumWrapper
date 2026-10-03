using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PdfiumWrapper.Processing;

/// <summary>
/// Ties worker processes to the coordinator's lifetime on Windows. Every worker is put in one job
/// object, created with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> and never closed, so Windows kills
/// the workers when the coordinator process ends for any reason, a crash included. On Linux and
/// macOS a worker ends on its own when its standard input closes (see <see cref="PdfWorkerHost"/>),
/// which is also the fallback on Windows when a worker cannot be put in the job.
/// </summary>
internal static partial class WorkerJobObject
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private static readonly Lazy<nint> s_job = new(Create);

    /// <summary>Puts the worker in the job. Best effort: a worker that has already exited, or a host that forbids nested jobs, is left out.</summary>
    public static void Assign(Process process)
    {
        if (!OperatingSystem.IsWindows() || s_job.Value == 0)
            return;
        try
        {
            AssignProcessToJobObject(s_job.Value, process.Handle);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>True when the process is in the workers' job; for tests.</summary>
    internal static bool Contains(Process process)
        => OperatingSystem.IsWindows() && s_job.Value != 0
           && IsProcessInJob(process.Handle, s_job.Value, out bool result) && result;

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows())
            return 0;

        nint job = CreateJobObjectW(0, null);
        if (job == 0)
            return 0;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(job);
            return 0;
        }

        // Never closed: the handle closes when this process ends, and that is what kills the workers.
        return job;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
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
}
