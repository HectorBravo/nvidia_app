namespace HectorBravo.Nvidia.Contracts;

/// <summary>
/// A lightweight, chart-oriented sample for one GPU. Kept separate from
/// <see cref="GpuSnapshot"/> so the time-series buffers stay cheap to
/// allocate/copy and carry only the values the live charts plot.
/// </summary>
public sealed record GpuMetricSample
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public int GpuIndex { get; init; }
    public int GpuUtilizationPct { get; init; }
    public int MemoryUtilizationPct { get; init; }
    public double PowerWatts { get; init; }
    public long MemoryUsedBytes { get; init; }
    public int TemperatureGpu { get; init; }
    public int GpuClockMhz { get; init; }
    public double PcieBandwidthGbps { get; init; }
    public double PcieMaxBandwidthGbps { get; init; }
    // Current link gen/width (0 = N/A) per sample, so the PCIe chart can show the full
    // link config ("4.0 x16") in the legend and in the hover readout, not just GB/s.
    public int PcieLinkGen { get; init; }
    public int PcieLinkWidth { get; init; }
    // GPU card name (from the snapshot) so the legend / hover readout can show the
    // per-GPU identity "[index: name]" next to the plotted values.
    public string GpuName { get; init; } = string.Empty;
    // Per-sample denominators for percentage readouts (0 = N/A, percentage is omitted):
    // total device memory in bytes (memory chart) and the enforced power cap in watts
    // (power chart).
    public long MemoryTotalBytes { get; init; }
    public double PowerCapWatts { get; init; }
}
