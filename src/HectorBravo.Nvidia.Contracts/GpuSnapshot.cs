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

    // ── PCIe link ────────────────────────────────────────────────────────────────────
    public int PcieLinkGen { get; init; }      // 1-5; 0 = N/A
    public int PcieLinkWidth { get; init; }    // 1,2,4,8,16; 0 = N/A
    public int PcieMaxLinkGen { get; init; }   // max supported gen for this GPU
    public int PcieMaxLinkWidth { get; init; } // max supported width for this GPU

    /// <summary>Current PCIe bandwidth in GB/s, or 0 if unavailable.</summary>
    public double PcieBandwidthGbps => PcieLinkGen > 0 && PcieLinkWidth > 0
        ? PciePerLaneGbps(PcieLinkGen) * PcieLinkWidth
        : 0;

    /// <summary>Maximum possible PCIe bandwidth in GB/s for this GPU.</summary>
    public double PcieMaxBandwidthGbps => PcieMaxLinkGen > 0 && PcieMaxLinkWidth > 0
        ? PciePerLaneGbps(PcieMaxLinkGen) * PcieMaxLinkWidth
        : 0;

    /// <summary>Per-lane transfer rate in GB/s for a given PCIe generation.</summary>
    public static double PciePerLaneGbps(int gen) => gen switch
    {
        1 => 0.25,
        2 => 0.5,
        3 => 1.0,
        4 => 2.0,
        5 => 4.0,
        _ => 0,
    };

    /// <summary>Display string like "4.0 x16" or "N/A" (current link).</summary>
    public string PcieLabel => PcieLinkLabel(PcieLinkGen, PcieLinkWidth);

    /// <summary>Display string for a given PCIe gen/width, like "4.0 x16" or "N/A".</summary>
    /// <remarks>Static so per-sample history (e.g. the chart legend/hover) can format any
    /// gen/width consistently with the current-link <see cref="PcieLabel"/>.</remarks>
    public static string PcieLinkLabel(int gen, int width)
        => gen > 0 && width > 0 ? $"{gen}.0 x{width}" : "N/A";

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
