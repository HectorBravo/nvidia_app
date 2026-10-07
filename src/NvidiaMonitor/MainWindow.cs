using System;
using System.Collections.Generic;
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
        Grid.SetRow(_grid, 1);
        Grid.SetRow(_statusText, 2);
        _root.Children.Add(header);
        _root.Children.Add(_grid);
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
                Power = WattsOrNa(g.PowerMw),
                MemUsage = Theme.Mb(g.MemoryUsedBytes) + " / " + Theme.Mb(g.MemoryTotalBytes) + " MB",
                MemClock = g.MemoryClockMhz > 0 ? $"{g.MemoryClockMhz} MHz" : "N/A",
                Fan = g.FanSpeedPct > 0 ? $"{g.FanSpeedPct} %" : "N/A",
                PState = g.PerfStateLabel,
            });
        }
        _grid.ItemsSource = rows;

        _statusText.Text = string.IsNullOrWhiteSpace(_monitor?.StatusMessage)
            ? string.Empty
            : _monitor!.StatusMessage;
    }

    private static string PctOrNa(int value)
        => value < 0 ? "N/A" : value + "%";

    private static string WattsOrNa(int mw)
        => mw <= 0 ? "N/A" : Theme.Watts(mw) + " W";

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
        grid.Columns.Add(NumCol("Power", "Power"));
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
}
