// Typed NVML delegate signatures. Every out/value parameter uses IntPtr so the
// delegates are 100% safe (no AllowUnsafeBlocks, no raw pointers). x64 Windows
// defaults to the C calling convention, which matches NVML. The loader resolves
// these by symbol name at runtime via Marshal.GetDelegateForFunctionPointer.
namespace HectorBravo.Nvidia.Core;

internal static class Nvml
{
    public delegate int NvmlInitV2();
    public delegate int NvmlShutdown();

    public delegate int SystemGetDriverVersion(IntPtr buffer, uint length);
    public delegate int SystemGetNvmlVersion(IntPtr buffer, uint length);
    public delegate int SystemGetCudaDriverVersion(IntPtr version);

    public delegate int DeviceGetCount(IntPtr count);
    public delegate int DeviceGetHandleByIndex(uint index, IntPtr device);

    public delegate int DeviceGetName(IntPtr device, IntPtr name, uint length);
    // Common signature for name/uuid/serial/vbios getters — one type, bound by
    // symbol name at resolve time.
    public delegate int DeviceGetString(IntPtr device, IntPtr buffer, uint length);
    public delegate int DeviceGetUuid(IntPtr device, IntPtr uuid, uint length);
    public delegate int DeviceGetSerial(IntPtr device, IntPtr serial, uint length);
    public delegate int DeviceGetVbiosVersion(IntPtr device, IntPtr version, uint length);

    public delegate int DeviceGetPciInfoV3(IntPtr device, ref NvmlTypes.PciInfo pci);

    public delegate int DeviceGetTemperature(IntPtr device, int sensorType, IntPtr temp);
    public delegate int DeviceGetUtilizationRates(IntPtr device, ref NvmlTypes.Utilization util);
    public delegate int DeviceGetPowerUsage(IntPtr device, IntPtr power);
    public delegate int DeviceGetMemoryInfo(IntPtr device, ref NvmlTypes.Memory memory);

    public delegate int DeviceGetClockInfo(IntPtr device, int clockType, IntPtr clock);
    public delegate int DeviceGetMaxClockInfo(IntPtr device, int clockType, IntPtr clock);

    public delegate int DeviceGetFanSpeed(IntPtr device, IntPtr speed);
    public delegate int DeviceGetPerformanceState(IntPtr device, IntPtr pstate);
    public delegate int DeviceGetNumGpuCores(IntPtr device, IntPtr cores);

    // Both real signatures carry TWO uint* out-params:
    //   nvmlDeviceGetEncoderUtilization(nvmlDevice_t, unsigned int *utilization, unsigned int *samplingPeriodUs)
    //   nvmlDeviceGetDecoderUtilization(nvmlDevice_t, unsigned int *utilization, unsigned int *samplingPeriodUs)
    // The second pointer MUST be a valid writable buffer or native faults (SEH,
    // uncatchable on .NET 8) — hence both are declared as IntPtr out-params here.
    public delegate int DeviceGetEncoderUtilization(IntPtr device, IntPtr utilization, IntPtr samplingPeriodUs);
    public delegate int DeviceGetDecoderUtilization(IntPtr device, IntPtr utilization, IntPtr samplingPeriodUs);
    public delegate int DeviceGetEnforcedPowerLimit(IntPtr device, IntPtr limit);
    public delegate int DeviceGetDefaultPowerLimit(IntPtr device, IntPtr limit);
    public delegate int DeviceGetMinPowerLimit(IntPtr device, IntPtr limit);
    public delegate int DeviceGetMaxPowerLimit(IntPtr device, IntPtr limit);
    public delegate int DeviceGetNvLinkSupportedCount(IntPtr device, IntPtr count);

    public delegate int DeviceGetCurrPcieLinkGeneration(IntPtr device, IntPtr gen);
    public delegate int DeviceGetMaxPcieLinkGeneration(IntPtr device, IntPtr gen);
    public delegate int DeviceGetCurrPcieLinkWidth(IntPtr device, IntPtr width);
    public delegate int DeviceGetMaxPcieLinkWidth(IntPtr device, IntPtr width);

    public delegate int DeviceGetDriverModel(IntPtr device, IntPtr current, IntPtr pending);

    public delegate int DeviceGetTotalEccErrors(IntPtr device, int errorType, int counterType, IntPtr counts);
    public delegate int DeviceGetEccMode(IntPtr device, IntPtr current, IntPtr pending);

    public delegate int DeviceGetNvLinkState(IntPtr device, uint link, IntPtr isActive);

    public delegate int DeviceGetCurrentClocksThrottleReasons(IntPtr device, IntPtr reasons);

    // Running-process list APIs: (device, count*, infos*). Both the compute and
    // graphics variants share this exact signature, so a single delegate type is
    // bound to whichever symbol resolves. The first call with a null buffer
    // returns the element count via NVML_INSUFFICIENT_SIZE; the second fills it.
    public delegate int DeviceGetRunningProcesses(IntPtr device, IntPtr count, IntPtr infos);

    // Per-process utilization for the last driver sample period. The batched API
    // (newer drivers) fills a caller-allocated array of v1 info structs through a
    // versioned container struct; the per-process variant is the primary path (both are
    // two-phase: NULL probe → INSUFFICIENT_SIZE → allocate → fill). See
    // NvmlCoreService.ReadSmUtilization. Not supported on MIG-enabled GPUs.
    public delegate int DeviceGetProcessUtilization(IntPtr device, IntPtr samples, IntPtr count, ulong lastSeenTimestamp);
    public delegate int DeviceGetProcessesUtilizationInfo(IntPtr device, ref NvmlTypes.ProcessesUtilizationInfo info);
}
