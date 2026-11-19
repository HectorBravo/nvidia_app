using System;
using System.Globalization;
using System.Text;
using HectorBravo.Nvidia.Contracts;

namespace HectorBravo.Nvidia.Test;

/// <summary>
/// Renders a <see cref="SystemSnapshot"/> plus the machine's physical-RAM reading
/// as an nvidia-smi-style, monospaced, column-aligned text table for headless /
/// CLI consumption.
/// </summary>
public static class SmiFormatter
{
    // Column widths: each is max(header part length, longest possible value).
    // This guarantees headers and dashes are perfectly aligned. Headers may span
    // two lines (see the header block in Format): long labels wrap onto line 2,
    // short ones sit on line 2 alone so line 1 stays blank above them.
    private const int WIdx     = 3;   // "Id"         | values: 0,1,2
    private const int WName    = 24;  // "Name"       | longest GPU name
    private const int WUtil    = 6;   // "GPU" /      | "100%" ("use")
    private const int WMemUtil = 6;   // "Mem" /      | "100%" ("use")
    private const int WSm      = 4;   // "SM"         | "100%"
    private const int WTemp    = 4;   // "Temp"       | "100°C"
    private const int WPower   = 8;   // "Power"      | "1000/1000W" (int watts)
    private const int WMemory  = 13;  // "Memory"     | "23784/24576MB"
    private const int WMemClk  = 7;   // "MemClk"     | "9999MHz"
    private const int WPSt     = 3;   // "PSt"        | "P0"..."P8"
    private const int WFan     = 4;   // "Fan"        | "100%"
    private const int WSysRam  = 13;  // "SysRAM"     | "100 % 83/128GB" → "100% 83/128GB"
    private const int WPcie    = 13;  // "PCIe"       | "4.0x16/32GB/s"
    private const int Sep      = 1;   // single space between columns (compact table)

    /// <summary>
    /// Renders a <see cref="SystemSnapshot"/> plus the machine's physical-RAM
    /// reading as an nvidia-smi-style, monospaced, column-aligned text table for
    /// headless / CLI consumption.
    /// </summary>
    public static string Format(SystemSnapshot s, long ramUsedBytes, long ramTotalBytes)
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

        // Header rows. Only the two utilization labels wrap: "GPU"/"use" and "Mem"/"use"
        // put their first word on line 1; every other label sits on line 2 (blank padding
        // above it), so line 2 carries the full header and pads the last column to match.
        sb.Append(Spaces(WIdx)).Append(Spaces(Sep));
        sb.Append(Spaces(WName)).Append(Spaces(Sep));
        sb.Append(PadL("GPU", WUtil)).Append(Spaces(Sep));
        sb.Append(PadL("Mem", WMemUtil)).Append(Spaces(Sep));
        sb.Append(Spaces(WSm)).Append(Spaces(Sep));
        sb.Append(Spaces(WTemp)).Append(Spaces(Sep));
        sb.Append(Spaces(WPower)).Append(Spaces(Sep));
        sb.Append(Spaces(WMemory)).Append(Spaces(Sep));
        sb.Append(Spaces(WMemClk)).Append(Spaces(Sep));
        sb.Append(Spaces(WPSt)).Append(Spaces(Sep));
        sb.Append(Spaces(WFan)).Append(Spaces(Sep));
        sb.Append(Spaces(WSysRam)).Append(Spaces(Sep));
        sb.Append(Spaces(WPcie));
        sb.AppendLine();

        sb.Append(Pad("Id", WIdx)).Append(Spaces(Sep));
        sb.Append(Pad("Name", WName)).Append(Spaces(Sep));
        sb.Append(PadL("use", WUtil)).Append(Spaces(Sep));
        sb.Append(PadL("use", WMemUtil)).Append(Spaces(Sep));
        sb.Append(PadL("SM", WSm)).Append(Spaces(Sep));
        sb.Append(PadL("Temp", WTemp)).Append(Spaces(Sep));
        sb.Append(PadL("Power", WPower)).Append(Spaces(Sep));
        sb.Append(PadL("Memory", WMemory)).Append(Spaces(Sep));
        sb.Append(PadL("Clock", WMemClk)).Append(Spaces(Sep));
        sb.Append(PadL("PSt", WPSt)).Append(Spaces(Sep));
        sb.Append(PadL("Fan", WFan)).Append(Spaces(Sep));
        sb.Append(PadL("SysRAM", WSysRam)).Append(Spaces(Sep));
        sb.Append(PadL("PCIe", WPcie));
        sb.AppendLine();

        // Separator row (dashes).
        sb.Append(Dash(WIdx)).Append(Spaces(Sep));
        sb.Append(Dash(WName)).Append(Spaces(Sep));
        sb.Append(Dash(WUtil)).Append(Spaces(Sep));
        sb.Append(Dash(WMemUtil)).Append(Spaces(Sep));
        sb.Append(Dash(WSm)).Append(Spaces(Sep));
        sb.Append(Dash(WTemp)).Append(Spaces(Sep));
        sb.Append(Dash(WPower)).Append(Spaces(Sep));
        sb.Append(Dash(WMemory)).Append(Spaces(Sep));
        sb.Append(Dash(WMemClk)).Append(Spaces(Sep));
        sb.Append(Dash(WPSt)).Append(Spaces(Sep));
        sb.Append(Dash(WFan)).Append(Spaces(Sep));
        sb.Append(Dash(WSysRam)).Append(Spaces(Sep));
        sb.Append(Dash(WPcie));
        sb.AppendLine();

        // System RAM is machine-wide, not per-GPU: the same reading repeats on every
        // row so the table stays rectangular (the user asked for a column).
        var sysRam = SysRam(ramUsedBytes, ramTotalBytes);

        foreach (var g in s.Gpus)
        {
            sb.Append(Pad(g.Index.ToString(CultureInfo.InvariantCulture), WIdx)).Append(Spaces(Sep));
            sb.Append(Pad(g.Name, WName)).Append(Spaces(Sep));
            sb.Append(PadL(Pct(g.GpuUtilizationPct), WUtil)).Append(Spaces(Sep));
            sb.Append(PadL(Pct(g.MemoryUtilizationPct), WMemUtil)).Append(Spaces(Sep));
            sb.Append(PadL(Pct(g.SmUtilizationPct), WSm)).Append(Spaces(Sep));
            sb.Append(PadL(TempVal(g.TemperatureGpu), WTemp)).Append(Spaces(Sep));
            sb.Append(PadL(Power(g.PowerMw, g.EnforcedPowerLimitMw), WPower)).Append(Spaces(Sep));
            sb.Append(PadL(Memory(g.MemoryUsedBytes, g.MemoryTotalBytes), WMemory)).Append(Spaces(Sep));
            sb.Append(PadL(MemClkVal(g.MemoryClockMhz), WMemClk)).Append(Spaces(Sep));
            sb.Append(PadL(g.PerfStateLabel, WPSt)).Append(Spaces(Sep));
            sb.Append(PadL(Pct(g.FanSpeedPct), WFan)).Append(Spaces(Sep));
            sb.Append(PadL(sysRam, WSysRam)).Append(Spaces(Sep));
            sb.Append(PadL(Pcie(g), WPcie));
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

    /// <summary>Percentage: "34%" or N/A for negative sentinel.</summary>
    private static string Pct(int v) => v < 0 ? "N/A" : $"{v}%";

    /// <summary>Temperature: "30°C" or N/A for negative sentinel.</summary>
    private static string TempVal(int v) => v < 0 ? "N/A" : $"{v}\u00B0C";

    /// <summary>Memory clock: "9501MHz" or N/A for negative sentinel.</summary>
    private static string MemClkVal(int v) => v < 0 ? "N/A" : $"{v}MHz";

    /// <summary>Power: "usage / cap W" in whole watts; a negative usage sentinel renders N/A.</summary>
    private static string Power(int usageMw, int capMw) =>
        usageMw < 0 ? "N/A" : $"{Watts(usageMw)}/{Watts(capMw)}W";

    /// <summary>
    /// mW → whole watts (rounded): 14600 → "15", a valid 0 → "0"; a negative sentinel renders N/A.
    /// Invariant culture so no thousands separator is ever emitted.
    /// </summary>
    private static string Watts(int mw) =>
        mw < 0 ? "N/A" : ((int)Math.Round(mw / 1000.0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Memory: "used/total MB"; 0 total renders as N/A.</summary>
    private static string Memory(long usedBytes, long totalBytes) =>
        totalBytes <= 0 ? "N/A"
        : $"{Mb(usedBytes)}/{Mb(totalBytes)}MB";

    /// <summary>Bytes → MB (integer). Invariant culture so no thousands/comma separator is ever emitted.</summary>
    private static string Mb(long bytes) => (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// System RAM: "63% 41/64GB" — the used share of physical memory plus the
    /// actual used / total values in whole GB (GiB, 1024³ rounded — the same numbers
    /// Task Manager shows). A non-positive total (OS read failed) renders N/A; never
    /// divides by zero.
    /// </summary>
    private static string SysRam(long usedBytes, long totalBytes) =>
        totalBytes <= 0 ? "N/A"
        : $"{Pct((int)Math.Round(usedBytes * 100.0 / totalBytes))} {Gb(usedBytes)}/{Gb(totalBytes)}GB";

    /// <summary>
    /// Bytes → whole GB (GiB, 1024³, rounded away from zero): 83.1 → "83", 127.8 → "128".
    /// Invariant culture (no comma separator).
    /// </summary>
    private static string Gb(long bytes) =>
        ((long)Math.Round(bytes / (1024.0 * 1024 * 1024), MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// PCIe: "{gen}.0x{width}/{bandwidth}GB/s" — mirrors the GUI's PCIe values:
    /// the link config from <see cref="GpuSnapshot.PcieLabel"/> (the overview
    /// grid cell) plus the theoretical bandwidth in GB/s from
    /// <see cref="GpuSnapshot.PcieBandwidthGbps"/> (the charted "PCIe Bandwidth"
    /// card). "N/A" when NVML reports no link (gen or width == 0).
    /// </summary>
    private static string Pcie(GpuSnapshot g) =>
        g.PcieBandwidthGbps > 0
            ? $"{g.PcieLabel.Replace(" ", string.Empty, StringComparison.Ordinal)}/{g.PcieBandwidthGbps.ToString("0.##", CultureInfo.InvariantCulture)}GB/s"
            : "N/A";
}