namespace HectorBravo.Nvidia.Core;

/// <summary>
/// Bootstrap placeholder so the project builds before the NVML engine
/// (Subagent A) lands. Will be replaced by the real
/// <see cref="HectorBravo.Nvidia.Contracts.IGpuMonitorService"/>
/// implementation.
/// </summary>
internal static class Bootstrap
{
    public static string Placeholder => "NvmlCore bootstrap (awaiting engine)";
}
