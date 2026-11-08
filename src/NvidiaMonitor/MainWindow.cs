using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HectorBravo.Nvidia.Contracts;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// The main window: a dark-themed live monitor. On load it builds the NVML
/// engine via <see cref="GpuServiceFactory"/>, initializes it, and (if available)
/// drives a <see cref="GpuPollingService"/> that republishes snapshots ~1 Hz.
/// If NVML is unavailable it shows a friendly fallback and stops. Background
/// polling events are marshalled to the UI thread.
/// </summary>
public class MainWindow : Window
{
    private readonly AppLog _log = new();
    private IGpuMonitorService? _monitor;
    private GpuPollingService? _polling;
    // 12 min of 1 Hz samples, so a 10-minute window (plus a little history) fits.
    private readonly GpuHistory _history = new(capacity: 720);
    // Shared timeline / zoom / crosshair / scroll state that keeps all charts in lock-step.
    private readonly ChartController _chartCtl = new();
    private bool _disposed;
    private bool _closing; // set on Closed; stops in-flight UI marshalling

    private readonly TextBlock _driverText;
    private readonly TextBlock _statusText;
    private readonly DataGrid _grid;
    private readonly Grid _root;

    // ── tab control + per-tab widgets ──────────────────────────
    private readonly TabControl _tabs;
    // Assigned inside BuildTabs(), which the constructor calls before any use.
    private System.Windows.Controls.ScrollViewer _chartsScroll = null!;
    private readonly System.Collections.Generic.List<MetricChart> _charts = new();
    private readonly System.Collections.Generic.List<ChartSpec> _specs = new();

    // Per-chart running Y-axis ceiling (auto-scales upward; never shrinks).
    // Index order matches _charts/_specs: memUsed (0-100 % axis), util, memUtil, temp,
    // smUtil, power, pcieBw, clock, memClock — the percent charts all seed at 100.
    private readonly double[] _runningMax = { 100, 100, 100, 100, 100, 400, 32, 2100, 1600 };

    // One-shot fit-to-content startup sizing (see OnContentRendered).
    private readonly StackPanel _header;
    // Top-right per-device list (colour swatch + "cuda:N  card name"), one row per GPU.
    private readonly StackPanel _devices;
    private readonly Border _rootBorder;
    private bool _autoSized;
    private bool _dataReceived;

    public MainWindow()
    {
        Title = "NVIDIA GPU Monitor";
        // Default (restored) size. OnLayoutUpdated() resizes the window one time,
        // after the first data frame, so that all charts and the visible data fit
        // without the user having to resize it at startup (clamped to the work area).
        Width = 1040;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Bg;
        LayoutUpdated += OnLayoutUpdated;

        var title = new TextBlock
        {
            Text = "NVIDIA GPU Monitor",
            Foreground = Theme.Accent,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            FontFamily = Theme.Mono,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _driverText = new TextBlock
        {
            Text = "Initializing…",
            Foreground = Theme.Muted,
            FontFamily = Theme.Mono,
            FontSize = 12,
        };
        var buildTime = new TextBlock
        {
            Text = $"[{new FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).LastWriteTime:yyyy-MM-dd HH:mm}]",
            Foreground = Theme.White,
            FontFamily = Theme.Mono,
            FontSize = 12,
        };
        _header = new StackPanel
        {
            Children = { title, buildTime, _driverText },
            Margin = new Thickness(16, 12, 16, 8),
        };

        _grid = BuildGrid();
        _tabs = BuildTabs();

        _statusText = new TextBlock
        {
            Text = string.Empty,
            Foreground = Theme.Muted,
            FontFamily = Theme.Mono,
            FontSize = 11,
            Margin = new Thickness(16, 6, 16, 10),
        };

        // Right side of the header row: one line per device — a swatch in the same
        // colour as that GPU's chart series, plus "cuda:N  card name" — so the device
        // list doubles as the legend key for every chart below it.
        _devices = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 16, 8),
        };

        var headerRow = new Grid();
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_header, 0);
        Grid.SetColumn(_devices, 1);
        headerRow.Children.Add(_header);
        headerRow.Children.Add(_devices);

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(headerRow, 0);
        Grid.SetRow(_tabs, 1);
        Grid.SetRow(_statusText, 2);
        _root.Children.Add(headerRow);
        _root.Children.Add(_tabs);
        _root.Children.Add(_statusText);

        Content = _rootBorder = new Border { Background = Theme.Bg, Child = _root };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }
    // ── lifecycle ──────────────────────────────────────────────
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _monitor = GpuServiceFactory.CreateMonitor(_log);
        if (_monitor == null)
        {
            ShowFallback("No GPU monitoring service is available.");
            return;
        }

        var ok = await _monitor.InitializeAsync();
        if (!ok || !_monitor.IsAvailable)
        {
            ShowFallback(string.IsNullOrWhiteSpace(_monitor.StatusMessage)
                ? "NVML is not available on this system."
                : _monitor.StatusMessage);
            return;
        }

        // Live polling: events fire on a background thread → marshal to UI.
        _polling = new GpuPollingService(_monitor, _log, intervalMs: 1000);
        _polling.SnapshotAvailable += (_, snap) =>
            ToUi(() => ApplySnapshot(snap));
        _polling.StatusChanged += (_, msg) =>
            ToUi(() => OnStatusChanged(msg));

        _polling.Start();

        // Pull the first frame immediately so the UI isn't blank for a second.
        await _polling.RefreshNowAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Mark first so any in-flight background refresh bails before touching UI.
        _closing = true;
        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose the poller (stops its timer, so no new refreshes start).
        // We can't detach the `event` handlers here (events expose no setter
        // outside their declaring class), but every handler routes through
        // ToUi(), which no-ops once _closing/_disposed is set — so a single
        // in-flight refresh finishing after dispose is harmless.
        _polling?.Dispose();
        _polling = null;

        try
        {
            _monitor?.ShutdownAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // best effort — never let shutdown throw out of OnClosed.
        }
        _log.Dispose();
    }

    /// <summary>
    /// Marshals an action onto the UI thread, dropping it when the window is
    /// closing/disposed so a straggler background refresh can't touch controls.
    /// Uses BeginInvoke (not Invoke) to avoid blocking the worker thread.
    /// </summary>
    private void ToUi(Action action)
    {
        if (_closing || _disposed) return;
        try
        {
            Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher is shut down — ignore.
        }
    }

    /// <summary>
    /// Reacts to an availability/status change pushed by the poller. If NVML
    /// stops working mid-session (driver crash, GPU removed) show the fallback
    /// and stop the grid from being overwritten by stale/empty snapshots.
    /// </summary>
    private void OnStatusChanged(string message)
    {
        if (_monitor is null) return;

        if (!_monitor.IsAvailable)
        {
            _history.Clear(); // don't let stale chart data linger
            _chartCtl.ClearCross(); // hide the red crosshair on NVML loss
            _polling?.Pause();
            ShowFallback(string.IsNullOrWhiteSpace(message)
                ? "NVML is not available on this system."
                : message);
            return;
        }

        // Still available — just refresh the one-line status text.
        _statusText.Text = string.IsNullOrWhiteSpace(message) ? string.Empty : message;
    }

    /// <summary>Swaps the content for a friendly "no NVML" screen and stops polling.</summary>
    private void ShowFallback(string message)
    {
        _driverText.Text = string.Empty;
        _statusText.Text = string.Empty;

        var text = new TextBlock
        {
            Text = $"NVML is unavailable\n\n{message}",
            Foreground = Theme.Warn,
            FontFamily = Theme.Mono,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Content = new Border { Background = Theme.Bg, Child = text };
    }
    // ── rendering ──────────────────────────────────────────────
    /// <summary>Called on the UI thread with the latest snapshot.</summary>
    private void ApplySnapshot(SystemSnapshot snap)
    {
        _dataReceived = true;
        _driverText.Text = BuildVersionLine(snap);
        UpdateDevicesPanel(snap);

        // Feed the bounded time-series history (used by future charts).
        foreach (var g in snap.Gpus)
        {
            _history.Add(new GpuMetricSample
            {
                GpuIndex = g.Index,
                GpuUtilizationPct = g.GpuUtilizationPct,
                MemoryUtilizationPct = g.MemoryUtilizationPct,
                SmUtilizationPct = g.SmUtilizationPct,
                PowerWatts = g.PowerMw / 1000.0,
                MemoryUsedBytes = g.MemoryUsedBytes,
                TemperatureGpu = g.TemperatureGpu,
                GpuClockMhz = g.GpuClockMhz,
                MemoryClockMhz = g.MemoryClockMhz,
                PcieBandwidthGbps = g.PcieBandwidthGbps,
                PcieMaxBandwidthGbps = g.PcieMaxBandwidthGbps,
                PcieLinkGen = g.PcieLinkGen,
                PcieLinkWidth = g.PcieLinkWidth,
                MemoryTotalBytes = g.MemoryTotalBytes,
                PowerCapWatts = g.EnforcedPowerLimitMw > 0 ? g.EnforcedPowerLimitMw / 1000.0 : 0.0,
                Timestamp = snap.Timestamp,
            });
        }

        var rows = new List<GpuRow>(snap.Gpus.Count);
        foreach (var g in snap.Gpus)
        {
            rows.Add(new GpuRow
            {
                Index = g.Index.ToString(),
                Name = string.IsNullOrWhiteSpace(g.Name) ? $"GPU {g.Index}" : g.Name,
                GpuUtil = PctOrNa(g.GpuUtilizationPct),
                MemUtil = PctOrNa(g.MemoryUtilizationPct),
                Temp = g.TemperatureGpu > 0 ? $"{g.TemperatureGpu} °C" : "N/A",
                Power = PowerUsageCapOrNa(g.PowerMw, g.EnforcedPowerLimitMw),
                MemUsage = Theme.Mb(g.MemoryUsedBytes) + " / " + Theme.Mb(g.MemoryTotalBytes) + " MB",
                MemClock = g.MemoryClockMhz > 0 ? $"{g.MemoryClockMhz} MHz" : "N/A",
                Fan = g.FanSpeedPct > 0 ? $"{g.FanSpeedPct} %" : "N/A",
                PState = g.PerfStateLabel,
                Pcie = g.PcieLabel,
            });
        }
        _grid.ItemsSource = rows;

        // ── Charts tab: per-metric series dict from the history ──
        UpdateCharts();

        _statusText.Text = string.IsNullOrWhiteSpace(_monitor?.StatusMessage)
            ? string.Empty
            : _monitor!.StatusMessage;
    }

    /// <summary>
    /// Rebuilds the top-right per-device list: a colour swatch (the same colour as
    /// that GPU's chart series) plus "cuda:N  card name", one row per visible device.
    /// </summary>
    private void UpdateDevicesPanel(SystemSnapshot snap)
    {
        _devices.Children.Clear();
        for (int i = 0; i < snap.Gpus.Count; i++)
        {
            var g = snap.Gpus[i];
            string name = string.IsNullOrWhiteSpace(g.Name) ? $"GPU {i}" : g.Name;
            var swatch = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = MetricChart.Palette[i % MetricChart.Palette.Length],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var text = new TextBlock
            {
                Foreground = Theme.Fg,
                FontFamily = Theme.Mono,
                FontSize = 12,
            };
            text.Inlines.Add(new System.Windows.Documents.Run($"cuda:{i}") { Foreground = Theme.Muted });
            text.Inlines.Add(new System.Windows.Documents.Run($"  {name}"));
            // The card's own max memory right after its name — this is exactly the
            // denominator the Memory-Used percentage chart divides by, and it differs
            // from card to card, so the user can see which 100 % each series means.
            var maxMemLabel = g.MemoryTotalBytes > 0 ? $" ({Theme.Mb(g.MemoryTotalBytes)} MiB)" : " (N/A)";
            text.Inlines.Add(new System.Windows.Documents.Run(maxMemLabel) { Foreground = Theme.Muted });
            _devices.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 2),
                Children = { swatch, text },
            });
        }
    }

    /// <summary>
    /// (Re)builds every <see cref="MetricChart"/> from <see cref="_history"/>.
    /// Called on the UI thread from <see cref="ApplySnapshot"/>; idempotent.
    /// </summary>
    private void UpdateCharts()
    {
        if (_charts.Count == 0) return;

        // Fetch each GPU's samples once per tick (avoids repeated lock + ToArray copies).
        var samplesByGpu = new Dictionary<int, IReadOnlyList<GpuMetricSample>>();
        int n = 0;
        int firstGpu = 0;
        foreach (var gpu in _history.GpuIndices)
        {
            var arr = _history.GetSeries(gpu);
            samplesByGpu[gpu] = arr;
            if (arr.Count > n)
            {
                n = arr.Count;
                firstGpu = gpu;
            }
        }
        if (n == 0) return;

        // Shared timeline (seconds since start) from the longest series, so it spans the
        // full history. Per-GPU value arrays are aligned to it below (front-padded with
        // zeros when that GPU started later or stopped earlier, so the most-recent samples
        // always sit at the right edge). Anchored at t=0: before the window fills, data
        // plots from the left; once it overflows, the view pins to the newest and scrolls
        // left back toward start.
        var t0 = samplesByGpu[firstGpu][0].Timestamp;
        var timeline = new double[n];
        for (int i = 0; i < n; i++)
            timeline[i] = (samplesByGpu[firstGpu][i].Timestamp - t0).TotalSeconds;

        for (int c = 0; c < _charts.Count; c++)
        {
            var spec = _specs[c];
            var seriesList = new List<KeyValuePair<int, double[]>>();
            var pcieGenMap = new List<KeyValuePair<int, int[]>>();
            var pcieWidthMap = new List<KeyValuePair<int, int[]>>();
            double max = _runningMax[c];

            // For percentage charts: the current denominator (total memory in MiB /
            // enforced power cap in W) taken from each GPU's newest sample.
            var pctBase = new List<KeyValuePair<int, double>>();
            if (spec.Pct && spec.PctBase != null)
            {
                foreach (var kv in samplesByGpu)
                {
                    double denom = spec.PctBase(kv.Value[^1]);
                    if (denom > 0) pctBase.Add(new KeyValuePair<int, double>(kv.Key, denom));
                }
            }

            foreach (var kv in samplesByGpu)
            {
                var src = spec.Selector(kv.Value);
                int m = src.Count;
                if (m == 0) continue;

                // Align this GPU's values to the shared timeline length (same 1 Hz cadence,
                // so the newest samples matter). Front-pad with zeros if it has fewer
                // samples; never trim the tail.
                double[] arr = new double[n];
                int srcStart = Math.Max(0, m - n); // oldest sample we keep
                int lead = n - (m - srcStart); // leading zeros = n - samplesKept
                if (lead > 0)
                {
                    for (int i = 0; i < lead; i++) arr[i] = 0;
                    for (int i = 0; i < n - lead; i++) arr[lead + i] = src[srcStart + i];
                }
                else
                {
                    for (int i = 0; i < n; i++) arr[i] = src[m - n + i];
                }

                for (int i = 0; i < n; i++)
                {
                    if (arr[i] > max) max = arr[i];
                }
                seriesList.Add(new KeyValuePair<int, double[]>(kv.Key, arr));

                // For the PCIe chart, keep the link gen/width per sample (aligned to the
                // shared timeline exactly like arr) so the legend/hover can show the link
                // config at any point in the history, not just the current one.
                if (spec.Pcie)
                {
                    int[] genArr = new int[n];
                    int[] widthArr = new int[n];
                    for (int i = 0; i < n - lead; i++)
                    {
                        genArr[lead + i] = kv.Value[srcStart + i].PcieLinkGen;
                        widthArr[lead + i] = kv.Value[srcStart + i].PcieLinkWidth;
                    }
                    pcieGenMap.Add(new KeyValuePair<int, int[]>(kv.Key, genArr));
                    pcieWidthMap.Add(new KeyValuePair<int, int[]>(kv.Key, widthArr));
                }
            }

            _runningMax[c] = max;

            // PCIe chart: use the max *possible* bandwidth of the fastest card as the
            // ceiling so a degraded link sits visibly low on the log scale.
            double ceiling;
            if (spec.LogScale)
            {
                double maxPossible = 0;
                foreach (var kv in samplesByGpu)
                    for (int i = 0; i < kv.Value.Count; i++)
                    {
                        double v = kv.Value[i].PcieMaxBandwidthGbps;
                        if (v > maxPossible) maxPossible = v;
                    }
                ceiling = maxPossible > 0 ? maxPossible : 32; // fallback: PCIe 4.0 x16
            }
            else
            {
                ceiling = Math.Max(spec.MinCeiling, spec.Percent ? 100 : (max <= 0 ? _runningMax[c] : max * 1.1));
            }

            _charts[c].SetSeries(timeline, seriesList, ceiling, spec.Title, spec.Unit, spec.Decimals,
                spec.Pcie ? pcieGenMap : null, spec.Pcie ? pcieWidthMap : null,
                spec.Pct ? pctBase : null);
        }

        // All charts share the timeline (same samples), so one combined tick resizes + follows +
        // redraws every chart — far cheaper than a full all-charts redraw per chart per second.
        _chartCtl.OnDataTick();
    }

    private static string PctOrNa(int value)
        => value < 0 ? "N/A" : $"{value} %";

    private static string PowerUsageCapOrNa(int usageMw, int capMw)
        => usageMw < 0 ? "N/A" : $"{WattsOnly(usageMw)} / {WattsOnly(capMw)} W";

    private static string WattsOnly(int mw)
        => mw < 0 ? "N/A" : Theme.Watts(mw);

    private static string BuildVersionLine(SystemSnapshot snap)
    {
        static string F(string label, string v)
            => string.IsNullOrWhiteSpace(v) ? $"{label}: —" : $"{label}: {v}";
        return $"{F("Driver", snap.DriverVersion)}   |   {F("CUDA", snap.CudaDriverVersion)}   |   {F("NVML", snap.NvmlVersion)}";
    }

    /// <summary>Builds the DataGrid with a column per monitored metric.</summary>
    private static DataGrid BuildGrid()
    {
        var grid = Theme.MakeDataGrid("GPUs");
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.Margin = new Thickness(16, 0, 16, 0);

        grid.Columns.Add(NumCol("GPU", "Index", alignRight: false));
        grid.Columns.Add(NumCol("Name", "Name", alignRight: false));
        grid.Columns.Add(NumCol("GPU Util", "GpuUtil"));
        grid.Columns.Add(NumCol("Mem Util", "MemUtil"));
        grid.Columns.Add(NumCol("Temp", "Temp"));
        grid.Columns.Add(NumCol("Power (usage/cap)", "Power"));
        grid.Columns.Add(NumCol("Memory (used/total)", "MemUsage"));
        grid.Columns.Add(NumCol("Mem Clock", "MemClock"));
        grid.Columns.Add(NumCol("Fan", "Fan"));
        grid.Columns.Add(NumCol("P-State", "PState"));
        grid.Columns.Add(NumCol("PCIe", "Pcie", alignRight: false));

        return grid;
    }

    private static DataGridTextColumn NumCol(string header, string path, bool alignRight = true)
    {
        var col = new DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(path),
            IsReadOnly = true,
            Width = DataGridLength.Auto,
        };
        if (alignRight)
        {
            col.ElementStyle = MakeRightAlign();
        }
        return col;
    }

    private static Style? _rightAlign;
    private static Style MakeRightAlign()
    {
        if (_rightAlign != null) return _rightAlign;
        _rightAlign = new Style(typeof(TextBlock));
        _rightAlign.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        _rightAlign.Setters.Add(new Setter(TextBlock.FontFamilyProperty, Theme.Mono));
        return _rightAlign;
    }

    /// <summary>Per-row view model holding pre-formatted (N/A-aware) strings.</summary>
    public sealed class GpuRow
    {
        public string Index { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string GpuUtil { get; init; } = string.Empty;
        public string MemUtil { get; init; } = string.Empty;
        public string Temp { get; init; } = string.Empty;
        public string Power { get; init; } = string.Empty;
        public string MemUsage { get; init; } = string.Empty;
        public string MemClock { get; init; } = string.Empty;
        public string Fan { get; init; } = string.Empty;
        public string PState { get; init; } = string.Empty;
        public string Pcie { get; init; } = string.Empty;
    }

    /// <summary>
    /// Declares one charted metric: its title, the Y-axis ceiling, and a selector
    /// that maps a GPU's <see cref="HectorBravo.Nvidia.Contracts.GpuMetricSample"/>
    /// series to the doubles to plot.
    /// </summary>
    private sealed record ChartSpec(
        string Title,
        string Unit,
        int Decimals,
        Func<IReadOnlyList<HectorBravo.Nvidia.Contracts.GpuMetricSample>, List<double>> Selector,
        bool LogScale = false,
        bool Pcie = false,
        bool Pct = false,
        // Fixed 0–100 Y axis (percentage charts): the ceiling never auto-grows.
        bool Percent = false,
        double MinCeiling = 0,
        Func<HectorBravo.Nvidia.Contracts.GpuMetricSample, double>? PctBase = null,
        // The values are already percentages of PctBase (Memory Used): the readout then
        // leads with "NN %" and shows the absolute amount in parentheses.
        bool PctValue = false,
        // Card starts collapsed to its header row (the top-left chevron expands it), so
        // the fit-to-content window opens shorter.
        bool Collapsed = false);

    /// <summary>Builds the 2-tab control (Overview / Charts); Charts is the default tab.</summary>
    private TabControl BuildTabs()
    {
        var tabs = new TabControl
        {
            Background = Theme.Bg,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(8, 0, 8, 0),
        };
        StyleTabControl(tabs);

        // ── Overview ────────────────────────────────────────────
        var overview = new TabItem { Header = "Overview", Content = _grid };

        // ── Charts ──────────────────────────────────────────────
        var stack = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        // Memory Used heads the stack: it is a percentage of *this card's own* memory
        // (the total NVML reports for it), so cards with different VRAM sizes share one
        // 0-100 % axis. The same denominator (in MiB) feeds the readout's "(NNNN MiB)"
        // amount, which goes *after* the percentage (PctValue); the unit is "%" with no
        // space so the legend reads "80% (19739 MiB)". The cast is mandatory: used/total
        // are longs, so long/long would truncate to 0/1.
        _specs.Add(new ChartSpec("Memory Used (%)", "%", 0,
            s => s.Select(x => x.MemoryTotalBytes > 0
                ? (double)x.MemoryUsedBytes / x.MemoryTotalBytes * 100.0
                : 0.0).ToList(),
            Pct: true,
            Percent: true,
            PctBase: s => s.MemoryTotalBytes / 1048576.0,
            PctValue: true));
        // Only Memory Utilization and Memory Used open expanded; the other seven cards start
        // collapsed (header row only, the top-left chevron expands them), so the
        // fit-to-content window opens as short as it can.
        _specs.Add(new ChartSpec("GPU Utilization (%)", "%", 0,
            s => s.Select(x => (double)x.GpuUtilizationPct).ToList(),
            Percent: true,
            Collapsed: true));
        _specs.Add(new ChartSpec("Memory Utilization (%)", "%", 0,
            s => s.Select(x => (double)x.MemoryUtilizationPct).ToList(),
            Percent: true));
        _specs.Add(new ChartSpec("Temperature (°C)", "°C", 0,
            s => s.Select(x => (double)x.TemperatureGpu).ToList(),
            MinCeiling: 100,
            Collapsed: true));
        _specs.Add(new ChartSpec("SM Utilization (%)", "%", 0,
            s => s.Select(x => (double)x.SmUtilizationPct).ToList(),
            Percent: true,
            MinCeiling: 100,
            Collapsed: true));
        _specs.Add(new ChartSpec("Power (W)", " W", 1,
            s => s.Select(x => x.PowerWatts).ToList(),
            Pct: true,
            PctBase: s => s.PowerCapWatts,
            Collapsed: true));
        _specs.Add(new ChartSpec("PCIe Bandwidth (GB/s)", " GB/s", 1,
            s => s.Select(x => x.PcieBandwidthGbps).ToList(),
            LogScale: true,
            Pcie: true,
            Collapsed: true));
        _specs.Add(new ChartSpec("GPU Clock (MHz)", " MHz", 0,
            s => s.Select(x => (double)x.GpuClockMhz).ToList(),
            Collapsed: true));
        _specs.Add(new ChartSpec("Memory Clock (MHz)", " MHz", 0,
            s => s.Select(x => (double)x.MemoryClockMhz).ToList(),
            Collapsed: true));
        foreach (var spec in _specs)
        {
            var chart = new MetricChart(spec.Title);
            chart.Bind(_chartCtl); // before the flags: SetCollapsed repaints via Redraw
            if (spec.LogScale) chart.SetLogScale(true);
            if (spec.PctValue) chart.SetPctValue(true);
            if (spec.Collapsed) chart.SetCollapsed(true);
            _charts.Add(chart);
            stack.Children.Add(chart);
        }

        // CTRL + mouse-wheel zoom: changes how many minutes fit the view, across all charts.
        // The vertical bar is *always* reserved (Visible, not Auto): an Auto bar takes 0 px
        // until it appears, so once the chart stack outgrows the window the bar would add its
        // 13 px and the layout would shift. Reserving it from the first layout keeps the
        // window stable (T14).
        _chartsScroll = new System.Windows.Controls.ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _chartsScroll.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            _chartCtl.Zoom(e.Delta < 0);
            e.Handled = true;
        };
        // Keep the shared viewport width current (drives the pixels-per-second scale).
        // Drag-and-drop reorder is handled on each chart panel itself (a panel is a drop target).
        _chartsScroll.SizeChanged += (_, __) => _chartCtl.SetViewportWidth(Math.Max(0, _chartsScroll.ViewportWidth));
        _chartCtl.SetViewportWidth(Math.Max(0, _chartsScroll.ViewportWidth));
        var chartsTab = new TabItem { Header = "Charts", Content = _chartsScroll };

        tabs.Items.Add(overview);
        tabs.Items.Add(chartsTab);
        // Default to the Charts tab on startup (Overview is index 0, Charts is 1).
        tabs.SelectedIndex = 1;

        return tabs;
    }

    /// <summary>
    /// Applies the dark theme to the tab headers. Unselected headers use the muted
    /// colour; the selected header lights up with the NVIDIA-green accent (matching
    /// the chart aesthetic) via a trigger.
    /// </summary>
    private static void StyleTabControl(TabControl tabs)
    {
        var headerStyle = new Style(typeof(TabItem));
        headerStyle.Setters.Add(new Setter(TabItem.ForegroundProperty, Theme.Muted));
        headerStyle.Setters.Add(new Setter(TabItem.FontFamilyProperty, Theme.Mono));
        headerStyle.Setters.Add(new Setter(TabItem.FontSizeProperty, 12.0));
        headerStyle.Setters.Add(new Setter(TabItem.PaddingProperty, new Thickness(12, 6, 12, 6)));

        var selected = new Trigger
        {
            Property = TabItem.IsSelectedProperty,
            Value = true,
        };
        selected.Setters.Add(new Setter(TabItem.ForegroundProperty, Theme.Accent));
        headerStyle.Triggers.Add(selected);

        tabs.ItemContainerStyle = headerStyle;
    }

    // ── one-shot fit-to-content startup sizing ─────────────────────
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        // Keep the green "slide" scrollbar style on every realized scrollbar.
        // Re-running on every render is deliberate: the Overview DataGrid's own
        // scrollbars only exist in the visual tree after that tab has been
        // shown at least once.
        Theme.ApplyScrollStyles(this);

        // One-time sizing (skipped on the "no NVML" fallback screen, which keeps
        // the default 1040×640): derive the window width from an unconstrained
        // content measure, then re-measure the content at that *realised* width
        // (at infinite width the chart header rows never wrap, so the first pass
        // underestimates their height), add the fixed chrome — including the real
        // tab-strip height from the TabControl template part — clamp to the work
        // area, and re-centre.
        if (_autoSized) return;
        if (Content is not Border b || !ReferenceEquals(b, _rootBorder)) return;
        if (!_dataReceived) return;
        _autoSized = true;

        var work = SystemParameters.WorkArea;

        double contentW = 0, contentH = 0;
        MeasureFit(_grid, ref contentW, ref contentH);
        if (_chartsScroll.Content is FrameworkElement chartsStack)
            MeasureFit(chartsStack, ref contentW, ref contentH);
        double w = Math.Clamp(Math.Max(contentW, 900), 720, work.Width - 16);

        // Re-measure at the realised content width (window width minus the tab margins and
        // the charts viewer's always-reserved 13 px vertical bar) so wrapped header rows
        // are counted at their true height.
        double contentH2 = 0;
        if (_chartsScroll.Content is FrameworkElement cs)
        {
            cs.Measure(new Size(Math.Max(400, w - 61), double.PositiveInfinity));
            contentH2 = cs.DesiredSize.Height;
        }

        // The real tab-strip height comes from the TabControl template part, not
        // from item containers (the non-selected ones report a collapsed height).
        double strip = 34; // fallback tab-strip height
        if (_tabs.Template is ControlTemplate tpl &&
            tpl.FindName("PART_HeaderPanel", _tabs) is FrameworkElement hp && hp.ActualHeight > 0)
        {
            strip = hp.ActualHeight;
        }

        // Fixed chrome: window border/frame + title bar + tab strip + header row
        // (taller of the left header / right device list) + status row + slack.
        double chrome = SystemParameters.WindowResizeBorderThickness.Top
            + SystemParameters.WindowResizeBorderThickness.Bottom
            + SystemParameters.WindowCaptionHeight
            + strip
            + Math.Max(_header.ActualHeight, _devices.ActualHeight)
            + _statusText.ActualHeight
            + 20;

        double h = Math.Clamp(Math.Max(contentH, contentH2) + chrome, 420, work.Height - 16);
        Width = w;
        Height = h;

        // The window opened at its default size; re-centre it in the work area
        // at the new size so it stays where the user expects.
        Left = work.Left + Math.Max(0, (work.Width - w) / 2);
        Top = work.Top + Math.Max(0, (work.Height - h) / 2);

        static void MeasureFit(FrameworkElement el, ref double w, ref double h)
        {
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (el.DesiredSize.Height > 0)
            {
                w = Math.Max(w, el.DesiredSize.Width);
                h = Math.Max(h, el.DesiredSize.Height);
            }
        }
    }
}
