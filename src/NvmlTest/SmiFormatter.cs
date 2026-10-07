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
    // Column widths (monospaced). The name column is the widest so long GPU
    // names keep the grid readable.
    private const int WIdx = 2;
    private const int WName = 24;
    private const int WNum = 8;
    private const int WMem = 18;
    private const int WPower = 18;
    private const int Sep = 2;

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

        // Header row.
        sb.Append(Pad("GPU", WIdx)).Append(Spaces(Sep));
        sb.Append(Pad("Name", WName)).Append(Spaces(Sep));
        sb.Append(PadL("Util %", WNum)).Append(Spaces(Sep));
        sb.Append(PadL("Mem %", WNum)).Append(Spaces(Sep));
        sb.Append(PadL("Temp °C", WNum)).Append(Spaces(Sep));
        sb.Append(PadL("Power", WPower)).Append(Spaces(Sep));
        sb.Append(PadL("Memory", WMem)).Append(Spaces(Sep));
        sb.Append(PadL("MemClk", WNum)).Append(Spaces(Sep));
        sb.Append(PadL("PSt", WNum)).Append(Spaces(Sep));
        sb.Append(PadL("Fan %", WNum));
        sb.AppendLine();

        // Separator row (dashes), matching nvidia-smi.
        sb.Append(Dash(WIdx)).Append(Spaces(Sep));
        sb.Append(Dash(WName)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WPower)).Append(Spaces(Sep));
        sb.Append(Dash(WMem)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WNum));
        sb.AppendLine();

        foreach (var g in s.Gpus)
        {
            sb.Append(Pad(g.Index.ToString(CultureInfo.InvariantCulture), WIdx)).Append(Spaces(Sep));
            sb.Append(Pad(g.Name, WName)).Append(Spaces(Sep));
            sb.Append(PadL(Int(g.GpuUtilizationPct), WNum)).Append(Spaces(Sep));
            sb.Append(PadL(Int(g.MemoryUtilizationPct), WNum)).Append(Spaces(Sep));
            sb.Append(PadL(Int(g.TemperatureGpu), WNum)).Append(Spaces(Sep));
            sb.Append(PadL(Power(g.PowerMw, g.EnforcedPowerLimitMw), WPower)).Append(Spaces(Sep));
            sb.Append(PadL(Memory(g.MemoryUsedBytes, g.MemoryTotalBytes), WMem)).Append(Spaces(Sep));
            sb.Append(PadL(Int(g.MemoryClockMhz), WNum)).Append(Spaces(Sep));
            sb.Append(PadL(g.PerfStateLabel, WNum)).Append(Spaces(Sep));
            sb.Append(PadL(Int(g.FanSpeedPct), WNum));
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

    /// <summary>Integer metric; a negative sentinel (unknown) renders as N/A, a valid 0 renders as 0.</summary>
    private static string Int(int v) => v < 0 ? "N/A" : v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Power: "usage / cap W" (space-separated, unit at the end); a negative usage sentinel renders N/A.</summary>
    private static string Power(int usageMw, int capMw) =>
        usageMw < 0 ? "N/A" : $"{Watts(usageMw)} / {Watts(capMw)} W";

    /// <summary>mW → watts, one decimal; a negative sentinel renders as N/A, a valid 0 renders as 0.0.</summary>
    private static string Watts(int mw) =>
        mw < 0 ? "N/A" : (mw / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Memory: "used / total MB" (space-separated); 0 total renders as N/A. Plain integer MB, no thousands separator.</summary>
    private static string Memory(long usedBytes, long totalBytes) =>
        totalBytes <= 0 ? "N/A"
        : $"{Mb(usedBytes)} / {Mb(totalBytes)} MB";

    /// <summary>Bytes → MB (integer). Invariant culture so no thousands/comma separator is ever emitted.</summary>
    private static string Mb(long bytes) => (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
}