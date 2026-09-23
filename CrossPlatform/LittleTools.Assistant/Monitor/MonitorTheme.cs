using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// Style constants for the two monitor windows, taken from the Windows module
/// (AIUsageMonitor/Program.cs): the 316x92 HUD (L1468-L1490), the 380x505 detail
/// window (L1885-L1902), the provider colours (L1497-L1499) and every text brush.
///
/// The shell surfaces use <see cref="GlassSurface"/> instead of the Windows
/// ARGB(62/72, 17, 20, 27): the same readability deviation the todo and stock
/// modules already made (the Windows alpha composites to a light grey on a bright
/// desktop and the white text loses its contrast). The Windows values are kept
/// here for reference.
/// </summary>
internal static class MonitorTheme
{
    /// <summary>Windows HUD background (Program.cs L1707) and hover (L1708).</summary>
    public static readonly Color WindowsCompactColor = Color.FromArgb(62, 17, 20, 27);
    public static readonly Color WindowsCompactHoverColor = Color.FromArgb(112, 17, 20, 27);
    /// <summary>Windows detail shell (Program.cs L1897).</summary>
    public static readonly Color WindowsDetailsColor = Color.FromArgb(72, 17, 20, 27);
    /// <summary>Windows settings dialog shell (Program.cs L535).</summary>
    public static readonly Color WindowsSettingsColor = Color.FromArgb(235, 17, 20, 27);

    public static readonly IBrush ShellBackground = GlassSurface.Shell();
    public static readonly IBrush ShellHover = GlassSurface.Hover();
    public static readonly IBrush ShellBorder = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
    public static readonly IBrush DetailsBackground = GlassSurface.Shell();
    public static readonly IBrush DetailsBorder = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
    public static readonly IBrush SettingsBackground = GlassSurface.Dialog();
    public static readonly IBrush SettingsBorder = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255));

    /// <summary>Windows shell DropShadowEffect: blur 12, depth 1, opacity 0.16.</summary>
    public static readonly BoxShadows ShellShadow =
        new(new BoxShadow { Blur = 12, OffsetY = 1, Color = Color.FromArgb(41, 0, 0, 0) });
    /// <summary>Windows detail shell: blur 14, depth 1, opacity 0.16.</summary>
    public static readonly BoxShadows DetailsShadow =
        new(new BoxShadow { Blur = 14, OffsetY = 1, Color = Color.FromArgb(41, 0, 0, 0) });

    /// <summary>Windows provider dot colours (Program.cs L1497-L1499).</summary>
    public static readonly Color CodexColor = Color.FromRgb(89, 210, 255);
    public static readonly Color DeepSeekColor = Color.FromRgb(124, 237, 174);
    public static readonly Color GlmColor = Color.FromRgb(190, 154, 255);

    /// <summary>Windows StatusBrush/WarningBrush/MutedBrush (Program.cs L1693-L1707).</summary>
    public static readonly IBrush PrimaryText = Brushes.White;
    public static readonly IBrush MutedText = new SolidColorBrush(Color.FromArgb(180, 210, 214, 224));
    public static readonly IBrush WarningText = new SolidColorBrush(Color.FromRgb(255, 198, 92));
    public static readonly IBrush DangerText = new SolidColorBrush(Color.FromRgb(255, 102, 119));

    /// <summary>Windows row label ARGB(175), footer ARGB(120), detail label ARGB(150), meta ARGB(145).</summary>
    public static readonly IBrush RowLabel = new SolidColorBrush(Color.FromArgb(175, 255, 255, 255));
    public static readonly IBrush FooterText = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
    public static readonly IBrush SectionLabel = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
    public static readonly IBrush MetaText = new SolidColorBrush(Color.FromArgb(145, 255, 255, 255));
    public static readonly IBrush AxisText = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255));
    public static readonly IBrush SeparatorBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
    public static readonly IBrush TodayText = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));

    /// <summary>Windows SmallButton/RangeButton (Program.cs L2060-L2075).</summary>
    public static readonly IBrush ButtonBackground = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
    public static readonly IBrush ButtonBorder = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255));

    /// <summary>Windows UpdateRangeButtons (Program.cs L2091-L2108).</summary>
    public static readonly IBrush DeepSeekSelected = new SolidColorBrush(Color.FromArgb(105, 124, 237, 174));
    public static readonly IBrush DeepSeekSelectedBorder = new SolidColorBrush(Color.FromArgb(150, 124, 237, 174));
    public static readonly IBrush GlmSelected = new SolidColorBrush(Color.FromArgb(105, 190, 154, 255));
    public static readonly IBrush GlmSelectedBorder = new SolidColorBrush(Color.FromArgb(150, 190, 154, 255));
    public static readonly IBrush ChoiceIdle = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255));
    public static readonly IBrush ChoiceIdleBorder = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255));

    /// <summary>Windows UsageChart colours (Program.cs L2248-L2254).</summary>
    public static readonly Color ChartBackground = Color.FromArgb(22, 255, 255, 255);
    public static readonly Color ChartGrid = Color.FromArgb(28, 255, 255, 255);

    /// <summary>Windows Settings dialog input/button brushes (Program.cs L586-L588).</summary>
    public static readonly IBrush InputBackground = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255));
    public static readonly IBrush InputBorder = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));

    /// <summary>Windows X-axis/text shade for the trend chart's series.</summary>
    public static Color SeriesColor(bool glm) => glm ? GlmColor : DeepSeekColor;

    public static readonly FontFamily UiFont = new("Segoe UI,Inter,Noto Sans CJK SC,Sans Serif");
    public static readonly FontFamily UiFontSemibold = new("Segoe UI Semibold,Segoe UI,Inter,Noto Sans CJK SC,Sans Serif");

    /// <summary>Windows MonitorWindow.CreateRow (Program.cs L1551-L1591).</summary>
    public static TextBlock Label(string text, double size, IBrush? foreground = null, bool bold = false) => new()
    {
        Text = text,
        FontFamily = bold ? UiFontSemibold : UiFont,
        FontSize = size,
        Foreground = foreground ?? PrimaryText,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>A dot for a provider row (Windows: 7x7, corner radius 4).</summary>
    public static Border Dot(Color color) => new()
    {
        Width = 7,
        Height = 7,
        CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(color),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Maps a composed tone to the Windows brush.</summary>
    public static IBrush ToneBrush(MonitorTone tone) => tone switch
    {
        MonitorTone.Muted => MutedText,
        MonitorTone.Warning => WarningText,
        MonitorTone.Danger => DangerText,
        _ => PrimaryText
    };

    /// <summary>Windows SmallButton (Program.cs L2060-L2065): 27 tall, 10pt.</summary>
    public static Button SmallButton(string text, double width)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = 27,
            FontFamily = UiFont,
            FontSize = 10,
            Foreground = PrimaryText,
            Background = ButtonBackground,
            BorderBrush = ButtonBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, 1, 4, 1),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ApplyPressFeedback(button);
        return button;
    }

    /// <summary>Windows RangeButton (Program.cs L2067-L2075): 30x22 (38 for GLM), 9.5pt.</summary>
    public static Button RangeButton(string text, double width)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = 22,
            Margin = new Thickness(3, 0, 0, 0),
            FontFamily = UiFontSemibold,
            FontSize = 9.5,
            Foreground = PrimaryText,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ApplyPressFeedback(button);
        return button;
    }

    /// <summary>Windows Settings dialog inputs (Program.cs L586-L587).</summary>
    public static TextBox Input(string text) => new()
    {
        Text = text,
        Height = 30,
        Padding = new Thickness(8, 4, 8, 4),
        FontFamily = UiFont,
        FontSize = 11,
        Foreground = PrimaryText,
        CaretBrush = PrimaryText,
        Background = InputBackground,
        BorderBrush = InputBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        VerticalContentAlignment = VerticalAlignment.Center
    };

    /// <summary>Windows PasswordBox for a manual API key (Program.cs L587).</summary>
    public static TextBox Password(string watermark)
    {
        var box = Input(string.Empty);
        box.PasswordChar = '●';
        box.PlaceholderText = watermark;
        return box;
    }

    public static CheckBox Check(string text, bool value) => new()
    {
        Content = text,
        IsChecked = value,
        Foreground = PrimaryText,
        FontFamily = UiFont,
        FontSize = 11,
        MinHeight = 0,
        Padding = new Thickness(0)
    };

    public static void ApplyPressFeedback(Button button) =>
        button.PropertyChanged += (_, args) =>
        {
            if (args.Property == Button.IsPressedProperty) button.Opacity = button.IsPressed ? 0.68 : 1;
        };
}
