using System;
using System.Collections.Generic;
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
    private DataGrid _processGrid = null!;
    private ListBox _logList = null!;
    private Button _clearLogBtn = null!;
    private System.Windows.Controls.ScrollViewer _chartsScroll = null!;
    private readonly System.Collections.Generic.List<MetricChart> _charts = new();
    private readonly System.Collections.Generic.List<ChartSpec> _specs = new();

    // Per-chart running Y-axis ceiling (auto-scales upward; never shrinks).
    // Index order matches _charts/_specs: util, memUtil, temp, power, clock, memUsed, pcieBw.
    private readonly double[] _runningMax = { 100, 100, 100, 400, 2100, 16384, 32 };

    public MainWindow()
    {
        Title = "NVIDIA GPU Monitor";
        Width = 1040;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Bg;

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
        var header = new StackPanel
        {
            Children = { title, _driverText },
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

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(_tabs, 1);
        Grid.SetRow(_statusText, 2);
        _root.Children.Add(header);
        _root.Children.Add(_tabs);
        _root.Children.Add(_statusText);

        Content = new Border { Background = Theme.Bg, Child = _root };

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

        // Activity Log tab: seed + live-append (subscribed once, on the UI thread).
        foreach (var entry in _log.Snapshot())
        {
            _logList.Items.Add(entry.Formatted);
        }
        _logList.ScrollIntoView(_logList.Items.Count > 0 ? _logList.Items[^1] : null);
        _log.EntryAdded += (_, entry) =>
            ToUi(() => AppendLog(entry));

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
        _driverText.Text = BuildVersionLine(snap);

        // Feed the bounded time-series history (used by future charts).
        foreach (var g in snap.Gpus)
        {
            _history.Add(new GpuMetricSample
            {
                GpuIndex = g.Index,
                GpuUtilizationPct = g.GpuUtilizationPct,
                MemoryUtilizationPct = g.MemoryUtilizationPct,
                PowerWatts = g.PowerMw / 1000.0,
                MemoryUsedBytes = g.MemoryUsedBytes,
                TemperatureGpu = g.TemperatureGpu,
                GpuClockMhz = g.GpuClockMhz,
                PcieBandwidthGbps = g.PcieBandwidthGbps,
                PcieMaxBandwidthGbps = g.PcieMaxBandwidthGbps,
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

        // ── Processes tab: rebuild from the flat per-snapshot list ──
        var procs = new List<ProcessRow>(snap.Processes.Count);
        foreach (var p in snap.Processes)
        {
            procs.Add(new ProcessRow
            {
                Gpu = p.GpuIndex.ToString(),
                Pid = p.Pid.ToString(),
                Name = string.IsNullOrWhiteSpace(p.ProcessName) ? $"pid {p.Pid}" : p.ProcessName,
                Kind = p.Kind.ToString(),
                Memory = Theme.Mb(p.MemoryUsedBytes) + " MB",
            });
        }
        _processGrid.ItemsSource = procs;

        // ── Charts tab: per-metric series dict from the history ──
        UpdateCharts();

        _statusText.Text = string.IsNullOrWhiteSpace(_monitor?.StatusMessage)
            ? string.Empty
            : _monitor!.StatusMessage;
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
            double max = _runningMax[c];

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
                ceiling = c < 2 ? 100 : (max <= 0 ? _runningMax[c] : max * 1.1);
            }

            _charts[c].SetSeries(timeline, seriesList, ceiling, spec.Title, spec.Unit, spec.Decimals);
        }

        // All charts share the timeline (same samples), so one combined tick resizes + follows +
        // redraws every chart — far cheaper than a full all-charts redraw per chart per second.
        _chartCtl.OnDataTick();
    }

    /// <summary>Appends one activity-log entry to the Log tab and scrolls to it.</summary>
    private void AppendLog(HectorBravo.Nvidia.Contracts.AppLogEntry entry)
    {
        _logList.Items.Add(entry.Formatted);
        _logList.ScrollIntoView(_logList.Items[^1]);
    }

    /// <summary>Clears the in-memory log and the Log tab's UI list (UI thread).</summary>
    private void ClearLog()
    {
        _log.Clear();
        _logList.Items.Clear();
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

    /// <summary>Per-row view model for the Processes tab (pre-formatted strings).</summary>
    public sealed class ProcessRow
    {
        public string Gpu { get; init; } = string.Empty;
        public string Pid { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Memory { get; init; } = string.Empty;
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
        bool LogScale = false);

    /// <summary>Builds the 4-tab control (Overview / Charts / Processes / Log).</summary>
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
        _specs.Add(new ChartSpec("GPU Utilization (%)", "%", 0,
            s => s.Select(x => (double)x.GpuUtilizationPct).ToList()));
        _specs.Add(new ChartSpec("Memory Utilization (%)", "%", 0,
            s => s.Select(x => (double)x.MemoryUtilizationPct).ToList()));
        _specs.Add(new ChartSpec("Temperature (°C)", "°C", 0,
            s => s.Select(x => (double)x.TemperatureGpu).ToList()));
        _specs.Add(new ChartSpec("Power (W)", " W", 1,
            s => s.Select(x => x.PowerWatts).ToList()));
        _specs.Add(new ChartSpec("GPU Clock (MHz)", " MHz", 0,
            s => s.Select(x => (double)x.GpuClockMhz).ToList()));
        _specs.Add(new ChartSpec("Memory Used (MiB)", " MiB", 0,
            s => s.Select(x => x.MemoryUsedBytes / 1048576.0).ToList()));
        _specs.Add(new ChartSpec("PCIe Bandwidth (GB/s)", " GB/s", 1,
            s => s.Select(x => x.PcieBandwidthGbps).ToList(),
            LogScale: true));
        foreach (var spec in _specs)
        {
            var chart = new MetricChart(spec.Title);
            if (spec.LogScale) chart.SetLogScale(true);
            _charts.Add(chart);
            chart.Bind(_chartCtl);
            stack.Children.Add(chart);
        }

        // CTRL + mouse-wheel zoom: changes how many minutes fit the view, across all charts.
        _chartsScroll = new System.Windows.Controls.ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
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

        // ── Processes ───────────────────────────────────────────
        _processGrid = BuildProcessGrid();
        var processesTab = new TabItem { Header = "Processes", Content = _processGrid };

        // ── Log ─────────────────────────────────────────────────
        _logList = new ListBox
        {
            Background = Theme.Bg,
            Foreground = Theme.Fg,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            FontFamily = Theme.Mono,
            FontSize = 12,
            Padding = new Thickness(6),
        };
        _clearLogBtn = new Button
        {
            Content = "Clear",
            Foreground = Theme.Fg,
            Background = Theme.Panel,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _clearLogBtn.Click += (_, _) => ClearLog();
        var logHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        logHeader.Children.Add(Theme.MakeLabel("Activity log"));
        logHeader.Children.Add(_clearLogBtn);
        var logStack = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        logStack.Children.Add(logHeader);
        logStack.Children.Add(_logList);
        var logTab = new TabItem { Header = "Log", Content = logStack };

        tabs.Items.Add(overview);
        tabs.Items.Add(chartsTab);
        tabs.Items.Add(processesTab);
        tabs.Items.Add(logTab);

        return tabs;
    }

    /// <summary>Builds the DataGrid that lists every running process, per GPU.</summary>
    private static DataGrid BuildProcessGrid()
    {
        var grid = Theme.MakeDataGrid("Processes");
        grid.HorizontalAlignment = HorizontalAlignment.Stretch;
        grid.Margin = new Thickness(16, 0, 16, 0);
        grid.Columns.Add(NumCol("GPU", "Gpu", alignRight: false));
        grid.Columns.Add(NumCol("PID", "Pid", alignRight: false));
        grid.Columns.Add(NumCol("Process", "Name", alignRight: false));
        grid.Columns.Add(NumCol("Kind", "Kind", alignRight: false));
        grid.Columns.Add(NumCol("Mem (MB)", "Memory"));
        return grid;
    }

    /// <summary>Applies the dark theme to the tab control and its headers.</summary>
    private static void StyleTabControl(TabControl tabs)
    {
        var headerStyle = new Style(typeof(TabItem));
        headerStyle.Setters.Add(new Setter(TabItem.ForegroundProperty, Theme.Muted));
        headerStyle.Setters.Add(new Setter(TabItem.FontFamilyProperty, Theme.Mono));
        headerStyle.Setters.Add(new Setter(TabItem.FontSizeProperty, 12.0));
        headerStyle.Setters.Add(new Setter(TabItem.PaddingProperty, new Thickness(12, 6, 12, 6)));
        tabs.ItemContainerStyle = headerStyle;
    }
}
