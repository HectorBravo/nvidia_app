using System;
using System.IO;
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

        // Single-instance guard: at most one GUI window may run at a time. If another
        // NvidiaMonitor instance is already live, print a refusal message and return
        // without opening a window (exit 0). Headless (--print) never reaches this.
        if (AnotherInstanceRunning())
        {
            Console.Error.WriteLine(
                "[NvidiaMonitor] Another NvidiaMonitor instance is already running - only one may run at a time.");
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
                        $"GPU[{g.Index}] {name}  util={FmtPct(g.GpuUtilizationPct)}  sm={FmtPct(g.SmUtilizationPct)}  mem={FmtPct(g.MemoryUtilizationPct)}  " +
                        $"temp={FmtInt(g.TemperatureGpu)}C  power={FmtWatts(g.PowerMw)}  " +
                        $"mem={Theme.Mb(g.MemoryUsedBytes)}/{Theme.Mb(g.MemoryTotalBytes)} MB  fan={FmtPct(g.FanSpeedPct)}  perf={g.PerfStateLabel}");
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

    /// <summary>
    /// Single-instance guard. Each GUI instance keeps a small lockfile whose mtime is
    /// refreshed on its ~1 Hz poll tick; a live instance therefore always leaves a
    /// fresh stamp, while a crashed/closed one leaves a stale (or missing) file. On
    /// launch we treat another instance as running when the lockfile was written
    /// within the last few seconds. Headless (--print) never reaches this check.
    /// </summary>
    private static bool AnotherInstanceRunning()
    {
        try
        {
            var stamp = LockStamp();
            if (stamp < 0)
            {
                return false; // no lockfile, unreadable, or stale -> we may start
            }

            // A live instance refreshes the stamp every ~1 s; treat > 6 s as dead.
            return stamp < 6000;
        }
        catch
        {
            // Fail open: never let the guard break the app.
            return false;
        }
    }

    /// <summary>Milliseconds since the lockfile was last written, or -1 if absent.</summary>
    private static long LockStamp()
    {
        var p = LockPath;
        try
        {
            if (!File.Exists(p))
            {
                return -1;
            }

            return (long)(DateTime.Now - new FileInfo(p).LastWriteTime).TotalMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Refreshes the lockfile stamp (its mtime is the heartbeat). Called by
    /// <see cref="MainWindow"/> on every poll tick (~1 Hz) while a GUI instance
    /// is live, so <see cref="AnotherInstanceRunning"/> can tell "alive" from
    /// "crashed/closed" without any IPC. Best effort — never throws.
    /// </summary>
    public static void RefreshInstanceLock()
    {
        try
        {
            var p = LockPath;
            if (!File.Exists(p))
            {
                File.WriteAllText(p, "");
            } else {
                // Refresh the stamp by writing (mtime == heartbeat); avoid a needless
                // truncate race by only rewriting when the file is older than ~2 s.
                var age = (long)(DateTime.Now - new FileInfo(p).LastWriteTime).TotalMilliseconds;
                if (age > 2000)
                {
                    File.WriteAllText(p, "");
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    /// Absolute path of the system temp directory (the %TEMP% env var).
    private static readonly string TEMP =
        Environment.GetEnvironmentVariable("TEMP") ?? @"C:\Windows\Temp";

    private static readonly string LockPath =
        Path.Combine(TEMP, "NvidiaMonitor.lock");
}
