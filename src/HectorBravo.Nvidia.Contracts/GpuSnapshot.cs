namespace HectorBravo.Nvidia.Contracts;

/// <summary>
/// A single point-in-time snapshot of one GPU. Values that NVML cannot
/// report for a given device are exposed as <c>0</c> (or <c>-1</c> for
/// counts/sizes where that is the natural "unknown" sentinel) and the UI
/// renders them as "N/A".
/// </summary>
public sealed record GpuSnapshot
{
    // ── Identity ───────────────────────────────────────────────
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Uuid { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public string BusId { get; init; } = string.Empty;
    public string VbiosVersion { get; init; } = string.Empty;

    // ── Utilization (%) ────────────────────────────────────────
    public int GpuUtilizationPct { get; init; }
    public int MemoryUtilizationPct { get; init; }
    public int EncoderUtilizationPct { get; init; }
    public int DecoderUtilizationPct { get; init; }

    // ── Temperature (°C) ───────────────────────────────────────
    public int TemperatureGpu { get; init; }
    public int TemperatureMemory { get; init; }

    // ── Power (mW) ─────────────────────────────────────────────
    public int PowerMw { get; init; }
    public int EnforcedPowerLimitMw { get; init; }
    public int DefaultPowerLimitMw { get; init; }
    public int MinPowerLimitMw { get; init; }
    public int MaxPowerLimitMw { get; init; }

    // ── Memory (bytes) ─────────────────────────────────────────
    public long MemoryTotalBytes { get; init; }
    public long MemoryUsedBytes { get; init; }
    public long MemoryFreeBytes { get; init; }

    // ── Clocks (MHz) ───────────────────────────────────────────
    public int GpuClockMhz { get; init; }
    public int MemoryClockMhz { get; init; }
    public int VideoClockMhz { get; init; }
    public int MaxGpuClockMhz { get; init; }
    public int MaxMemoryClockMhz { get; init; }

    // ── Fan / P-state ──────────────────────────────────────────
    public int FanSpeedPct { get; init; }
    public int PerfState { get; init; } = -1; // NVML_PERF_STATE_*; -1 = N/A

    // ── ECC ────────────────────────────────────────────────────
    public int EccCurrentMode { get; init; }   // 0 = disabled, 1 = enabled, -1 = N/A
    public int EccPersistingMode { get; init; }
    public long EccErrorsCorrected { get; init; }
    public long EccErrorsUncorrected { get; init; }

    // ── Throttling ─────────────────────────────────────────────
    public long ThrottleReasonsRaw { get; init; }              // nvmlClockEventReasons_t bitmask
    public IReadOnlyList<string> ActiveThrottleReasons { get; init; } = Array.Empty<string>();
    public long ViolationStatus { get; init; }                // raw violation counters bitmask

    // ── NVLink ─────────────────────────────────────────────────
    public int NvLinkCount { get; init; }
    public int NvLinkActiveCount { get; init; }

    // ── Modes / capabilities ───────────────────────────────────
    public bool PersistenceModeEnabled { get; init; }
    public int NumGpuCores { get; init; }
    public string? PowerSource { get; init; } // e.g. "External" / "Internal"

    // ── Derived counts ─────────────────────────────────────────
    public long ComputeProcessCount { get; init; }
    public long GraphicsProcessCount { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Human-readable perf-state label, e.g. "P0", or "N/A".</summary>
    public string PerfStateLabel =>
        PerfState is >= 0 and <= 15 ? $"P{PerfState}" : "N/A";
}
