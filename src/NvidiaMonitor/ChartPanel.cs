using System;
using System.Linq;
using System.Windows;
using System.Windows.Shapes;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// A self-contained live line chart drawn entirely with native WPF shapes
/// (<see cref="Polyline"/> + <see cref="Line"/> — no third-party charting library).
/// It plots ONE metric over time: each GPU is one polyline overlaid on the others,
/// coloured from a small palette. The control is a <see cref="Grid"/> with a
/// header row (title + per-GPU legend) and a <see cref="Canvas"/> plot area, all
/// wrapped in a themed border so it reads cleanly on the dark background.
///
/// <see cref="SetSeries"/> is idempotent: it clears and rebuilds the plot on every
/// call. Drawing is skipped until the canvas has a non-zero size (it redraws on
/// <see cref="System.Windows.FrameworkElement.SizeChanged"/> and on the next data
/// tick once sized).
/// </summary>
public class MetricChart : Border
{
    // ── palette ────────────────────────────────────────────────
    // GPU 0 is always the NVIDIA green; extra GPUs cycle through distinguishable
    // hues (teal → gold → blue → pink). The index is stable across charts, so the
    // same GPU keeps the same colour on every metric.
    private static readonly Brush[] Palette =
    {
        Frozen("#76b900"), // NVIDIA green
        Frozen("#3ba55c"),
        Frozen("#e0b400"),
        Frozen("#58a6ff"),
        Frozen("#ff7b9c"),
    };

    private readonly Canvas _canvas = new()
    {
        Background = Brushes.Transparent,
        ClipToBounds = true,
    };

    // The inner grid that lays out the header row + canvas plot area. This class
    // derives from Border so it can host a themed card around that grid.
    private readonly Grid _inner = new();

    private readonly TextBlock _maxText = new()
    {
        Foreground = Theme.Muted,
        FontFamily = Theme.Mono,
        FontSize = 10,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly StackPanel _legend = new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private string _title = string.Empty;
    private double _maxValue = 1;
    private IReadOnlyCollection<KeyValuePair<int, IReadOnlyList<double>>>? _lastSeries;

    public MetricChart(string title)
    {
        _title = title;

        _inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleText = new TextBlock
        {
            Text = title,
            Foreground = Theme.Fg,
            FontFamily = Theme.Mono,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 2) };
        DockPanel.SetDock(_maxText, Dock.Right);
        DockPanel.SetDock(_legend, Dock.Right);
        header.Children.Add(_maxText);
        header.Children.Add(_legend);
        header.Children.Add(titleText);
        Grid.SetRow(header, 0);

        _canvas.SizeChanged += (_, _) => Draw();
        Grid.SetRow(_canvas, 1);

        _inner.Children.Add(header);
        _inner.Children.Add(_canvas);

        // Themed card so each chart reads as a distinct panel. This class is
        // itself a Border, so the border props live on `this`.
        BorderBrush = Theme.Border;
        BorderThickness = new Thickness(1);
        Background = Theme.Panel;
        Child = _inner;
        Margin = new Thickness(0, 0, 0, 8);
        Height = 140;
    }

    /// <summary>
    /// Rebuilds the chart for one metric. <paramref name="values"/> maps
    /// gpuIndex → a series of samples (oldest → newest). <paramref name="maxValue"/>
    /// is the Y-axis ceiling (e.g. 100 for percentages; a running max for the rest).
    /// </summary>
    public void SetSeries(IReadOnlyCollection<KeyValuePair<int, IReadOnlyList<double>>> values, double maxValue, string title)
    {
        _title = title;
        _lastSeries = values;
        _maxValue = maxValue <= 0 ? 1 : maxValue;
        _maxText.Text = $"max {_maxValue:0}";
        Draw();
        UpdateLegend(values);
    }

    // ── internals ──────────────────────────────────────────────

    private void Draw()
    {
        _canvas.Children.Clear();

        var w = _canvas.ActualWidth;
        var h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0 || _lastSeries is null) return; // not sized yet

        double max = _maxValue;
        if (max <= 0) max = 1;

        foreach (var kvp in _lastSeries)
        {
            var values = kvp.Value;
            int n = values.Count;
            if (n < 2) continue; // need at least two points for a line

            var pts = new PointCollection(n);
            for (int i = 0; i < n; i++)
            {
                double x = (i / (double)(n - 1)) * w;
                double y = h - (values[i] / max) * h;
                y = Math.Clamp(y, 0, h); // guard values above the ceiling
                pts.Add(new Point(x, y));
            }

            _canvas.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = Palette[kvp.Key % Palette.Length],
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }

        // Baseline axis so the plot reads on the dark background.
        _canvas.Children.Add(new Line
        {
            X1 = 0,
            Y1 = h,
            X2 = w,
            Y2 = h,
            Stroke = Theme.Border,
            StrokeThickness = 1,
        });
    }

    private void UpdateLegend(IReadOnlyCollection<KeyValuePair<int, IReadOnlyList<double>>> values)
    {
        _legend.Children.Clear();
        foreach (var kvp in values.OrderBy(p => p.Key))
        {
            var series = kvp.Value;
            var cur = series.Count > 0 ? series[series.Count - 1] : 0;
            _legend.Children.Add(new Border
            {
                Background = Palette[kvp.Key % Palette.Length],
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            _legend.Children.Add(new TextBlock
            {
                Text = $"GPU {kvp.Key} · {cur:0}",
                Foreground = Theme.Muted,
                FontFamily = Theme.Mono,
                FontSize = 10,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}