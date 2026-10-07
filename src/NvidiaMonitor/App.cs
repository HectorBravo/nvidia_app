using System;
using System.Windows;
using HectorBravo.Nvidia.Contracts;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// WPF entry point for the NVIDIA GPU monitor.
///
/// Two modes:
///  - Normal (no args): launches <see cref="MainWindow"/>.
///  - Headless (--print): performs a single NVML refresh and prints a one-line
///    summary per GPU to the console, then exits without opening a window.
///    If NVML is unavailable, it prints the service's StatusMessage and exits.
/// </summary>
public partial class App : Application
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--print") >= 0)
        {
            // Headless: never construct a WPF window; exit code reflects success.
            Environment.Exit(RunHeadless());
            return;
        }

        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new MainWindow());
    }

    /// <summary>
    /// Headless single-shot refresh. Prints a header line plus one summary
    /// line per GPU to <c>stdout</c>. Returns 0 on a successful run (even when
    /// zero GPUs are found) and 1 when no service exists or NVML is
    /// unavailable (the reason goes to <c>stdout</c> as well). No WPF window
    /// is ever created in this path.
    /// </summary>
    private static int RunHeadless()
    {
        var svc = GpuServiceFactory.CreateMonitor();
        if (svc == null)
        {
            Console.Error.WriteLine("[NvidiaMonitor] No GPU monitoring service is available.");
            return 1;
        }

        try
        {
            var ok = svc.InitializeAsync().GetAwaiter().GetResult();
            if (!ok || !svc.IsAvailable)
            {
                Console.WriteLine($"[NvidiaMonitor] NVML unavailable: {svc.StatusMessage}");
                return 1;
            }

            var snap = svc.RefreshAsync().GetAwaiter().GetResult();
            Console.WriteLine(
                $"[NvidiaMonitor] driver={OrDash(snap.DriverVersion)} cuda={OrDash(snap.CudaDriverVersion)} nvml={OrDash(snap.NvmlVersion)} gpus={snap.GpuCount}");

            if (snap.GpuCount == 0)
            {
                Console.WriteLine("[NvidiaMonitor] No GPUs detected.");
            }
            else
            {
                foreach (var g in snap.Gpus)
                {
                    var name = string.IsNullOrWhiteSpace(g.Name) ? $"GPU {g.Index}" : g.Name;
                    Console.WriteLine(
                        $"GPU[{g.Index}] {name}  util={FmtPct(g.GpuUtilizationPct)}  mem={FmtPct(g.MemoryUtilizationPct)}  " +
                        $"temp={FmtInt(g.TemperatureGpu)}C  power={FmtWatts(g.PowerMw)}  " +
                        $"mem={Theme.MiB(g.MemoryUsedBytes)}/{Theme.MiB(g.MemoryTotalBytes)} MiB  fan={FmtPct(g.FanSpeedPct)}  perf={g.PerfStateLabel}");
                }
            }

            return 0;
        }
        finally
        {
            // Release NVML resources; never let shutdown mask the exit code.
            try
            {
                svc.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // best effort
            }
        }
    }

    private static string OrDash(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s;
    private static string FmtPct(int v) => v < 0 ? "N/A" : v + "%";
    private static string FmtInt(int v) => v <= 0 ? "N/A" : v.ToString();
    private static string FmtWatts(int mw) => mw <= 0 ? "N/A" : Theme.Watts(mw) + " W";
}
