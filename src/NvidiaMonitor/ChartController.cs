using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HectorBravo.Nvidia.Contracts;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// A self-contained live line chart drawn entirely with native WPF shapes
/// (<see cref="Polyline"/> + <see cref="Line"/> + <see cref="Canvas"/> — no third-party
/// charting library). Plots ONE metric over time; each GPU is one polyline overlaid on
/// the others, coloured from a stable palette.
///
/// Layout: a themed card with a header row (title + a red per-GPU crosshair readout + a
/// per-GPU "latest value" legend + running max) above an inner horizontal
/// <see cref="ScrollViewer"/> whose content is a wide <see cref="Canvas"/> holding the
/// full time history. Once enough time has elapsed the canvas is wider than the
/// viewport, so the inner scrollbar moves across time. A <see cref="ChartController"/>
/// owns the shared timeline / zoom / scroll / crosshair state and keeps every chart in
/// this group in lock-step, so the red crosshair and the per-minute gridlines line up.
///
/// The X-axis is anchored at t = 0 (start). While less than the window width has elapsed
/// the data plots from the left edge and the canvas is narrow; once history exceeds the
/// window, the view pins to the most-recent window and the user can scroll left back
/// toward t = 0. <see cref="SetSeries"/> stores data only; the controller redraws.
/// </summary>
public class MetricChart : Border
{
    // GPU 0 is always the NVIDIA green; extra GPUs cycle through distinguishable hues.
    private static readonly Brush[] Palette =
    {
        Frozen("#76b900"), // NVIDIA green
        Frozen("#3ba55c"),
        Frozen("#e0b400"),
        Frozen("#58a6ff"),
        Frozen("#ff7b9c"),
    };

    // Crosshair + per-GPU value readout are always red (per spec).
    private static readonly Brush CrossRed = Frozen("#ff5252");
    // Per-minute gridlines AND the t = 0 "start" marker share one light blue and the same
    // format (per user spec: minute 0 looks exactly like every other minute line).
    private static readonly Brush MinuteBlue = Frozen("#7f9bd1");
    private static readonly Brush MinuteLabel = Frozen("#7f9bd1");

    // Right-shift in pixels: when the timeline is shorter than the viewport, the whole
    // plot is shifted right so the data pins to the right edge (no blank centered strip).
    private double _rightShift;
    // Target offset of the controller's latest programmatic pin on this chart. A
    // ScrollChanged landing within ±8 px of it is the layout pass applying OUR scroll
    // (it can fire after _syncing is cleared), never a user gesture — so it must not
    // touch the follow-right latch. -1 when no programmatic scroll is pending.
    private double _progOffset = -1;
    internal void QueueProgrammaticScroll(double target) => _progOffset = target;
    // Fixed right-hand Y-axis: reserved width for the axis column (value labels + tick marks).
    private const int AxisPad = 62;

    private const double PlotH = 128;        // fixed plot (drawing) height in px
    private const double AxisH = 16;         // bottom strip for minute labels (below the plot)
    private const double ScrollReserve = 16; // space for the horizontal scrollbar row (below labels)
    private const double TopPad = 26;        // headroom so the top (max) value isn't clipped

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

    // Red, per-GPU value readout shown while the crosshair is active.
    private readonly StackPanel _readout = new()
    {
        Orientation = Orientation.Horizontal,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _titleText;

    private readonly ScrollViewer _plotScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top,
        MinWidth = 1,
        Background = Theme.Card,
    };

    // Right-hand Y-axis. Lives in its own ScrollViewer (fixed-width, so it never scrolls
    // horizontally) placed to the RIGHT of the plot; it stays fixed while the plot scrolls
    // across time. Its height follows the plot so tick labels align with the drawn values.
    private readonly ScrollViewer _axisScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalContentAlignment = VerticalAlignment.Top,
        MinWidth = 1,
        Background = Theme.Card,
    };

    // The axis drawing surface (value labels + tick marks), hosted inside _axisScroll.
    private readonly Canvas _axisCanvas = new()
    {
        Background = Theme.Card,
    };

    private readonly Canvas _canvas = new()
    {
        Background = Theme.Card,
        ClipToBounds = true,
        Height = PlotH,
    };

    // Persistent red dotted crosshair (moved on hover rather than rebuilt).
    private readonly Line _crossLine = new()
    {
        Stroke = CrossRed,
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 3, 3 },
        IsHitTestVisible = false,
        Visibility = Visibility.Hidden,
    };

    private ChartController? _ctl;
    private double[] _tl = Array.Empty<double>(); // seconds since start (full history)
    private IReadOnlyCollection<KeyValuePair<int, double[]>> _ser =
        new List<KeyValuePair<int, double[]>>();
    private string _unit = string.Empty;
    private int _dec;
    private double _ceiling = 1;
    private string _title = string.Empty;

    // When true, Y-axis uses logarithmic scale (for metrics with wide dynamic range).
    private bool _logScale;
    // Per-GPU PCIe link config (gen and width, aligned to the timeline; 0 = N/A) for the
    // legend and hover readout. Only populated on the PCIe chart (via SetSeries).
    private Dictionary<int, int[]> _pcieGen = new();
    private Dictionary<int, int[]> _pcieWidth = new();

    public MetricChart(string title)
    {
        _title = title;
        Cursor = Cursors.Hand;

        _titleText = new TextBlock
        {
            Text = title,
            Foreground = Theme.Fg,
            FontFamily = Theme.Mono,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(2, 2, 2, 4) };
        DockPanel.SetDock(_maxText, Dock.Right);
        DockPanel.SetDock(_legend, Dock.Right);
        DockPanel.SetDock(_readout, Dock.Right);
        header.Children.Add(_maxText);
        header.Children.Add(_legend);
        header.Children.Add(_readout);
        header.Children.Add(_titleText);
        Grid.SetRow(header, 0);

        // Body: the plot fills the remaining width; the fixed right-hand Y-axis sits in a
        // dedicated (non-scrolling, fixed-width) column to its right, so it stays put while
        // the plot scrolls across time.
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AxisPad) });

        _plotScroll.Content = _canvas;
        Grid.SetColumn(_plotScroll, 0);

        _axisScroll.Content = _axisCanvas;
        _axisCanvas.Height = PlotH + AxisH; // match the plot so value labels line up
        Grid.SetColumn(_axisScroll, 1);

        body.Children.Add(_plotScroll);
        body.Children.Add(_axisScroll);
        Grid.SetRow(body, 1);

        _inner.Children.Add(header);
        _inner.Children.Add(body);
        Child = _inner;

        Background = Theme.Card;
        BorderBrush = Theme.Border;
        BorderThickness = new Thickness(1);
        Margin = new Thickness(0, 0, 0, 10);
        Padding = new Thickness(8);
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseLeave += OnCanvasMouseLeave;
        _plotScroll.ScrollChanged += OnPlotScroll;

        // Drag-to-reorder: this panel is both a drag source (left-button-down starts a
        // WPF drag; a quick click-and-release is auto-cancelled, so no accidental move)
        // and a drop target (another chart can be dropped on it to reorder).
        MouseLeftButtonDown += OnCardMouseDown;
        AllowDrop = true;
        DragOver += OnChartDragOver;
        Drop += OnChartDrop;
    }
    /// <summary>Joins this chart to a shared controller (timeline / zoom / crosshair / scroll sync).</summary>
    public void Bind(ChartController controller)
    {
        _ctl = controller;
        controller.Attach(this);
    }

    /// <summary>
    /// Feeds one metric for the current full history. <paramref name="timelineSec"/> is
    /// seconds-since-start (oldest → newest) and <paramref name="values"/> maps
    /// gpuIndex → a value array aligned to it. <paramref name="ceiling"/> is the Y-axis
    /// ceiling. Storage only — the owning controller performs the redraw.
    /// </summary>
    public void SetSeries(
        double[] timelineSec,
        IReadOnlyCollection<KeyValuePair<int, double[]>> values,
        double ceiling,
        string title,
        string unit,
        int decimals,
        IReadOnlyCollection<KeyValuePair<int, int[]>>? pcieGen = null,
        IReadOnlyCollection<KeyValuePair<int, int[]>>? pcieWidth = null)
    {
        _tl = timelineSec ?? Array.Empty<double>();
        _ser = values ?? new List<KeyValuePair<int, double[]>>();
        _ceiling = ceiling <= 0 ? 1 : ceiling;
        _title = title;
        _unit = unit ?? string.Empty;
        _dec = decimals;
        _pcieGen = new Dictionary<int, int[]>();
        _pcieWidth = new Dictionary<int, int[]>();
        if (pcieGen != null) foreach (var kv in pcieGen) _pcieGen[kv.Key] = kv.Value;
        if (pcieWidth != null) foreach (var kv in pcieWidth) _pcieWidth[kv.Key] = kv.Value;
    }

    public ScrollViewer PlotScroll => _plotScroll;

    /// <summary>Switches this chart to a logarithmic Y-axis (for wide-range metrics like PCIe bandwidth).</summary>
    public void SetLogScale(bool log) => _logScale = log;

    /// <summary>Maps a value in [0..ceiling] to a Y pixel on the plot (top=ceiling at
    /// y=TopPad, bottom=0 at y=PlotH — both scales keep TopPad headroom so the max value
    /// and its axis label are never clipped at the top edge).</summary>
    private double ValueToY(double value, double ceiling)
    {
        if (!_logScale)
            return TopPad + (PlotH - TopPad) * (1.0 - value / ceiling);

        // Log scale with top headroom: bottom = 0.01 * ceiling at y = PlotH,
        // top = ceiling at y = TopPad, so the max value sits just below the top
        // edge instead of being clipped right at it.
        double minLog = Math.Log(ceiling * 0.01);
        double maxLog = Math.Log(ceiling);
        double vLog = Math.Log(Math.Max(value, ceiling * 0.01));
        double frac = (vLog - minLog) / (maxLog - minLog);
        double span = PlotH - TopPad;
        return TopPad + span * (1 - Math.Clamp(frac, 0, 1));
    }

    /// <summary>Re-renders the full canvas (per-minute gridlines + all series + crosshair).</summary>
    public void Redraw()
    {
        if (_ctl == null) return;

        double px = _ctl.PxPerSec;
        double contentW = _tl.Length > 0 ? _tl[^1] * px : 0;

        // The canvas must always be at least as wide as the plot viewport, otherwise a
        // ScrollViewer centers the narrow canvas (blank strips on both sides). Size it to
        // the viewport and right-shift the whole plot so short histories pin to the right
        // edge — once history exceeds the window the shift is 0 and it scrolls normally.
        double viewport = _plotScroll.ViewportWidth > 0 ? _plotScroll.ViewportWidth : _ctl.ViewportWidth;
        if (viewport <= 0) viewport = 960;
        _rightShift = Math.Max(0, viewport - contentW);
        _canvas.Width = contentW + _rightShift;
        _canvas.Height = PlotH + AxisH;
        double off = _rightShift; // right-shift applied to every x coordinate

        // Remove every drawn element except the persistent crosshair. Reverse-index so we
        // never mutate the collection while enumerating it (avoids InvalidOperationException).
        for (int i = _canvas.Children.Count - 1; i >= 0; i--)
        {
            if (_canvas.Children[i] != _crossLine) _canvas.Children.RemoveAt(i);
        }

        if (_tl.Length < 2)
        {
            UpdateCrosshair();
            return;
        }

        double max = _ceiling > 0 ? _ceiling : 1;

        // t = 0 "start" marker: drawn in EXACTLY the same format as the other per-minute
        // gridlines (same light blue, same thickness, same "0m" label style), so minute 0
        // reads as the first of the minute lines — only its position marks the timeline's
        // origin (per user spec).
        double x0 = _rightShift; // left edge of the data region (t = 0)
        _canvas.Children.Add(new Line
        {
            X1 = x0, Y1 = 0, X2 = x0, Y2 = PlotH,
            Stroke = MinuteBlue,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        });
        var lbl0 = new TextBlock
        {
            Text = "0m",
            Foreground = MinuteLabel,
            FontFamily = Theme.Mono,
            FontSize = 11,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(lbl0, x0 + 3);
        Canvas.SetTop(lbl0, PlotH + 2);
        _canvas.Children.Add(lbl0);

        // ── per-minute dark-blue gridlines within the visible window ──
        double viewSec = _ctl.WindowSeconds;
        double tStart = _ctl.ViewStartSec();
        double tEnd = tStart + viewSec;
        int firstMin = (int)Math.Floor(tStart / 60.0) + 1;
        if (tStart > 0.0 && firstMin * 60.0 < tStart) firstMin = (int)Math.Ceiling(tStart / 60.0);
        if (firstMin < 1) firstMin = 1;
        for (int m = firstMin; m * 60.0 <= tEnd; m++)
        {
            double x = m * 60.0 * px + off;
            _canvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = PlotH,
                Stroke = MinuteBlue,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
            var lbl = new TextBlock
            {
                Text = $"{m}m",
                Foreground = MinuteLabel,
                FontFamily = Theme.Mono,
                FontSize = 11,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(lbl, x + 3);
            Canvas.SetTop(lbl, PlotH + 2);
            _canvas.Children.Add(lbl);
        }

        // Horizontal gridlines with value labels — only drawn on log-scale charts
        // (e.g. PCIe Bandwidth) where the linear charts previously had none.
        if (_logScale)
        {
            // Gridlines at powers of two within [0.01*max, max]. Their value labels live on
            // the fixed right-hand Y-axis (DrawAxis), not in the plot, so the plot stays clear.
            for (int p = 0; p <= 8; p++)
            {
                double val = Math.Pow(2, p);
                if (val < max * 0.01 || val > max) continue;
                double y = ValueToY(val, max);
                _canvas.Children.Add(new Line
                {
                    X1 = 0, Y1 = y, X2 = contentW + off, Y2 = y,
                    Stroke = Theme.Grid,
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                });
            }
        }

        // ── per-GPU polylines (only the portion inside/near the viewport) ──
        foreach (var kvp in _ser)
        {
            var values = kvp.Value;
            int n = values.Length;
            if (n < 2 || n != _tl.Length) continue;

            // 1s margin each side for continuity past the viewport edges.
            int i0 = LowerIndex(tStart - 1);
            int i1 = UpperIndex(tEnd + 1);
            if (i1 - i0 < 1) continue;

            var pts = new PointCollection(i1 - i0 + 1);
            for (int i = i0; i <= i1; i++)
            {
                double xv = _tl[i] * px + off;
                double yv = ValueToY(values[i], max);
                pts.Add(new Point(xv, Math.Clamp(yv, 0, PlotH)));
            }

            _canvas.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = Palette[kvp.Key % Palette.Length],
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            });
        }

        // Baseline axis.
        _canvas.Children.Add(new Line
        {
            X1 = 0, Y1 = PlotH, X2 = contentW + off, Y2 = PlotH,
            Stroke = Theme.Border,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        });

        if (!_canvas.Children.Contains(_crossLine)) _canvas.Children.Add(_crossLine);

        DrawAxis(max);

        _maxText.Text = $"max {max:0}";
        UpdateLegend();
        UpdateCrosshair();
    }

    // Draws the fixed right-hand Y-axis value labels + tick marks on _axisCanvas, aligned with
    // the plot's values (linear: 0..max with evenly spaced ticks; PCIe/log: powers of two).
    private void DrawAxis(double max)
    {
        for (int i = _axisCanvas.Children.Count - 1; i >= 0; i--)
            _axisCanvas.Children.RemoveAt(i);

        // Axis line at the left edge of the axis column (abuts the plot's right edge).
        _axisCanvas.Children.Add(new Line
        {
            X1 = 0, Y1 = 0, X2 = 0, Y2 = PlotH,
            Stroke = Theme.Border,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        });

        double topY = TopPad; // y of the topmost tick (value = max) — both scales keep headroom
        if (_logScale)
        {
            for (int p = 0; p <= 8; p++)
            {
                double val = Math.Pow(2, p);
                if (val < max * 0.01 || val > max) continue;
                DrawAxisTick(val >= 1024 ? $"{val / 1024:0.#}G" : $"{val:0}G", ValueToY(val, max));
            }
        }
        else
        {
            for (int i = 0; i <= 5; i++)
            {
                double val = max * i / 5.0;
                DrawAxisTick(Fmt(val), topY + (PlotH - topY) * (1 - i / 5.0));
            }
        }
    }

    private void DrawAxisTick(string text, double y)
    {
        _axisCanvas.Children.Add(new Line
        {
            X1 = 0, Y1 = y, X2 = 6, Y2 = y,
            Stroke = Theme.Border,
            StrokeThickness = 1,
            IsHitTestVisible = false,
        });
        var lbl = new TextBlock
        {
            Text = text,
            Foreground = Theme.Muted,
            FontFamily = Theme.Mono,
            FontSize = 10,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(lbl, 8);
        Canvas.SetTop(lbl, y - 7);
        _axisCanvas.Children.Add(lbl);
    }

    // PCIe gen/width at a timeline index for a GPU (0/0 when unavailable or not a PCIe chart).
    private void PcieAt(int gpu, int idx, out int gen, out int width)
    {
        gen = 0;
        width = 0;
        if (_pcieGen.TryGetValue(gpu, out int[]? g) && idx >= 0 && idx < g.Length) gen = g[idx];
        if (_pcieWidth.TryGetValue(gpu, out int[]? w) && idx >= 0 && idx < w.Length) width = w[idx];
    }

    /// <summary>Cheap update: move the red crosshair and refresh the red per-GPU readout.</summary>
    public void UpdateCrosshair()
    {
        if (_ctl == null || _tl.Length < 2)
        {
            _crossLine.Visibility = Visibility.Hidden;
            _readout.Children.Clear();
            return;
        }

        double t = _ctl.CrossTimeSec();
        if (t < 0)
        {
            _crossLine.Visibility = Visibility.Hidden;
            _readout.Children.Clear();
            return;
        }

        double px = _ctl.PxPerSec;
        double x = t * px + _rightShift;
        _crossLine.X1 = x;
        _crossLine.X2 = x;
        _crossLine.Y1 = 0;
        _crossLine.Y2 = PlotH;
        _crossLine.Visibility = Visibility.Visible;

        _readout.Children.Clear();
        int sampleIdx = _tl.Length > 0 ? LowerIndex(t) : 0;
        foreach (var kvp in _ser.OrderBy(p => p.Key))
        {
            double v = ValueAt(t, _tl, kvp.Value);
            _readout.Children.Add(new Border
            {
                Background = Palette[kvp.Key % Palette.Length],
                Width = 9,
                Height = 9,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            string text = Fmt(v) + _unit;
            PcieAt(kvp.Key, sampleIdx, out int gen, out int width);
            if (gen > 0 || width > 0)
                text += $"  [{GpuSnapshot.PcieLinkLabel(gen, width)}]";
            _readout.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = CrossRed,
                FontFamily = Theme.Mono,
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    public void ClearCross()
    {
        _crossLine.Visibility = Visibility.Hidden;
        _readout.Children.Clear();
    }
    private void OnPlotScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_ctl == null) return;
        if (e.HorizontalChange != 0)
        {
            // A ScrollChanged landing near the controller's queued programmatic pin is the
            // layout pass applying OUR scroll — it can fire after _syncing is cleared, so
            // without a discriminator it would be misread as a user gesture (the very race
            // that used to poison the follow intent). A real user scroll travels a visible
            // distance (the scrollbar thumb spans the whole range), whereas our pin moves
            // at most a tick or two — so call it programmatic only when the movement is
            // small AND lands on the queued target. A user who drags back to the right edge
            // from deep in history moves a long way and therefore resumes following.
            bool programmatic = _progOffset >= 0
                && Math.Abs(e.HorizontalOffset - _progOffset) < 8.0
                && Math.Abs(e.HorizontalChange) <= 30.0;
            _progOffset = -1;
            _ctl.SetScrolled(this, e.HorizontalOffset, programmatic);
            return;
        }
        // The offset didn't move, but the content range did (the canvas grew past the
        // viewport — history outgrew the window — or the window was resized). While
        // following, re-pin to the newest data now that the range is up to date: this is
        // what keeps every chart glued to its right edge.
        _progOffset = -1;
        if (_ctl.Following)
        {
            double max = Math.Max(0, _plotScroll.ScrollableWidth);
            if (_plotScroll.HorizontalOffset < max - 0.5)
                _ctl.PinFollow(this, max);
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_ctl == null) return;
        double x = e.GetPosition(_canvas).X; // canvas coords: 0 == start, scroll-independent
        // Subtract the right-shift so the crosshair maps to the correct time in the data
        // region (the left part of the canvas is blank padding when history < window).
        double dataX = Math.Max(0, x - _rightShift);
        _ctl.SetCross(dataX / _ctl.PxPerSec);
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e) => _ctl?.ClearCross();

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        var data = new DataObject("metric-chart", this);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
    }

    private void OnChartDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("metric-chart") is not MetricChart src || src == this)
        {
            e.Effects = DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void OnChartDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("metric-chart") is not MetricChart src || src == this) return;
        if (Parent is not StackPanel sp) return;

        sp.Children.Remove(src);
        int myIdx = sp.Children.IndexOf(this);
        bool before = e.GetPosition(this).Y < ActualHeight / 2.0;
        sp.Children.Insert(Math.Min(before ? myIdx : myIdx + 1, sp.Children.Count), src);
    }

    private int LowerIndex(double t)
    {
        int lo = 0, hi = _tl.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_tl[mid] <= t) lo = mid; else hi = mid;
        }
        return lo;
    }

    private int UpperIndex(double t)
    {
        int lo = 0, hi = _tl.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_tl[mid] < t) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>Value at time t (seconds) for <paramref name="val"/> (aligned to <paramref name="tl"/>), via linear interpolation.</summary>
    private static double ValueAt(double t, double[] tl, double[] val)
    {
        int n = val.Length;
        if (n == 0) return 0;
        if (t <= tl[0]) return val[0];
        if (t >= tl[n - 1]) return val[n - 1];
        // Binary-search the timeline for the two samples bracketing t (indices line up with val).
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (tl[mid] <= t) lo = mid; else hi = mid;
        }
        double span = tl[hi] - tl[lo];
        double f = span > 0 ? (t - tl[lo]) / span : 0;
        return val[lo] * (1 - f) + val[hi] * f;
    }

    private string Fmt(double v) => v.ToString(_dec == 1 ? "0.0" : "0", CultureInfo.InvariantCulture);

    private void UpdateLegend()
    {
        _legend.Children.Clear();
        foreach (var kvp in _ser.OrderBy(p => p.Key))
        {
            var s = kvp.Value;
            double cur = s.Length > 0 ? s[^1] : 0;
            _legend.Children.Add(new Border
            {
                Background = Palette[kvp.Key % Palette.Length],
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            int n = s.Length;
            PcieAt(kvp.Key, n > 0 ? n - 1 : 0, out int pgen, out int pwidth);
            string legendText = Fmt(cur) + _unit;
            if (pgen > 0 || pwidth > 0)
                legendText += $"  [{GpuSnapshot.PcieLinkLabel(pgen, pwidth)}]";
            _legend.Children.Add(new TextBlock
            {
                Text = legendText,
                Foreground = Theme.Fg,
                FontFamily = Theme.Mono,
                FontSize = 12,
                Margin = new Thickness(0, 0, 8, 0),
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
/// <summary>
/// Shared state for a group of <see cref="MetricChart"/>s that must stay in lock-step:
/// one timeline (seconds since start), one "minutes shown" window (X, default 10), one
/// pixels-per-second scale derived from the viewport width, a synchronized horizontal
/// scroll offset, and a single red crosshair time. All charts in the group subscribe to
/// this controller; it pushes redraws / crosshair updates / scroll sync to them.
/// </summary>
public sealed class ChartController
{
    public double WindowMinutes { get; private set; } = 10;
    private const double MinMinutes = 2;
    private const double MaxMinutes = 60;

    public double PxPerSec { get; private set; } = 1.6;
    public double ViewportWidth { get; private set; } = 960;

    private double _crossTime = -1; // seconds since start; < 0 when no crosshair
    private double _lastScroll;
    private bool _syncing;
    // Follow the newest data (right edge) until the user scrolls into history. Latched in
    // SetScrolled; drives AutoFollow on every new sample. Starts true so charts track live.
    private bool _followRight = true;

    private readonly List<MetricChart> _charts = new();

    public IReadOnlyCollection<MetricChart> Charts => _charts;
    public double WindowSeconds => WindowMinutes * 60.0;

    public void Attach(MetricChart chart) => _charts.Add(chart);

    public void SetViewportWidth(double w)
    {
        if (w <= 0) return;
        ViewportWidth = w;
        RecomputePx();
        RaiseRedraw();
    }

    /// <summary>One polling tick: recompute the shared scale, resize, follow, then redraw every chart.
    /// Called once per tick after all charts' <see cref="MetricChart.SetSeries"/> have been updated.</summary>
    public void OnDataTick()
    {
        RecomputePx();
        RaiseRedraw();      // resize the canvases to the new timeline
        // Force every plot's ScrollViewer to measure/arrange NOW, before pinning. Without
        // this the pin below targets the STALE (previous-tick) ScrollableWidth, and as the
        // canvas outgrows the viewport the offset lags further and further left of the
        // newest data — the "left-aligned at 10 min" symptom.
        foreach (var c in _charts) c.PlotScroll.UpdateLayout();
        _syncing = true;    // suppress the ScrollChanged events this causes
        AutoFollow();
        _syncing = false;
        RaiseRedraw();      // re-render (gridlines shift) after the scroll offset changed

        // Safety re-pin: a ScrollChanged for our own pin can still land during the layout
        // pass after _syncing cleared; if it ever moved a following chart off the right
        // edge, put it back. (A user scroll into history sets _followRight = false, in
        // which case this block does nothing and their position is preserved.)
        if (_followRight)
        {
            _syncing = true;
            AutoFollow(pinAll: true);
            _syncing = false;
        }
    }

    /// <summary>CTRL+wheel zoom: change how many minutes fit the viewport, anchored at the newest data.</summary>
    public void Zoom(bool zoomOut)
    {
        double factor = zoomOut ? 1.3 : 1.0 / 1.3;
        WindowMinutes = Math.Clamp(WindowMinutes * factor, MinMinutes, MaxMinutes);
        RecomputePx();
        RaiseRedraw();      // resize to the new px-per-second first
        _syncing = true;
        AutoFollow(pinAll: true);
        _syncing = false;
        RaiseRedraw();
    }

    public void SetCross(double tSec)
    {
        _crossTime = Math.Max(0, tSec);
        foreach (var c in _charts) c.UpdateCrosshair();
    }

    public void ClearCross()
    {
        _crossTime = -1;
        foreach (var c in _charts) c.ClearCross();
    }

    public double CrossTimeSec() => _crossTime;

    /// <summary>Time (seconds since start) at the left edge of the current viewport.</summary>
    public double ViewStartSec() => _lastScroll / (PxPerSec <= 0 ? 1 : PxPerSec);

    /// <summary>Scroll-sync: propagate one chart's offset to the others (guarded against loops).</summary>
    public void SetScrolled(MetricChart src, double offsetPx, bool programmatic = false)
    {
        _lastScroll = offsetPx;
        // Only a REAL user scroll latches the follow intent. Programmatic scrolls (zoom /
        // follow / cross-chart sync) arrive wrapped in _syncing, and the layout pass
        // applying our own pin arrives flagged programmatic — latching from either is what
        // used to drop the follow intent and leave the view left-aligned once history
        // outgrew the window.
        if (!_syncing && !programmatic)
        {
            double srcMax0 = Math.Max(0, src.PlotScroll.ScrollableWidth);
            _followRight = srcMax0 <= 0 || offsetPx >= srcMax0 - 8.0;
        }
        if (_syncing) return;
        // Real user scroll: propagate the same view position (as a fraction of each
        // chart's range) to the other charts, queueing programmatic offsets so their
        // later layout events can't be misread as user scrolls either.
        double srcMax = Math.Max(0, src.PlotScroll.ScrollableWidth);
        _syncing = true;
        try
        {
            double frac = srcMax > 0 ? Math.Clamp(offsetPx / srcMax, 0, 1) : 1;
            foreach (var c in _charts)
            {
                if (c == src) continue;
                double target = frac * Math.Max(0, c.PlotScroll.ScrollableWidth);
                if (Math.Abs(c.PlotScroll.HorizontalOffset - target) > 0.5)
                {
                    c.QueueProgrammaticScroll(target);
                    c.PlotScroll.ScrollToHorizontalOffset(target);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>True while the charts are pinned to the newest data (until the user scrolls into history).</summary>
    public bool Following => _followRight;

    /// <summary>Pin <paramref name="src"/> — and keep every other chart in sync — to the newest
    /// data. Used when the content range grows while following (history outgrew the window,
    /// or the window was resized) and the ScrollViewer's own scroll didn't reach the edge.</summary>
    public void PinFollow(MetricChart src, double srcMax)
    {
        _syncing = true;
        try
        {
            foreach (var c in _charts)
            {
                double m = c == src ? srcMax : Math.Max(0, c.PlotScroll.ScrollableWidth);
                if (Math.Abs(c.PlotScroll.HorizontalOffset - m) > 0.5)
                {
                    c.QueueProgrammaticScroll(m);
                    c.PlotScroll.ScrollToHorizontalOffset(m);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RecomputePx()
    {
        // pxPerSec is chosen so that exactly WindowMinutes fit the viewport width.
        double vp = ViewportWidth > 0 ? ViewportWidth : 960;
        PxPerSec = vp / WindowSeconds;
        if (PxPerSec <= 0) PxPerSec = 1.6;
    }

    private void AutoFollow(bool pinAll = false)
    {
        // After a timeline / zoom change, pin charts to the newest data when the user is in
        // follow mode (or on an explicit zoom). While the user has scrolled into history we
        // keep their offset (clamped to the new max). Run AFTER the canvases have been
        // resized (and, on a data tick, after a forced UpdateLayout so the range is fresh).
        foreach (var c in _charts)
        {
            double max = Math.Max(0, c.PlotScroll.ScrollableWidth);
            double target;
            if (pinAll || _followRight)
                target = max;
            else
            {
                double o = c.PlotScroll.HorizontalOffset;
                target = o > max ? max : -1;
            }
            if (target >= 0)
            {
                c.QueueProgrammaticScroll(target);
                c.PlotScroll.ScrollToHorizontalOffset(target);
            }
        }
    }

    private void RaiseRedraw()
    {
        foreach (var c in _charts) c.Redraw();
    }
}