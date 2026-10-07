namespace HectorBravo.Nvidia.Contracts;

/// <summary>
/// Result of a CSV or XML export. <see cref="FilePath"/> is null when the
/// operation failed; <see cref="Error"/> carries the reason.
/// </summary>
public sealed record ExportResult
{
    public bool Success { get; init; }
    public string? FilePath { get; init; }
    public string? Error { get; init; }
    public int GpuCount { get; init; }
    public int ProcessCount { get; init; }
}

/// <summary>
/// Serializes <see cref="SystemSnapshot"/>s to nvidia-smi-style CSV or XML.
/// Pure data → text; no NVML dependency.
/// </summary>
public static class Exporter
{
    public static string ToCsv(SystemSnapshot snapshot)
    {
        var sb = new System.Text.StringBuilder();

        // nvidia-smi style header rows
        sb.AppendLine("Index, Name, Driver, CUDA, Temp, Pwr, Pwr-Limit, GPU-Util, Mem-Util, Mem-Usage, Clock, Max-Clock, Bus-Id, Fan, Perf");
        sb.AppendLine("---------------------------------------------------------------------------------------------------------------------------------------------");
        foreach (var g in snapshot.Gpus)
        {
            sb.AppendLine(
                $"{g.Index}, {g.Name}, {snapshot.DriverVersion}, {snapshot.CudaDriverVersion}, " +
                $"{g.TemperatureGpu} C, {g.PowerMw / 1000.0:0.0} / {g.EnforcedPowerLimitMw / 1000.0:0.0} W, " +
                $"{g.GpuUtilizationPct} %, {g.MemoryUtilizationPct} %, " +
                $"{BytesToMiB(g.MemoryUsedBytes)} / {BytesToMiB(g.MemoryTotalBytes)} MiB, " +
                $"{g.GpuClockMhz} / {g.MaxGpuClockMhz} MHz, {g.BusId}, {g.FanSpeedPct} %, {g.PerfStateLabel}");
        }

        return sb.ToString();
    }

    public static string ToXml(SystemSnapshot snapshot)
    {
        var x = new System.Xml.Linq.XDocument(
            new System.Xml.Linq.XElement(
                "nvidia_system",
                new System.Xml.Linq.XAttribute("timestamp", snapshot.Timestamp.ToString("O")),
                new System.Xml.Linq.XAttribute("driver_version", snapshot.DriverVersion),
                new System.Xml.Linq.XAttribute("cuda_driver_version", snapshot.CudaDriverVersion),
                new System.Xml.Linq.XAttribute("nvml_version", snapshot.NvmlVersion)));

        var gpusEl = new System.Xml.Linq.XElement("gpus");
        foreach (var g in snapshot.Gpus)
        {
            gpusEl.Add(new System.Xml.Linq.XElement("gpu",
                new System.Xml.Linq.XAttribute("index", g.Index),
                new System.Xml.Linq.XAttribute("name", g.Name),
                new System.Xml.Linq.XAttribute("uuid", g.Uuid),
                new System.Xml.Linq.XAttribute("serial", g.Serial),
                new System.Xml.Linq.XAttribute("bus_id", g.BusId),
                new System.Xml.Linq.XAttribute("vbios", g.VbiosVersion),
                new System.Xml.Linq.XAttribute("temperature_c", g.TemperatureGpu),
                new System.Xml.Linq.XAttribute("memory_temp_c", g.TemperatureMemory),
                new System.Xml.Linq.XAttribute("power_w", g.PowerMw / 1000.0),
                new System.Xml.Linq.XAttribute("power_limit_w", g.EnforcedPowerLimitMw / 1000.0),
                new System.Xml.Linq.XAttribute("gpu_util_pct", g.GpuUtilizationPct),
                new System.Xml.Linq.XAttribute("mem_util_pct", g.MemoryUtilizationPct),
                new System.Xml.Linq.XAttribute("mem_used_mib", BytesToMiB(g.MemoryUsedBytes)),
                new System.Xml.Linq.XAttribute("mem_total_mib", BytesToMiB(g.MemoryTotalBytes)),
                new System.Xml.Linq.XAttribute("gpu_clock_mhz", g.GpuClockMhz),
                new System.Xml.Linq.XAttribute("mem_clock_mhz", g.MemoryClockMhz),
                new System.Xml.Linq.XAttribute("fan_pct", g.FanSpeedPct),
                new System.Xml.Linq.XAttribute("perf_state", g.PerfStateLabel),
                new System.Xml.Linq.XAttribute("persistence_mode", g.PersistenceModeEnabled ? "Enabled" : "Disabled")));
        }

        var procsEl = new System.Xml.Linq.XElement("processes");
        foreach (var p in snapshot.Processes)
        {
            procsEl.Add(new System.Xml.Linq.XElement("process",
                new System.Xml.Linq.XAttribute("gpu_index", p.GpuIndex),
                new System.Xml.Linq.XAttribute("pid", p.Pid),
                new System.Xml.Linq.XAttribute("name", p.ProcessName ?? string.Empty),
                new System.Xml.Linq.XAttribute("kind", p.Kind.ToString()),
                new System.Xml.Linq.XAttribute("mem_mib", BytesToMiB(p.MemoryUsedBytes))));
        }

        x.Root!.Add(gpusEl);
        x.Root.Add(procsEl);
        return x.ToString();
    }

    public static long BytesToMiB(long bytes) => bytes / (1024 * 1024);
}
