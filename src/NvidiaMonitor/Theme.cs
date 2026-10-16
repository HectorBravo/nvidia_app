using System.Windows;
using System.Windows.Controls;
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
    public static readonly Color WhiteColor   = Hex("#ffffff");
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
    public static readonly Brush White   = Frozen(WhiteColor);
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

    /// <summary>MB from bytes, or "N/A" when the size is the unknown sentinel (-1 / &lt; 0). Invariant culture => no thousands separator.</summary>
    public static string Mb(long bytes)
    {
        if (bytes < 0) return "N/A";
        return (bytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Watts from milliwatts, or "N/A" when the value is a negative sentinel.</summary>
    public static string Watts(int mw)
    {
        if (mw < 0) return "N/A";
        return (mw / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
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
        s.Setters.Add(new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
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

    // ── Green "slide" scrollbars ─────────────────────────────────────────────
    // The stock WPF scrollbars are light grey / white and stand out on the dark
    // theme (most visible on the per-chart horizontal bars). These restyle them
    // as a slim rounded dark track with a green gradient thumb: a bright aqua
    // leading edge (#18FFCC) fading through emerald (#048D5F) into deep green —
    // colours sampled from the user's reference gradient — so the thumb reads as
    // a green "slide". The XAML is built as a string (this app has no .xaml
    // files) and parsed once.

    private static ResourceDictionary? _scrollResources;
    private static Style? _scrollH;
    private static Style? _scrollV;

    private const string ScrollXamlPart1 =
@"<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <SolidColorBrush x:Key=""Track"" Color=""#0A0E13"" Opacity=""0.78""/>
  <LinearGradientBrush x:Key=""GradH"" StartPoint=""0,0.5"" EndPoint=""1,0.5"">
    <GradientStop Color=""#0A3D2B"" Offset=""0""/><GradientStop Color=""#18FFCC"" Offset=""0.12""/><GradientStop Color=""#0FBE8C"" Offset=""0.38""/><GradientStop Color=""#048D5F"" Offset=""0.7""/><GradientStop Color=""#052318"" Offset=""1""/>
  </LinearGradientBrush>
  <LinearGradientBrush x:Key=""GradHOver"" StartPoint=""0,0.5"" EndPoint=""1,0.5"">
    <GradientStop Color=""#0F5A3C"" Offset=""0""/><GradientStop Color=""#5FFFD9"" Offset=""0.12""/><GradientStop Color=""#23E3A8"" Offset=""0.38""/><GradientStop Color=""#0BB57E"" Offset=""0.7""/><GradientStop Color=""#0A3D2B"" Offset=""1""/>
  </LinearGradientBrush>
  <LinearGradientBrush x:Key=""GradHDrag"" StartPoint=""0,0.5"" EndPoint=""1,0.5"">
    <GradientStop Color=""#083322"" Offset=""0""/><GradientStop Color=""#10D6A6"" Offset=""0.12""/><GradientStop Color=""#0FA87E"" Offset=""0.38""/><GradientStop Color=""#037550"" Offset=""0.7""/><GradientStop Color=""#041F14"" Offset=""1""/>
  </LinearGradientBrush>
  <LinearGradientBrush x:Key=""GradV"" StartPoint=""0,0"" EndPoint=""0,1"">
    <GradientStop Color=""#0A3D2B"" Offset=""0""/><GradientStop Color=""#18FFCC"" Offset=""0.12""/><GradientStop Color=""#0FBE8C"" Offset=""0.38""/><GradientStop Color=""#048D5F"" Offset=""0.7""/><GradientStop Color=""#052318"" Offset=""1""/>
  </LinearGradientBrush>
  <LinearGradientBrush x:Key=""GradVOver"" StartPoint=""0,0"" EndPoint=""0,1"">
    <GradientStop Color=""#0F5A3C"" Offset=""0""/><GradientStop Color=""#5FFFD9"" Offset=""0.12""/><GradientStop Color=""#23E3A8"" Offset=""0.38""/><GradientStop Color=""#0BB57E"" Offset=""0.7""/><GradientStop Color=""#0A3D2B"" Offset=""1""/>
  </LinearGradientBrush>
  <LinearGradientBrush x:Key=""GradVDrag"" StartPoint=""0,0"" EndPoint=""0,1"">
    <GradientStop Color=""#083322"" Offset=""0""/><GradientStop Color=""#10D6A6"" Offset=""0.12""/><GradientStop Color=""#0FA87E"" Offset=""0.38""/><GradientStop Color=""#037550"" Offset=""0.7""/><GradientStop Color=""#041F14"" Offset=""1""/>
  </LinearGradientBrush>
  <Style x:Key=""PageBtn"" TargetType=""RepeatButton"">
    <Setter Property=""Focusable"" Value=""False""/>
    <Setter Property=""IsTabStop"" Value=""False""/>
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""RepeatButton""><Border Background=""Transparent""/></ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <ControlTemplate x:Key=""ThumbHT"" TargetType=""Thumb"">
    <Border x:Name=""bg"" CornerRadius=""4"" BorderBrush=""#04140D"" BorderThickness=""1"" Background=""{StaticResource GradH}""/>
    <ControlTemplate.Triggers>
      <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""bg"" Property=""Background"" Value=""{StaticResource GradHOver}""/></Trigger>
      <Trigger Property=""IsDragging"" Value=""True""><Setter TargetName=""bg"" Property=""Background"" Value=""{StaticResource GradHDrag}""/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <ControlTemplate x:Key=""ThumbVT"" TargetType=""Thumb"">
    <Border x:Name=""bg"" CornerRadius=""4"" BorderBrush=""#04140D"" BorderThickness=""1"" Background=""{StaticResource GradV}""/>
    <ControlTemplate.Triggers>
      <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""bg"" Property=""Background"" Value=""{StaticResource GradVOver}""/></Trigger>
      <Trigger Property=""IsDragging"" Value=""True""><Setter TargetName=""bg"" Property=""Background"" Value=""{StaticResource GradVDrag}""/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>";

    private const string ScrollXamlPart2 =
@"
  <Style x:Key=""ThumbH"" TargetType=""Thumb"">
    <Setter Property=""Height"" Value=""7""/><Setter Property=""VerticalAlignment"" Value=""Center""/><Setter Property=""Template"" Value=""{StaticResource ThumbHT}""/>
  </Style>
  <Style x:Key=""ThumbV"" TargetType=""Thumb"">
    <Setter Property=""Width"" Value=""7""/><Setter Property=""HorizontalAlignment"" Value=""Center""/><Setter Property=""Template"" Value=""{StaticResource ThumbVT}""/>
  </Style>
  <ControlTemplate x:Key=""ScrollHT"" TargetType=""ScrollBar"">
    <Grid Background=""Transparent"" SnapsToDevicePixels=""True"">
      <Border CornerRadius=""5"" Background=""{StaticResource Track}"" Height=""9"" VerticalAlignment=""Center""/>
      <Track x:Name=""PART_Track"" IsDirectionReversed=""False"">
        <Track.DecreaseRepeatButton><RepeatButton Style=""{StaticResource PageBtn}"" Command=""ScrollBar.LineLeftCommand""/></Track.DecreaseRepeatButton>
        <Track.Thumb><Thumb Style=""{StaticResource ThumbH}""/></Track.Thumb>
        <Track.IncreaseRepeatButton><RepeatButton Style=""{StaticResource PageBtn}"" Command=""ScrollBar.LineRightCommand""/></Track.IncreaseRepeatButton>
      </Track>
    </Grid>
  </ControlTemplate>
  <ControlTemplate x:Key=""ScrollVT"" TargetType=""ScrollBar"">
    <Grid Background=""Transparent"" SnapsToDevicePixels=""True"">
      <Border CornerRadius=""5"" Background=""{StaticResource Track}"" Width=""9"" HorizontalAlignment=""Center""/>
      <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
        <Track.DecreaseRepeatButton><RepeatButton Style=""{StaticResource PageBtn}"" Command=""ScrollBar.LineUpCommand""/></Track.DecreaseRepeatButton>
        <Track.Thumb><Thumb Style=""{StaticResource ThumbV}""/></Track.Thumb>
        <Track.IncreaseRepeatButton><RepeatButton Style=""{StaticResource PageBtn}"" Command=""ScrollBar.LineDownCommand""/></Track.IncreaseRepeatButton>
      </Track>
    </Grid>
  </ControlTemplate>
  <Style x:Key=""ScrollH"" TargetType=""ScrollBar"">
    <Setter Property=""Height"" Value=""13""/><Setter Property=""VerticalAlignment"" Value=""Center""/><Setter Property=""Background"" Value=""Transparent""/><Setter Property=""Template"" Value=""{StaticResource ScrollHT}""/>
  </Style>
  <Style x:Key=""ScrollV"" TargetType=""ScrollBar"">
    <Setter Property=""Width"" Value=""13""/><Setter Property=""HorizontalAlignment"" Value=""Center""/><Setter Property=""Background"" Value=""Transparent""/><Setter Property=""Template"" Value=""{StaticResource ScrollVT}""/>
  </Style>
</ResourceDictionary>";

    /// <summary>
    /// Walks the visual tree from <paramref name="root"/> and applies the green "slide"
    /// scrollbar style to every ScrollBar that doesn't already carry it. Idempotent, so it
    /// is safe to call on every render — that is what catches scrollbars whose templates
    /// are realized lazily (e.g. the Overview DataGrid's own bars, which only exist once
    /// that tab has been shown at least once).
    /// </summary>
    public static void ApplyScrollStyles(DependencyObject root)
    {
        var h = GetScrollStyle(Orientation.Horizontal);
        var v = GetScrollStyle(Orientation.Vertical);

        Visit(root);

        void Visit(DependencyObject d)
        {
            if (d is ScrollBar bar)
            {
                var target = bar.Orientation == Orientation.Horizontal ? h : v;
                if (!ReferenceEquals(bar.Style, target)) bar.Style = target;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                Visit(VisualTreeHelper.GetChild(d, i));
        }
    }

    private static Style GetScrollStyle(Orientation orientation)
    {
        if (_scrollH is null)
        {
            _scrollResources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(
                ScrollXamlPart1 + ScrollXamlPart2);
            _scrollH = (Style)_scrollResources["ScrollH"];
            _scrollV = (Style)_scrollResources["ScrollV"];
        }
        return orientation == Orientation.Horizontal ? _scrollH! : _scrollV!;
    }
}
