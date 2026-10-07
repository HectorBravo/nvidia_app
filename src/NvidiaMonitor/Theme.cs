using System.Windows;
using System.Windows.Media;

namespace HectorBravo.Nvidia.Monitor;

/// <summary>
/// Central dark-theme palette + small formatting helpers shared across the UI.
/// All colours are defined here so the look is consistent and easy to retune.
/// </summary>
public static class Theme
{
    // ── Palette ────────────────────────────────────────────────
    public static readonly Color BgColor      = Hex("#0f1117");
    public static readonly Color PanelColor   = Hex("#161a23");
    public static readonly Color CardColor    = Hex("#12161f");
    public static readonly Color BorderColor  = Hex("#232838");
    public static readonly Color GridColor    = Hex("#1c2130");
    public static readonly Color FgColor      = Hex("#e6e9ef");
    public static readonly Color MutedColor   = Hex("#8a93a6");
    public static readonly Color AccentColor  = Hex("#76b900"); // NVIDIA green
    public static readonly Color WarnColor    = Hex("#e0a030");
    public static readonly Color ErrorColor   = Hex("#e05a5a");
    public static readonly Color OkColor      = Hex("#76b900");

    public static readonly Brush Bg      = Frozen(BgColor);
    public static readonly Brush Panel   = Frozen(PanelColor);
    public static readonly Brush Card    = Frozen(CardColor);
    public static readonly Brush Border  = Frozen(BorderColor);
    public static readonly Brush Grid    = Frozen(GridColor);
    public static readonly Brush Fg      = Frozen(FgColor);
    public static readonly Brush Muted   = Frozen(MutedColor);
    public static readonly Brush Accent  = Frozen(AccentColor);
    public static readonly Brush Warn    = Frozen(WarnColor);
    public static readonly Brush Error   = Frozen(ErrorColor);

    /// <summary>Monospaced font for all numeric readouts (Consolas, fallback).</summary>
    public static readonly FontFamily Mono = new("Consolas, Courier New, monospace");

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Hex(string s)
    {
        return (Color)ColorConverter.ConvertFromString(s);
    }

    // ── "N/A" handling ─────────────────────────────────────────
    /// <summary>Formats a value, substituting "N/A" when <paramref name="available"/> is false.</summary>
    public static string Na(bool available, string value) => available ? value : "N/A";

    /// <summary>Human MiB from bytes, or "N/A" when the size is the unknown sentinel (-1 / &lt; 0).</summary>
    public static string MiB(long bytes)
    {
        if (bytes < 0) return "N/A";
        return $"{Contracts.Exporter.BytesToMiB(bytes):N0}";
    }

    /// <summary>Watts from milliwatts, or "N/A" when zero/unknown.</summary>
    public static string Watts(int mw)
    {
        if (mw <= 0) return "N/A";
        return $"{mw / 1000.0:0.0}";
    }

    /// <summary>Percent, or "N/A" when the raw value is the unknown sentinel.</summary>
    public static string Pct(int value, int unknownSentinel = -1)
    {
        if (value == unknownSentinel) return "N/A";
        return $"{value}";
    }

    // ── Reusable dark DataGrid look ────────────────────────────
    public static DataGrid MakeDataGrid(string title)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = true,
            CanUserResizeRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Background = Bg,
            Foreground = Fg,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            RowBackground = Card,
            AlternatingRowBackground = Frozen(Hex("#131826")),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = Grid,
            VerticalGridLinesBrush = Brushes.Transparent,
            FontFamily = Mono,
            FontSize = 12,
            RowHeight = 22,
            Margin = new Thickness(0, 0, 0, 8),
            SelectionMode = DataGridSelectionMode.Single,
        };
        grid.ColumnHeaderStyle = MakeColumnHeaderStyle();
        return grid;
    }

    private static Style? _colHeaderStyle;
    public static Style MakeColumnHeaderStyle()
    {
        if (_colHeaderStyle != null) return _colHeaderStyle;
        var s = new Style(typeof(DataGridColumnHeader));
        s.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, Frozen(Hex("#1a1f2b"))));
        s.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, Muted));
        s.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, Border));
        s.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        s.Setters.Add(new Setter(DataGridColumnHeader.FontFamilyProperty, Mono));
        s.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 11.0));
        s.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
        s.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(8, 6, 8, 6)));
        s.Setters.Add(new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        _colHeaderStyle = s;
        return s;
    }

    /// <summary>A small label used above each card / chart section.</summary>
    public static TextBlock MakeLabel(string text, bool muted = false, double size = 11)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = muted ? Muted : Fg,
            FontFamily = Mono,
            FontSize = size,
            Margin = new Thickness(0, 0, 0, 4),
        };
    }
}
