namespace HectorBravo.Nvidia.Contracts;

/// <summary>Which workloads a process is consuming on the GPU.</summary>
public enum GpuProcessKind
{
    Compute,
    Graphics,
}

/// <summary>
/// A single process using a GPU, as reported by NVML running-process APIs.
/// <see cref="ProcessName"/> is resolved from the OS where possible; NVML
/// itself only returns the PID, so it may be null for processes the OS
/// cannot resolve (e.g. cross-session or system processes).
/// </summary>
public sealed record GpuProcessInfo
{
    public int GpuIndex { get; init; }
    public long Pid { get; init; }
    public string? ProcessName { get; init; }
    public GpuProcessKind Kind { get; init; }
    public long MemoryUsedBytes { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}
