using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RigControl.Core.Util;

/// <summary>
/// Ties helper programs to RigControl with a Windows job object: if RigControl ends in any way (including being
/// ended from Task Manager), Windows ends every program attached here too, so none is left running on its own.
/// </summary>
public static class ChildProcesses
{
    private static readonly object Gate = new();
    private static IntPtr _job;

    /// <summary>Attaches a started process. Returns false if Windows refused (the process then still runs normally).</summary>
    public static bool Attach(Process process)
    {
        // Job objects are a Windows thing. Elsewhere the helper simply runs on its own, as the summary says.
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            lock (Gate)
            {
                if (_job == IntPtr.Zero)
                {
                    var job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) return false;
                    var info = new JobExtendedLimitInformation { BasicLimitInformation = { LimitFlags = 0x2000 /* end everything when the job closes */ } };
                    var size = Marshal.SizeOf<JobExtendedLimitInformation>();
                    var ptr = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(info, ptr, false);
                        if (!SetInformationJobObject(job, 9, ptr, (uint)size))
                        {
                            CloseHandle(job);
                            return false;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                    _job = job; // kept open for the life of RigControl; Windows closes it when RigControl ends
                }
                return AssignProcessToJobObject(_job, process.Handle);
            }
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
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
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
