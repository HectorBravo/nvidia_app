using System;
using System.Runtime.InteropServices;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// Reads the machine's physical memory usage straight from the OS (kernel32
/// GlobalMemoryStatusEx). Independent of NVML / any graphics card, so it feeds
/// the System RAM chart even when no GPU is present.
/// </summary>
internal static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;      // percent of physical memory in use (0..100)
        public ulong ullTotalPhys;     // total physical memory, bytes
        public ulong ullAvailPhys;     // available physical memory, bytes
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// Returns (usedBytes, totalBytes) of physical RAM, or (0, 0) when the OS call fails.
    /// "Used" is total minus available — the same figure Task Manager reports as In Use.
    /// </summary>
    public static (long used, long total) Read()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            return (0, 0);

        ulong used = status.ullTotalPhys - status.ullAvailPhys;
        // Physical RAM fits comfortably in a signed long on any real machine; clamp anyway.
        long total = (long)Math.Min(status.ullTotalPhys, long.MaxValue);
        long usedClamped = (long)Math.Min(used, long.MaxValue);
        return (usedClamped, total);
    }
}
