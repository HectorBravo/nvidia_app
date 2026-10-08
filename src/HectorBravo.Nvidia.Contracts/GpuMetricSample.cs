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
}
