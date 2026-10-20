using System.Runtime.InteropServices;
using System.Text;
using HectorBravo.Nvidia.Contracts;

namespace HectorBravo.Nvidia.Core;

/// <summary>
/// NVML-backed <see cref="IGpuMonitorService"/>. Loads nvml.dll at runtime
/// (no compile-time dependency), resolves each entry point by symbol name, and
/// performs one full polling pass per <see cref="RefreshAsync"/>. Degrades
/// gracefully: a missing library, driver, or GPU yields an empty snapshot with a
/// friendly <see cref="StatusMessage"/> rather than an exception, so the UI can
/// show a fallback screen.
/// </summary>
public sealed class NvmlCoreService : IGpuMonitorService
{
    private readonly NvmlLoader _loader = new();
    private readonly AppLog? _log;

    private bool _initialized;
    private IntPtr[] _devices = Array.Empty<IntPtr>();
    private Nvml.NvmlShutdown? _shutdown;

    public NvmlCoreService(AppLog? log = null)
    {
        _log = log;
    }

    public bool IsAvailable { get; private set; }
    public string StatusMessage { get; private set; } = "Not initialized.";

    public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return Task.FromResult(IsAvailable);
        }

        if (!_loader.TryLoad(msg => _log?.Info(msg)))
        {
            StatusMessage = "NVML library (nvml.dll) not found. Install a recent NVIDIA driver.";
            IsAvailable = false;
            return Task.FromResult(false);
        }

        var init = Resolve<Nvml.NvmlInitV2>("nvmlInit_v2", "nvmlInit");
        if (init == null)
        {
            StatusMessage = "nvmlInit_v2 not found in nvml.dll.";
            IsAvailable = false;
            return Task.FromResult(false);
        }

        var rc = Safe(init, "nvmlInit", () => init());
        if (rc != NvmlErr.Success)
        {
            StatusMessage = $"NVML init failed: {NvmlErr.Describe(rc)}";
            _log?.Warning(StatusMessage);
            IsAvailable = false;
            return Task.FromResult(false);
        }

        _initialized = true;

        var countFn = Resolve<Nvml.DeviceGetCount>("nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
        if (countFn == null)
        {
            StatusMessage = "nvmlDeviceGetCount not found in nvml.dll.";
            IsAvailable = false;
            return Task.FromResult(false);
        }

        var gpuCount = 0;
        var countPtr = Marshal.AllocHGlobal(4);
        try
        {
            if (Safe(countFn, "nvmlDeviceGetCount", () => countFn(countPtr)) == NvmlErr.Success)
            {
                gpuCount = Marshal.ReadInt32(countPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(countPtr);
        }

        var handleFn = Resolve<Nvml.DeviceGetHandleByIndex>("nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
        var devices = new IntPtr[gpuCount];
        for (var i = 0; i < gpuCount; i++)
        {
            if (handleFn == null)
            {
                break;
            }

            var hPtr = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                // signature: (uint index, out device handle)
                if (Safe(handleFn, "nvmlDeviceGetHandleByIndex", () => handleFn((uint)i, hPtr)) == NvmlErr.Success)
                {
                    devices[i] = Marshal.ReadIntPtr(hPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(hPtr);
            }
        }

        _devices = devices;
        IsAvailable = gpuCount > 0;
        StatusMessage = gpuCount > 0
            ? $"OK — {gpuCount} GPU(s). NVML loaded from {_loader.LoadedPath}."
            : "No NVIDIA GPU detected by NVML.";
        _log?.Info(StatusMessage);
        return Task.FromResult(IsAvailable);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var shutdown = _shutdown ?? Resolve<Nvml.NvmlShutdown>("nvmlShutdown");
        _shutdown = shutdown;

        if (shutdown != null)
        {
            try
            {
                _ = shutdown();
            }
            catch
            {
                // best-effort shutdown
            }
        }

        _initialized = false;
        _devices = Array.Empty<IntPtr>();
        _loader.Unload(msg => _log?.Info(msg));
        IsAvailable = false;
        StatusMessage = "Shut down.";
        return Task.CompletedTask;
    }

    public Task<SystemSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
        {
            return Task.FromResult(new SystemSnapshot
            {
                NvmlVersion = string.Empty,
                DriverVersion = string.Empty,
                CudaDriverVersion = string.Empty,
            });
        }

        var driverVer = ReadDriverVersion();
        var nvmlVer = ReadNvmlVersion();
        var cudaVer = ReadCudaVersion();

        var gpus = new List<GpuSnapshot>();
        var allProcesses = new List<GpuProcessInfo>();

        for (var i = 0; i < _devices.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gpu = ReadGpu(_devices[i], i, allProcesses);
            gpus.Add(gpu);
        }

        return Task.FromResult(new SystemSnapshot
        {
            NvmlVersion = nvmlVer,
            DriverVersion = driverVer,
            CudaDriverVersion = cudaVer,
            Gpus = gpus,
            Processes = allProcesses,
            Timestamp = DateTimeOffset.Now,
        });
    }

    // ── delegate plumbing ─────────────────────────────────────────
    private T? Resolve<T>(params string[] candidates) where T : Delegate
        => (T?)_loader.Get(typeof(T), candidates);

    private static int Safe<T>(T? del, string name, Func<int> invoke) where T : Delegate
    {
        if (del == null)
        {
            return NvmlErr.FunctionNotFound;
        }

        try
        {
            return invoke();
        }
        catch
        {
            return NvmlErr.FunctionNotFound;
        }
    }

    // ── typed readers (IntPtr out-params) ─────────────────────────
    private static string ReadString(IntPtr p)
    {
        if (p == IntPtr.Zero)
        {
            return string.Empty;
        }

        var len = 0;
        while (Marshal.ReadByte(p, len) != 0)
        {
            len++;
        }

        var bytes = new byte[len];
        Marshal.Copy(p, bytes, 0, len);
        return Encoding.UTF8.GetString(bytes);
    }

    private string GetStr(IntPtr dev, params string[] symbols)
    {
        var fn = Resolve<Nvml.DeviceGetString>(symbols);
        if (fn == null)
        {
            return string.Empty;
        }

        var buf = Marshal.AllocHGlobal(256);
        try
        {
            var rc = Safe(fn, symbols[0], () => fn(dev, buf, 256));
            return rc == NvmlErr.Success ? ReadString(buf) : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // Returns -1 when the metric is unavailable (missing symbol or NVML error).
    // -1 is an "unknown" sentinel so a legitimate 0 (e.g. an idle GPU reporting
    // 0% utilization) is preserved and can be rendered as 0, not N/A.
    private int GetU32<T>(IntPtr dev, T? fn, Func<T, IntPtr, int> call) where T : Delegate
    {
        if (fn == null)
        {
            return -1;
        }

        var p = Marshal.AllocHGlobal(4);
        try
        {
            var rc = Safe(fn, string.Empty, () => call(fn, p));
            return rc == NvmlErr.Success ? Marshal.ReadInt32(p) : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    // Variant for NVML getters that have TWO uint* out-params (e.g.
    // nvmlDeviceGetEncoder/DecoderUtilization: *utilization, *samplingPeriodUs).
    // Both buffers are allocated; the first holds the value we keep, the second
    // is a valid write target so native never faults on a garbage pointer.
    private int GetU32Two<T>(T? fn, Func<T, IntPtr, IntPtr, int> call) where T : Delegate
    {
        if (fn == null)
        {
            return -1;
        }

        var p1 = Marshal.AllocHGlobal(4);
        var p2 = Marshal.AllocHGlobal(4);
        try
        {
            var rc = Safe(fn, string.Empty, () => call(fn, p1, p2));
            return rc == NvmlErr.Success ? Marshal.ReadInt32(p1) : -1;
        }
        finally
        {
            Marshal.FreeHGlobal(p1);
            Marshal.FreeHGlobal(p2);
        }
    }

    private long GetU64<T>(IntPtr dev, T? fn, Func<T, IntPtr, int> call) where T : Delegate
    {
        if (fn == null)
        {
            return 0;
        }

        var p = Marshal.AllocHGlobal(8);
        try
        {
            var rc = Safe(fn, string.Empty, () => call(fn, p));
            return rc == NvmlErr.Success ? Marshal.ReadInt64(p) : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private string ReadDriverVersion()
    {
        var fn = Resolve<Nvml.SystemGetDriverVersion>("nvmlSystemGetDriverVersion");
        if (fn == null)
        {
            return string.Empty;
        }

        var buf = Marshal.AllocHGlobal(80);
        try
        {
            var rc = Safe(fn, "nvmlSystemGetDriverVersion", () => fn(buf, 80));
            return rc == NvmlErr.Success ? ReadString(buf) : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private string ReadNvmlVersion()
    {
        var fn = Resolve<Nvml.SystemGetNvmlVersion>("nvmlSystemGetNVMLVersion");
        if (fn == null)
        {
            return string.Empty;
        }

        var buf = Marshal.AllocHGlobal(80);
        try
        {
            var rc = Safe(fn, "nvmlSystemGetNvmlVersion", () => fn(buf, 80));
            return rc == NvmlErr.Success ? ReadString(buf) : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private string ReadCudaVersion()
    {
        var fn = Resolve<Nvml.SystemGetCudaDriverVersion>("nvmlSystemGetCudaDriverVersion");
        if (fn == null)
        {
            return string.Empty;
        }

        var p = Marshal.AllocHGlobal(4);
        try
        {
            var rc = Safe(fn, "nvmlSystemGetCudaDriverVersion", () => fn(p));
            if (rc != NvmlErr.Success)
            {
                return string.Empty;
            }

            var raw = Marshal.ReadInt32(p);
            return raw > 0 ? $"{raw / 1000}.{(raw / 10) % 100}" : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    private GpuSnapshot ReadGpu(IntPtr dev, int index, List<GpuProcessInfo> allProcesses)
    {
        // utilization (struct out)
        var util = ReadUtil(dev);
        var sm = ReadSmUtilization(dev);
        var mem = ReadMem(dev);
        var (eccCorr, eccUncorr) = ReadEcc(dev);
        var eccModes = ReadEccModes(dev);

        var procs = ReadProcesses(dev, index, GpuProcessKind.Compute, allProcesses);
        ReadProcesses(dev, index, GpuProcessKind.Graphics, allProcesses);

        var nv = ReadNvLinks(dev);
        var throttle = GetU64(dev,
            Resolve<Nvml.DeviceGetCurrentClocksThrottleReasons>("nvmlDeviceGetCurrentClocksThrottleReasons_v2", "nvmlDeviceGetCurrentClocksThrottleReasons"),
            (f, p) => f(dev, p));

        return new GpuSnapshot
        {
            Index = index,
            Name = GetStr(dev, "nvmlDeviceGetName"),
            Uuid = GetStr(dev, "nvmlDeviceGetUUID"),
            Serial = GetStr(dev, "nvmlDeviceGetSerial"),
            BusId = ReadPciId(dev),
            VbiosVersion = GetStr(dev, "nvmlDeviceGetVbiosVersion"),

            GpuUtilizationPct = (int)(util?.Gpu ?? 0),
            MemoryUtilizationPct = (int)(util?.Memory ?? 0),
            SmUtilizationPct = sm,
            EncoderUtilizationPct = GetU32Two(Resolve<Nvml.DeviceGetEncoderUtilization>("nvmlDeviceGetEncoderUtilization"), (f, p1, p2) => f(dev, p1, p2)),
            DecoderUtilizationPct = GetU32Two(Resolve<Nvml.DeviceGetDecoderUtilization>("nvmlDeviceGetDecoderUtilization"), (f, p1, p2) => f(dev, p1, p2)),

            TemperatureGpu = GetU32(dev, Resolve<Nvml.DeviceGetTemperature>("nvmlDeviceGetTemperature"), (f, p) => f(dev, NvmlEnum.TemperatureGpu, p)),

            PowerMw = GetU32(dev, Resolve<Nvml.DeviceGetPowerUsage>("nvmlDeviceGetPowerUsage"), (f, p) => f(dev, p)),
            EnforcedPowerLimitMw = GetU32(dev, Resolve<Nvml.DeviceGetEnforcedPowerLimit>("nvmlDeviceGetEnforcedPowerLimit"), (f, p) => f(dev, p)),
            DefaultPowerLimitMw = GetU32(dev, Resolve<Nvml.DeviceGetDefaultPowerLimit>("nvmlDeviceGetDefaultPowerLimit"), (f, p) => f(dev, p)),
            MinPowerLimitMw = GetU32(dev, Resolve<Nvml.DeviceGetMinPowerLimit>("nvmlDeviceGetMinPowerLimit"), (f, p) => f(dev, p)),
            MaxPowerLimitMw = GetU32(dev, Resolve<Nvml.DeviceGetMaxPowerLimit>("nvmlDeviceGetMaxPowerLimit"), (f, p) => f(dev, p)),

            MemoryTotalBytes = (long)(mem?.Total ?? 0),
            MemoryUsedBytes = (long)(mem?.Used ?? 0),
            MemoryFreeBytes = (long)(mem?.Free ?? 0),

            GpuClockMhz = GetU32(dev, Resolve<Nvml.DeviceGetClockInfo>("nvmlDeviceGetClockInfo"), (f, p) => f(dev, NvmlEnum.ClockGraphics, p)),
            MemoryClockMhz = GetU32(dev, Resolve<Nvml.DeviceGetClockInfo>("nvmlDeviceGetClockInfo"), (f, p) => f(dev, NvmlEnum.ClockMem, p)),
            VideoClockMhz = GetU32(dev, Resolve<Nvml.DeviceGetClockInfo>("nvmlDeviceGetClockInfo"), (f, p) => f(dev, NvmlEnum.ClockVideo, p)),
            MaxGpuClockMhz = GetU32(dev, Resolve<Nvml.DeviceGetClockInfo>("nvmlDeviceGetMaxClockInfo"), (f, p) => f(dev, NvmlEnum.ClockGraphics, p)),
            MaxMemoryClockMhz = GetU32(dev, Resolve<Nvml.DeviceGetClockInfo>("nvmlDeviceGetMaxClockInfo"), (f, p) => f(dev, NvmlEnum.ClockMem, p)),

            FanSpeedPct = GetU32(dev, Resolve<Nvml.DeviceGetFanSpeed>("nvmlDeviceGetFanSpeed"), (f, p) => f(dev, p)),
            PerfState = GetU32(dev, Resolve<Nvml.DeviceGetPerformanceState>("nvmlDeviceGetPerformanceState"), (f, p) => f(dev, p)),

            PcieLinkGen = Math.Max(0, GetU32(dev, Resolve<Nvml.DeviceGetCurrPcieLinkGeneration>("nvmlDeviceGetCurrPcieLinkGeneration"), (f, p) => f(dev, p))),
            PcieLinkWidth = Math.Max(0, GetU32(dev, Resolve<Nvml.DeviceGetCurrPcieLinkWidth>("nvmlDeviceGetCurrPcieLinkWidth"), (f, p) => f(dev, p))),
            PcieMaxLinkGen = Math.Max(0, GetU32(dev, Resolve<Nvml.DeviceGetMaxPcieLinkGeneration>("nvmlDeviceGetMaxPcieLinkGeneration"), (f, p) => f(dev, p))),
            PcieMaxLinkWidth = Math.Max(0, GetU32(dev, Resolve<Nvml.DeviceGetMaxPcieLinkWidth>("nvmlDeviceGetMaxPcieLinkWidth"), (f, p) => f(dev, p))),

            EccCurrentMode = eccModes.Current,
            EccPersistingMode = eccModes.Persistent,
            EccErrorsCorrected = eccCorr,
            EccErrorsUncorrected = eccUncorr,

            ThrottleReasonsRaw = throttle,
            ActiveThrottleReasons = NvmlThrottle.Decode(throttle),

            NvLinkCount = nv.Total,
            NvLinkActiveCount = nv.Active,
            NumGpuCores = GetU32(dev, Resolve<Nvml.DeviceGetNumGpuCores>("nvmlDeviceGetNumGpuCores"), (f, p) => f(dev, p)),

            ComputeProcessCount = procs.Count(p => p.Kind == GpuProcessKind.Compute),
            GraphicsProcessCount = procs.Count(p => p.Kind == GpuProcessKind.Graphics),
            Timestamp = DateTimeOffset.Now,
        };
    }

    // ── struct out-params ─────────────────────────────────────────
    private NvmlTypes.Utilization? ReadUtil(IntPtr dev)
    {
        var fn = Resolve<Nvml.DeviceGetUtilizationRates>("nvmlDeviceGetUtilizationRates");
        if (fn == null)
        {
            return null;
        }

        var u = new NvmlTypes.Utilization();
        var rc = Safe(fn, "nvmlDeviceGetUtilizationRates", () => fn(dev, ref u));
        return rc == NvmlErr.Success ? u : null;
    }

    private NvmlTypes.Memory? ReadMem(IntPtr dev)
    {
        var fn = Resolve<Nvml.DeviceGetMemoryInfo>("nvmlDeviceGetMemoryInfo");
        if (fn == null)
        {
            return null;
        }

        var m = new NvmlTypes.Memory();
        var rc = Safe(fn, "nvmlDeviceGetMemoryInfo", () => fn(dev, ref m));
        return rc == NvmlErr.Success ? m : null;
    }

    // ── per-process SM utilization (the `sm` column of nvidia-smi dmon) ─────
    // NVML has no device-level "SM utilization" metric: dmon's number is the SUM of
    // the per-process SM-utilization values for the last driver sample period, and
    // only processes that actually used the GPU during that period are reported.
    // The per-process API (nvmlDeviceGetProcessUtilization) is the primary path:
    // every driver that ships it honours the two-phase protocol, while the batched
    // nvmlDeviceGetProcessesUtilizationInfo is kept only as the fallback for a
    // driver that lacks that symbol (on several drivers the batched call also
    // rejects the container version with ARGUMENT_VERSION_MISMATCH). Everything
    // that cannot be read
    // MIG — both APIs return NOT_SUPPORTED there, missing export) degrades to 0,
    // matching this engine's "one bad read never aborts the snapshot" rule.
    // The raw sum can exceed 100 with several concurrent processes, but the
    // user-facing value is a percentage and must never: clamp at 100.
    private int ReadSmUtilization(IntPtr dev)
    {
        var legacy = Resolve<Nvml.DeviceGetProcessUtilization>("nvmlDeviceGetProcessUtilization");
        if (legacy != null)
        {
            return ReadSmUtilizationLegacy(legacy, dev);
        }

        var batched = Resolve<Nvml.DeviceGetProcessesUtilizationInfo>("nvmlDeviceGetProcessesUtilizationInfo");
        if (batched != null)
        {
            var info = new NvmlTypes.ProcessesUtilizationInfo
            {
                Version = NvmlTypes.ProcessesUtilizationVersion,
                ProcessSamplesCount = 0,
                LastSeenTimeStamp = 0,
                ProcUtilArray = IntPtr.Zero,
            };

            // Phase 1: NULL array — the driver answers with the element count.
            var probe = Safe(batched, "nvmlDeviceGetProcessesUtilizationInfo", () => batched(dev, ref info));
            if (probe == NvmlErr.Success)
            {
                return 0; // the driver handed back an empty list directly
            }
            if (probe != NvmlErr.InsufficientSize || info.ProcessSamplesCount == 0)
            {
                return 0; // NOT_FOUND (idle period) / NOT_SUPPORTED (MIG) / …
            }

            var n = Math.Min((int)info.ProcessSamplesCount, 256);
            var arr = Marshal.AllocHGlobal(n * NvmlTypes.ProcessUtilizationInfoSize);
            try
            {
                // Phase 2: caller-allocated array of v1 info structs.
                info.ProcessSamplesCount = (uint)n;
                info.ProcUtilArray = arr;
                var rc = Safe(batched, "nvmlDeviceGetProcessesUtilizationInfo", () => batched(dev, ref info));
                if (rc != NvmlErr.Success)
                {
                    return 0;
                }

                var total = 0;
                for (var i = 0; i < Math.Min(n, (int)info.ProcessSamplesCount); i++)
                {
                    var s = Marshal.PtrToStructure<NvmlTypes.ProcessUtilizationInfo>(
                        arr + i * NvmlTypes.ProcessUtilizationInfoSize);
                    total += (int)s.SmUtil;
                }

                return Math.Min(100, total);
            }
            finally
            {
                Marshal.FreeHGlobal(arr);
            }
        }

        return 0;
    }

    // The per-process two-phase read backing the SM column: NULL probe →
    // INSUFFICIENT_SIZE with the required record count → caller-allocated array →
    // the driver writes however many samples accumulated since the last call.
    private int ReadSmUtilizationLegacy(Nvml.DeviceGetProcessUtilization legacy, IntPtr dev)
    {
        var countPtr = Marshal.AllocHGlobal(4);
        IntPtr samples = IntPtr.Zero;
        try
        {
            var probe = Safe(legacy, "nvmlDeviceGetProcessUtilization", () => legacy(dev, IntPtr.Zero, countPtr, 0));
            if (probe == NvmlErr.InsufficientSize)
            {
                var n = Math.Min(Marshal.ReadInt32(countPtr), 256);
                if (n > 0)
                {
                    samples = Marshal.AllocHGlobal(n * NvmlTypes.ProcessUtilizationSampleSize);
                    Marshal.WriteInt32(countPtr, n);
                    var rc = Safe(legacy, "nvmlDeviceGetProcessUtilization", () => legacy(dev, samples, countPtr, 0));
                    if (rc == NvmlErr.Success)
                    {
                        // The driver may have written fewer records than n: count the real ones.
                        var written = Math.Min(n, Math.Max(0, Marshal.ReadInt32(countPtr)));
                        var total = 0;
                        for (var i = 0; i < written; i++)
                        {
                            var s = Marshal.PtrToStructure<NvmlTypes.ProcessUtilizationSample>(
                                samples + i * NvmlTypes.ProcessUtilizationSampleSize);
                            total += (int)s.SmUtil;
                        }

                        return Math.Min(100, total);
                    }

                    return 0; // the probe promised data but the read failed (GPU lost mid-read, …)
                }

                return 0; // the probe reported no samples
            }

            return 0; // NOT_FOUND (no active process this period) / NOT_SUPPORTED (MIG) / …
        }
        finally
        {
            if (samples != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(samples);
            }

            Marshal.FreeHGlobal(countPtr);
        }
    }

    private (long Corrected, long Uncorrected) ReadEcc(IntPtr dev)
    {
        var fn = Resolve<Nvml.DeviceGetTotalEccErrors>("nvmlDeviceGetTotalEccErrors");
        if (fn == null)
        {
            return (0, 0);
        }

        long corr = 0, uncorr = 0;
        var p = Marshal.AllocHGlobal(8);
        try
        {
            if (Safe(fn, "nvmlDeviceGetTotalEccErrors", () => fn(dev, NvmlEnum.MemErrCorrected, NvmlEnum.EccCounterVolatile, p)) == NvmlErr.Success)
            {
                corr = Marshal.ReadInt64(p);
            }

            if (Safe(fn, "nvmlDeviceGetTotalEccErrors", () => fn(dev, NvmlEnum.MemErrUncorrected, NvmlEnum.EccCounterVolatile, p)) == NvmlErr.Success)
            {
                uncorr = Marshal.ReadInt64(p);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }

        return (corr, uncorr);
    }

    private (int Current, int Persistent) ReadEccModes(IntPtr dev)
    {
        var fn = Resolve<Nvml.DeviceGetEccMode>("nvmlDeviceGetEccMode");
        if (fn == null)
        {
            return (-1, -1);
        }

        int cur = -1, persist = -1;
        var p1 = Marshal.AllocHGlobal(4);
        var p2 = Marshal.AllocHGlobal(4);
        try
        {
            if (Safe(fn, "nvmlDeviceGetEccMode", () => fn(dev, p1, p2)) == NvmlErr.Success)
            {
                cur = Marshal.ReadInt32(p1);
                persist = Marshal.ReadInt32(p2);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p1);
            Marshal.FreeHGlobal(p2);
        }

        return (cur, persist);
    }

    // ── running processes (two-phase) ────────────────────────────
    /// <summary>
    /// Reads the running-process list for one workload via the two-phase NVML
    /// contract: call with a null buffer to get the element count, then call
    /// again with a sized buffer. Resolves V3/V2/V1 symbols in order.
    /// </summary>
    private List<GpuProcessInfo> ReadProcesses(IntPtr dev, int gpuIndex, GpuProcessKind kind, List<GpuProcessInfo> sink)
    {
        var fn = Resolve<Nvml.DeviceGetRunningProcesses>(
            kind == GpuProcessKind.Compute
                ? "nvmlDeviceGetComputeRunningProcesses_v3"
                : "nvmlDeviceGetGraphicsRunningProcesses_v3",
            kind == GpuProcessKind.Compute
                ? "nvmlDeviceGetComputeRunningProcesses_v2"
                : "nvmlDeviceGetGraphicsRunningProcesses_v2",
            kind == GpuProcessKind.Compute
                ? "nvmlDeviceGetComputeRunningProcesses"
                : "nvmlDeviceGetGraphicsRunningProcesses");

        if (fn == null)
        {
            return new List<GpuProcessInfo>();
        }

        var countPtr = Marshal.AllocHGlobal(4);
        var infosPtr = IntPtr.Zero;
        try
        {
            // phase 1: null buffer -> NVML writes the element count.
            Marshal.WriteInt32(countPtr, 0);
            if (Safe(fn, "count", () => fn(dev, countPtr, IntPtr.Zero)) != NvmlErr.InsufficientSize)
            {
                return new List<GpuProcessInfo>();
            }

            var n = Marshal.ReadInt32(countPtr);
            if (n <= 0)
            {
                return new List<GpuProcessInfo>();
            }

            // phase 2: sized buffer -> NVML fills it.
            var stride = Marshal.SizeOf<NvmlTypes.ProcessInfo>();
            infosPtr = Marshal.AllocHGlobal(n * stride);
            var result = new List<GpuProcessInfo>(n);

            if (Safe(fn, "fill", () => fn(dev, countPtr, infosPtr)) == NvmlErr.Success)
            {
                var read = Math.Min(n, Marshal.ReadInt32(countPtr));
                for (var i = 0; i < read; i++)
                {
                    var offset = infosPtr.ToInt64() + (i * stride);
                    var pi = (NvmlTypes.ProcessInfo)Marshal.PtrToStructure<NvmlTypes.ProcessInfo>(
                        new IntPtr(offset));
                    result.Add(new GpuProcessInfo
                    {
                        GpuIndex = gpuIndex,
                        Pid = pi.Pid,
                        Kind = kind,
                        MemoryUsedBytes = (long)pi.UsedGpuMemory,
                        Timestamp = DateTimeOffset.Now,
                    });
                }
            }

            sink.AddRange(result);
            return result;
        }
        finally
        {
            if (infosPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(infosPtr);
            }

            Marshal.FreeHGlobal(countPtr);
        }
    }

    // ── NVLink topology ──────────────────────────────────────────
    private (int Total, int Active) ReadNvLinks(IntPtr dev)
    {
        var total = GetU32(dev,
            Resolve<Nvml.DeviceGetNvLinkSupportedCount>("nvmlDeviceGetNvLinkSupportedCount"),
            (f, p) => f(dev, p));
        if (total <= 0) // 0 = no NVLink, -1 = metric unavailable
        {
            return (0, 0);
        }

        var state = Resolve<Nvml.DeviceGetNvLinkState>("nvmlDeviceGetNvLinkState");
        if (state == null)
        {
            return (total, 0);
        }

        var active = 0;
        var p = Marshal.AllocHGlobal(4);
        try
        {
            for (var link = 0; link < total; link++)
            {
                if (Safe(state, "nvmlDeviceGetNvLinkState", () => state(dev, (uint)link, p)) == NvmlErr.Success)
                {
                    if (Marshal.ReadInt32(p) != 0)
                    {
                        active++;
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }

        return (total, active);
    }

    // ── PCI bus id ───────────────────────────────────────────────
    private string ReadPciId(IntPtr dev)
    {
        var fn = Resolve<Nvml.DeviceGetPciInfoV3>("nvmlDeviceGetPciInfo_v3");
        if (fn == null)
        {
            return string.Empty;
        }

        var info = new NvmlTypes.PciInfo();
        var rc = Safe(fn, "nvmlDeviceGetPciInfo_v3", () => fn(dev, ref info));
        if (rc != NvmlErr.Success)
        {
            return string.Empty;
        }

        // Prefer the NUL-terminated BusId string field if it has data, else
        // synthesize from the numeric Domain/Bus/Device.
        var text = ReadStringBytes(info.BusId);
        if (text.Length > 0)
        {
            return text;
        }

        return $"{info.Domain:X4}:{info.Bus:X2}:{info.Device:X2}.0";
    }

    private static string ReadStringBytes(byte[]? buf)
    {
        if (buf is null)
        {
            return string.Empty;
        }

        var len = Array.IndexOf(buf, (byte)0);
        if (len < 0)
        {
            len = buf.Length;
        }

        return Encoding.UTF8.GetString(buf, 0, len).Trim();
    }
}