using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileAssistant.IndexBench;

/// <summary>Caps the private committed memory of the replay receiver and its child together. Does not limit the desktop.</summary>
internal sealed class BenchmarkMemoryLimit : IDisposable
{
    private readonly SafeFileHandle job;

    public BenchmarkMemoryLimit(int mib)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mib);
        job = CreateJobObjectW(0, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var info = new ExtendedLimits { JobMemoryLimit = checked((nuint)mib * 1024 * 1024) };
            info.Basic.Flags = 0x200; // JOB_OBJECT_LIMIT_JOB_MEMORY
            if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
                !AssignProcessToJobObject(job, GetCurrentProcess()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { job.Dispose(); throw; }
    }

    public void VerifyChild(Process child)
    {
        if (!IsProcessInJob(child.Handle, job, out var included) || !included)
            throw new InvalidOperationException("回放发送端没有进入同一个内存限制组。");
    }

    public long PeakBytes()
    {
        if (!QueryInformationJobObject(job, 9, out var info, (uint)Marshal.SizeOf<ExtendedLimits>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (long)info.PeakJobMemory;
    }
    public void Dispose() => job.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out ExtendedLimits info, uint size, out uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(nint process, SafeFileHandle job, out bool result);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
