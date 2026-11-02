using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AnimeJaNai.Addons;

// Job objects enforce resource/lifetime limits, not file/network permissions.
// Wasmtime's guest capability boundary provides those restrictions.
internal sealed class WindowsJob : IDisposable
{
    private readonly SafeFileHandle handle;
    public WindowsJob(nuint processMemory = 512u * 1024 * 1024, nuint jobMemory = 768u * 1024 * 1024, uint cpuRate = 2500)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Worker supervision currently supports Windows.");
        handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var info = new ExtendedLimits
        {
            Basic = new BasicLimits { Flags = 0x2000 | 0x100 | 0x200 | 0x8, ActiveProcesses = 2 },
            ProcessMemory = processMemory,
            JobMemory = jobMemory,
        };
        var cpu = new CpuLimits { Flags = 0x1 | 0x4, Rate = cpuRate };
        try
        {
            if (!SetLimits(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
                (cpuRate > 0 && !SetCpu(handle, 15, ref cpu, (uint)Marshal.SizeOf<CpuLimits>())))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { handle.Dispose(); throw; }
    }

    public void Attach(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Terminate()
    {
        if (!TerminateJobObject(handle, 1)) handle.Dispose();
    }

    public async Task WaitForEmptyAsync()
    {
        // The bridge exiting does not imply its Wasmtime child has exited.
        // Keep the job open until both have released their working directory.
        while (!handle.IsClosed)
        {
            if (!QueryAccounting(handle, 1, out var info, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (info.ActiveProcesses == 0) return;
            await Task.Delay(10);
        }
    }
    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct CpuLimits { public uint Flags, Rate; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLimits(SafeFileHandle job, int informationClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCpu(SafeFileHandle job, int informationClass, ref CpuLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryAccounting(SafeFileHandle job, int informationClass, out Accounting info, uint length, IntPtr returnedLength);
}
