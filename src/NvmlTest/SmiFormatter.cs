using System.Globalization;
using System.Text;
using HectorBravo.Nvidia.Contracts;

namespace HectorBravo.Nvidia.Test;

/// <summary>
/// Renders a <see cref="SystemSnapshot"/> as an nvidia-smi-style, monospaced,
/// column-aligned text table for headless / CLI consumption.
/// </summary>
public static class SmiFormatter
{
    // Column widths: each is max(header length, longest possible value).
    // This guarantees headers and dashes are perfectly aligned.
    private const int WIdx     = 3;   // "GPU"        | values: 0,1,2
    private const int WName    = 24;  // "Name"       | longest GPU name
    private const int WUtil    = 9;   // "GPU Usage"  | "100 %"
    private const int WMemUtil = 9;   // "Mem Usage"  | "100 %"
    private const int WTemp    = 6;   // "Temp"       | "100 °C"
    private const int WPower   = 15;  // "Power"      | "112.2 / 430.0 W"
    private const int WMemory  = 17;  // "Memory"     | "23784 / 24576 MB"
    private const int WMemClk  = 8;   // "MemClk"     | "9999 MHz"
    private const int WPSt     = 3;   // "PSt"        | "P0"..."P8"
    private const int WFan     = 5;   // "Fan"        | "100 %"
    private const int Sep      = 2;

    public static string Format(SystemSnapshot s)
    {
        var sb = new StringBuilder();

        // Header line: system-level version info (nvidia-smi style banner).
        sb.Append("Driver: ").Append(NonEmpty(s.DriverVersion));
        sb.Append("  CUDA: ").Append(NonEmpty(s.CudaDriverVersion));
        sb.Append("  NVML: ").Append(NonEmpty(s.NvmlVersion));
        sb.Append(' ').Append(s.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();
        sb.AppendLine();

        if (s.GpuCount == 0)
        {
            sb.AppendLine("No GPUs found.");
            return sb.ToString();
        }

        // Header row (left-aligned for index/name, right-aligned for numeric).
        sb.Append(Pad("GPU", WIdx)).Append(Spaces(Sep));
        sb.Append(Pad("Name", WName)).Append(Spaces(Sep));
        sb.Append(PadL("GPU Usage", WUtil)).Append(Spaces(Sep));
        sb.Append(PadL("Mem Usage", WMemUtil)).Append(Spaces(Sep));
        sb.Append(PadL("Temp", WTemp)).Append(Spaces(Sep));
        sb.Append(PadL("Power", WPower)).Append(Spaces(Sep));
        sb.Append(PadL("Memory", WMemory)).Append(Spaces(Sep));
        sb.Append(PadL("MemClk", WMemClk)).Append(Spaces(Sep));
        sb.Append(PadL("PSt", WPSt)).Append(Spaces(Sep));
        sb.Append(PadL("Fan", WFan));
        sb.AppendLine();

        // Separator row (dashes).
        sb.Append(Dash(WIdx)).Append(Spaces(Sep));
        sb.Append(Dash(WName)).Append(Spaces(Sep));
        sb.Append(Dash(WUtil)).Append(Spaces(Sep));
        sb.Append(Dash(WMemUtil)).Append(Spaces(Sep));
        sb.Append(Dash(WTemp)).Append(Spaces(Sep));
        sb.Append(Dash(WPower)).Append(Spaces(Sep));
        sb.Append(Dash(WMemory)).Append(Spaces(Sep));
        sb.Append(Dash(WMemClk)).Append(Spaces(Sep));
        sb.Append(Dash(WPSt)).Append(Spaces(Sep));
        sb.Append(Dash(WFan));
        sb.AppendLine();

        foreach (var g in s.Gpus)
        {
            sb.Append(Pad(g.Index.ToString(CultureInfo.InvariantCulture), WIdx)).Append(Spaces(Sep));
            sb.Append(Pad(g.Name, WName)).Append(Spaces(Sep));
            sb.Append(PadL(Pct(g.GpuUtilizationPct), WUtil)).Append(Spaces(Sep));
            sb.Append(PadL(Pct(g.MemoryUtilizationPct), WMemUtil)).Append(Spaces(Sep));
            sb.Append(PadL(TempVal(g.TemperatureGpu), WTemp)).Append(Spaces(Sep));
            sb.Append(PadL(Power(g.PowerMw, g.EnforcedPowerLimitMw), WPower)).Append(Spaces(Sep));
            sb.Append(PadL(Memory(g.MemoryUsedBytes, g.MemoryTotalBytes), WMemory)).Append(Spaces(Sep));
            sb.Append(PadL(MemClkVal(g.MemoryClockMhz), WMemClk)).Append(Spaces(Sep));
            sb.Append(PadL(g.PerfStateLabel, WPSt)).Append(Spaces(Sep));
            sb.Append(PadL(Pct(g.FanSpeedPct), WFan));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── helpers ─────────────────────────────────────────────────

    private static string NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? "N/A" : s!;

    private static string Pad(string? s, int width) =>
        (s ?? "N/A").PadRight(width);

    private static string PadL(string? s, int width) =>
        (s ?? "N/A").PadLeft(width);

    private static string Dash(int width) => new string('-', width);

    private static string Spaces(int n) => new string(' ', n);

    /// <summary>Percentage: "34 %" or N/A for negative sentinel.</summary>
    private static string Pct(int v) => v < 0 ? "N/A" : $"{v} %";

    /// <summary>Temperature: "30 °C" or N/A for negative sentinel.</summary>
    private static string TempVal(int v) => v < 0 ? "N/A" : $"{v} \u00B0C";

    /// <summary>Memory clock: "9501 MHz" or N/A for negative sentinel.</summary>
    private static string MemClkVal(int v) => v < 0 ? "N/A" : $"{v} MHz";

    /// <summary>Power: "usage / cap W"; a negative usage sentinel renders N/A.</summary>
    private static string Power(int usageMw, int capMw) =>
        usageMw < 0 ? "N/A" : $"{Watts(usageMw)} / {Watts(capMw)} W";

    /// <summary>mW → watts, one decimal; a negative sentinel renders as N/A, a valid 0 renders as 0.0.</summary>
    private static string Watts(int mw) =>
        mw < 0 ? "N/A" : (mw / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Memory: "used / total MB"; 0 total renders as N/A.</summary>
    private static string Memory(long usedBytes, long totalBytes) =>
        totalBytes <= 0 ? "N/A"
        : $"{Mb(usedBytes)} / {Mb(totalBytes)} MB";

    /// <summary>Bytes → MB (integer). Invariant culture so no thousands/comma separator is ever emitted.</summary>
    private static string Mb(long bytes) => (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
}