using HectorBravo.Nvidia.Contracts;
using HectorBravo.Nvidia.Core;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// Factory that constructs the concrete NVML-backed monitoring engine.
///
/// The <c>NvmlCore</c> project is a direct <c>ProjectReference</c>, so the
/// <see cref="NvmlCoreService"/> type is available at compile time and can be
/// instantiated directly — no runtime reflection probe is needed. The factory
/// keeps a stable surface (<see cref="HasConcreteService"/>,
/// <see cref="CreateMonitor"/>) so callers (App/MainWindow) depend only on the
/// <see cref="IGpuMonitorService"/> contract, not the concrete engine type.
/// </summary>
public static class GpuServiceFactory
{
    /// <summary>True — a concrete <see cref="IGpuMonitorService"/> (NVML-backed) is available.</summary>
    public static bool HasConcreteService => true;

    /// <summary>
    /// Creates a fresh <see cref="NvmlCoreService"/>. Pass an optional
    /// <see cref="AppLog"/> to route the engine's activity into the UI log.
    /// </summary>
    public static IGpuMonitorService? CreateMonitor(AppLog? log = null)
        => new NvmlCoreService(log);

    /// <summary>Convenience: create the concrete engine type directly.</summary>
    public static NvmlCoreService CreateConcrete(AppLog? log = null)
        => new NvmlCoreService(log);
}
