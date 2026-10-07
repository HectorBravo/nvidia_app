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
    private const int WMem = 13;
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
        sb.Append(Pad("Util %", WNum)).Append(Spaces(Sep));
        sb.Append(Pad("Mem %", WNum)).Append(Spaces(Sep));
        sb.Append(Pad("Temp °C", WNum)).Append(Spaces(Sep));
        sb.Append(Pad("Power W", WNum)).Append(Spaces(Sep));
        sb.Append(Pad("Memory", WMem)).Append(Spaces(Sep));
        sb.Append(Pad("MemClk", WNum)).Append(Spaces(Sep));
        sb.Append(Pad("PSt", WNum)).Append(Spaces(Sep));
        sb.Append("Fan %");
        sb.AppendLine();

        // Separator row (dashes), matching nvidia-smi.
        sb.Append(Dash(WIdx)).Append(Spaces(Sep));
        sb.Append(Dash(WName)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WMem)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WNum)).Append(Spaces(Sep));
        sb.Append(Dash(WNum));
        sb.AppendLine();

        foreach (var g in s.Gpus)
        {
            sb.Append(Pad(g.Index.ToString(CultureInfo.InvariantCulture), WIdx)).Append(Spaces(Sep));
            sb.Append(Pad(g.Name, WName)).Append(Spaces(Sep));
            sb.Append(Pad(Int(g.GpuUtilizationPct), WNum)).Append(Spaces(Sep));
            sb.Append(Pad(Int(g.MemoryUtilizationPct), WNum)).Append(Spaces(Sep));
            sb.Append(Pad(Int(g.TemperatureGpu), WNum)).Append(Spaces(Sep));
            sb.Append(Pad(Power(g.PowerMw), WNum)).Append(Spaces(Sep));
            sb.Append(Pad(Memory(g.MemoryUsedBytes, g.MemoryTotalBytes), WMem)).Append(Spaces(Sep));
            sb.Append(Pad(Int(g.MemoryClockMhz), WNum)).Append(Spaces(Sep));
            sb.Append(Pad(g.PerfStateLabel, WNum)).Append(Spaces(Sep));
            sb.Append(Int(g.FanSpeedPct));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ── helpers ─────────────────────────────────────────────────

    private static string NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? "N/A" : s!;

    private static string Pad(string? s, int width) =>
        (s ?? "N/A").PadRight(width);

    private static string Dash(int width) => new string('-', width);

    private static string Spaces(int n) => new string(' ', n);

    /// <summary>Integer metric; 0 (the NVML "unsupported" sentinel) renders as N/A.</summary>
    private static string Int(int v) => v == 0 ? "N/A" : v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Power: mW → watts, one decimal; 0 renders as N/A.</summary>
    private static string Power(int mw) =>
        mw == 0 ? "N/A" : (mw / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Memory: "used/total GB" with one decimal; 0 total renders as N/A.</summary>
    private static string Memory(long usedBytes, long totalBytes) =>
        totalBytes <= 0 ? "N/A"
        : $"{Gb(usedBytes)}/{Gb(totalBytes)} GB";

    /// <summary>Bytes → GiB, one decimal (e.g. 24576 MiB → "24.0").
    /// NOTE: the format spec must be "0.0" (a digit placeholder after the decimal),
    /// NOT "0.1" — in a .NET custom numeric format string the "1" is a literal
    /// character (and the decimal point is dropped when no digit placeholder
    /// follows it), which would render 24.0 as "241".</summary>
    private static string Gb(long bytes) =>
        (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
}