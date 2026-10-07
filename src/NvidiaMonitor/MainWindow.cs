using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    private readonly GpuHistory _history = new(capacity: 300);
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
    // Index order matches _charts/_specs: util, memUtil, temp, power, clock, memUsed.
    private readonly double[] _runningMax = { 100, 100, 100, 400, 2100, 16384 };

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

        for (int c = 0; c < _charts.Count; c++)
        {
            var spec = _specs[c];
            var seriesList = new List<KeyValuePair<int, IReadOnlyList<double>>>();
            double max = _runningMax[c];

            foreach (var gpu in _history.GpuIndices)
            {
                var series = spec.Selector(_history.GetSeries(gpu));
                var list = new List<double>(series.Count);
                foreach (var v in series)
                {
                    list.Add(v);
                    if (v > max) max = v;
                }
                seriesList.Add(new KeyValuePair<int, IReadOnlyList<double>>(gpu, list));
            }

            _runningMax[c] = max;
            double ceiling = c < 2 ? 100 : (max <= 0 ? _runningMax[c] : max * 1.1);
            _charts[c].SetSeries(seriesList, ceiling, spec.Title);
        }
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
        double Ceiling,
        Func<IReadOnlyList<HectorBravo.Nvidia.Contracts.GpuMetricSample>, IReadOnlyList<double>> Selector);

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
        _specs.Add(new ChartSpec("GPU Utilization (%)", 100,
            s => s.Select(x => (double)x.GpuUtilizationPct).ToList()));
        _specs.Add(new ChartSpec("Memory Utilization (%)", 100,
            s => s.Select(x => (double)x.MemoryUtilizationPct).ToList()));
        _specs.Add(new ChartSpec("Temperature (°C)", 100,
            s => s.Select(x => (double)x.TemperatureGpu).ToList()));
        _specs.Add(new ChartSpec("Power (W)", 400,
            s => s.Select(x => x.PowerWatts).ToList()));
        _specs.Add(new ChartSpec("GPU Clock (MHz)", 2100,
            s => s.Select(x => (double)x.GpuClockMhz).ToList()));
        _specs.Add(new ChartSpec("Memory Used (MiB)", 16384,
            s => s.Select(x => x.MemoryUsedBytes / 1048576.0).ToList()));
        foreach (var spec in _specs)
        {
            var chart = new MetricChart(spec.Title);
            _charts.Add(chart);
            stack.Children.Add(chart);
        }
        _chartsScroll = new System.Windows.Controls.ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
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
