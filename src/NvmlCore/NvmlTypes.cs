using System.Runtime.InteropServices;
using HectorBravo.Nvidia.Contracts;

namespace HectorBravo.Nvidia.Core;

/// <summary>
/// Result wrapper for a single NVML call: either a value (<see cref="Ok"/>)
/// or the underlying NVML return code (<see cref="ErrorCode"/>,
/// <see cref="NvmlErrorCode"/>). Missing functions / unsupported metrics
/// surface as a non-ok result so the caller degrades to N/A instead of
/// throwing.
/// </summary>
public readonly struct NvmlResult<T>
{
    public NvmlResult() { }
    public bool Ok { get; init; }
    public T? Value { get; init; }
    public int ErrorCode { get; init; }
    public string Function { get; init; } = string.Empty;

    public static NvmlResult<T> Success(T value) =>
        new() { Ok = true, Value = value, ErrorCode = (int)NvmlErrorCode.Success, Function = string.Empty };

    public static NvmlResult<T> Fail(int code, string function = "") =>
        new() { Ok = false, Value = default, ErrorCode = code, Function = function ?? string.Empty };
}

/// <summary>
/// Plain-struct mirrors of NVML types used by out-parameters. Layout matches
/// <c>nvml.h</c> 13.4.92. Inline char arrays are marshalled as fixed-length
/// <see cref="UnmanagedType.ByValArray"/> to avoid unsafe code.
/// </summary>
internal static class NvmlTypes
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] BusIdLegacy;
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] BusId;
    }

    /// <summary>nvmlProcessInfo_t == v2 (4 fields). Only the first two are read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInfo
    {
        public uint Pid;
        public ulong UsedGpuMemory;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    /// <summary>
    /// nvmlProcessUtilizationSample_t (32 bytes; the 4 bytes after the PID are
    /// implicit padding for the 64-bit timestamp). One sample per process that
    /// was active during the last driver sample period.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessUtilizationSample
    {
        public uint Pid;
        public ulong TimeStamp;
        public uint SmUtil;
        public uint MemUtil;
        public uint EncUtil;
        public uint DecUtil;
    }

    /// <summary>
    /// nvmlProcessUtilizationInfo_v1_t (40 bytes; the v0 layout plus JpgUtil and
    /// OfaUtil, trailing pad to an 8-byte boundary).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessUtilizationInfo
    {
        public ulong TimeStamp;
        public uint Pid;
        public uint SmUtil;
        public uint MemUtil;
        public uint EncUtil;
        public uint DecUtil;
        public uint JpgUtil;
        public uint OfaUtil;
    }

    /// <summary>
    /// nvmlProcessesUtilizationInfo_v1_t (24 bytes). <see cref="Version"/> must be
    /// <see cref="ProcessesUtilizationVersion"/>. The batched API is two-phase: a call
    /// with <see cref="ProcUtilArray"/> = null returns NVML_INSUFFICIENT_SIZE and fills
    /// <see cref="ProcessSamplesCount"/> with the number of elements the caller must
    /// then allocate (stride <see cref="ProcessUtilizationInfoSize"/> bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessesUtilizationInfo
    {
        public uint Version;
        public uint ProcessSamplesCount;
        public ulong LastSeenTimeStamp;
        public IntPtr ProcUtilArray;
    }

    /// <summary>Size in bytes of <see cref="ProcessUtilizationSample"/> (no unsafe sizeof).</summary>
    internal const int ProcessUtilizationSampleSize = 32;

    /// <summary>Size in bytes of <see cref="ProcessUtilizationInfo"/> (no unsafe sizeof).</summary>
    internal const int ProcessUtilizationInfoSize = 40;

    /// <summary>NVML_STRUCT_VERSION(ProcessesUtilizationInfo, 1) = 24 | (1 &lt;&lt; 24).</summary>
    internal const int ProcessesUtilizationVersion = 16777240; // 24 | (1 << 24)
}

/// <summary>Exact NVML enum values from nvml.h 13.4.92.</summary>
internal static class NvmlEnum
{
    // temperature
    public const int TemperatureGpu = 0;

    // clock types
    public const int ClockGraphics = 0;
    public const int ClockSm = 1;
    public const int ClockMem = 2;
    public const int ClockVideo = 3;

    // memory error type
    public const int MemErrCorrected = 0;
    public const int MemErrUncorrected = 1;

    // ECC counter type
    public const int EccCounterVolatile = 0;
    public const int EccCounterAggregate = 1;

    // enable state
    public const int Disabled = 0;
    public const int Enabled = 1;

    // driver model (nvml.h: WDM=0, WDDM=1, MCDM=2)
    public const int DriverWdm = 0;
    public const int DriverWddm = 1;
    public const int DriverMcDm = 2;
}

/// <summary>NVML clock-throttle-reason bitmask -> human label map (nvml.h).</summary>
internal static class NvmlThrottle
{
    public static readonly (long Bit, string Label)[] Reasons =
    {
        (0x1, "GPU Idle"),
        (0x2, "Applications Clocks Setting"),
        (0x4, "SW Power Cap"),
        (0x8, "HW Slowdown"),
        (0x10, "Sync Boost"),
        (0x20, "SW Thermal Slowdown"),
        (0x40, "HW Thermal Slowdown"),
        (0x80, "HW Power Brake Slowdown"),
        (0x100, "Display Clock Setting"),
        (0x200, "Board Limit"),
        (0x400, "Reliability"),
    };

    public static IReadOnlyList<string> Decode(long mask)
    {
        if (mask == 0)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var (bit, label) in Reasons)
        {
            if ((mask & bit) != 0)
            {
                list.Add(label);
            }
        }

        return list;
    }
}
