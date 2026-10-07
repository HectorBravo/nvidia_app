namespace HectorBravo.Nvidia.Contracts;

/// <summary>
/// The full state of the NVML domain at a moment in time: system-level
/// version info plus one <see cref="GpuSnapshot"/> per GPU and the flat
/// list of all GPU processes across every GPU.
/// </summary>
public sealed record SystemSnapshot
{
    public string DriverVersion { get; init; } = string.Empty;
    public string CudaDriverVersion { get; init; } = string.Empty;
    public string NvmlVersion { get; init; } = string.Empty;

    public IReadOnlyList<GpuSnapshot> Gpus { get; init; } = Array.Empty<GpuSnapshot>();
    public IReadOnlyList<GpuProcessInfo> Processes { get; init; } = Array.Empty<GpuProcessInfo>();

    public int GpuCount => Gpus.Count;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
