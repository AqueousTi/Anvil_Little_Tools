using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Style constants for the stock windows. Every value is the one used by the
/// Windows module (StockMonitor/StockWindow.cs): the 316x92 translucent capsule,
/// the 470x650 detail window, the 11px cards and the A-share change colours.
/// </summary>
internal static class StockTheme
{
    public static readonly Color ShellBackgroundColor = Color.FromArgb(62, 17, 20, 27);
    public static readonly Color ShellHoverColor = Color.FromArgb(112, 17, 20, 27);
    public static readonly Color DetailsBackgroundColor = Color.FromArgb(72, 17, 20, 27);

    public static readonly IBrush ShellBackground = new SolidColorBrush(ShellBackgroundColor);
    public static readonly IBrush ShellHover = new SolidColorBrush(ShellHoverColor);
    public static readonly IBrush ShellBorder = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
    public static readonly IBrush DetailsBackground = new SolidColorBrush(DetailsBackgroundColor);
    public static readonly IBrush DetailsBorder = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));

    /// <summary>Windows shell DropShadowEffect: blur 12, depth 1, opacity 0.16.</summary>
    public static readonly BoxShadows ShellShadow =
        new(new BoxShadow { Blur = 12, OffsetY = 1, Color = Color.FromArgb(41, 0, 0, 0) });
    /// <summary>Windows detail shell: blur 14, depth 1, opacity 0.16.</summary>
    public static readonly BoxShadows DetailsShadow =
        new(new BoxShadow { Blur = 14, OffsetY = 1, Color = Color.FromArgb(41, 0, 0, 0) });

    /// <summary>Windows Card(): ARGB(35) fill, ARGB(33) border, radius 11.</summary>
    public static readonly IBrush CardBackground = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255));
    public static readonly IBrush CardBorder = new SolidColorBrush(Color.FromArgb(33, 255, 255, 255));

    /// <summary>Windows Secondary(): ARGB(145, 255, 255, 255).</summary>
    public static readonly IBrush SecondaryText = new SolidColorBrush(Color.FromArgb(145, 255, 255, 255));
    public static readonly IBrush MutedText = new SolidColorBrush(Color.FromArgb(165, 165, 170, 180));
    public static readonly IBrush PrimaryText = Brushes.White;

    /// <summary>Windows SmallButton(): ARGB(24) fill on an ARGB(40) border.</summary>
    public static readonly IBrush ButtonBackground = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
    public static readonly IBrush ButtonBorder = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    public static readonly IBrush ButtonPressed = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));

    /// <summary>Windows UpdateChoices(): ARGB(45) selected against ARGB(10).</summary>
    public static readonly IBrush ChoiceSelected = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
    public static readonly IBrush ChoiceIdle = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));

    /// <summary>Windows RenderTabs(): ARGB(42) selected against ARGB(12).</summary>
    public static readonly IBrush TabSelected = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255));
    public static readonly IBrush TabIdle = new SolidColorBrush(Color.FromArgb(12, 255, 255, 255));

    /// <summary>Windows out-of-range tab arrows use Opacity 0.28.</summary>
    public const double TabArrowIdleOpacity = 0.28;
    public const double TabArrowActiveOpacity = 0.9;

    public const double CompactWidth = 316;
    public const double CompactHeight = 92;
    public const double DetailsWidth = 470;
    public const double DetailsHeight = 650;
    public const double ShellCornerRadius = 15;
    public const double DetailsCornerRadius = 17;
    public const double CardCornerRadius = 11;

    public static readonly FontFamily UiFont = new("Segoe UI,Inter,Noto Sans CJK SC,Sans Serif");

    /// <summary>Windows ChangeBrush (StockWindow.cs L963): red up, green down, white flat.</summary>
    public static IBrush ChangeBrush(StockFormat.ChangeKind kind) => kind switch
    {
        StockFormat.ChangeKind.Up => new SolidColorBrush(Color.FromRgb(239, 88, 91)),
        StockFormat.ChangeKind.Down => new SolidColorBrush(Color.FromRgb(124, 237, 174)),
        _ => Brushes.White
    };

    /// <summary>
    /// Windows PremiumValue(): a white core with a repeating six stop gradient
    /// outline, so a cheap premium is visible against the dark shell.
    /// </summary>
    public static IBrush PremiumOutlineGradient() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.52, 0.5, RelativeUnit.Relative),
        SpreadMethod = GradientSpreadMethod.Repeat,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(89, 210, 255), 0),
            new GradientStop(Color.FromRgb(104, 156, 255), 0.18),
            new GradientStop(Color.FromRgb(190, 154, 255), 0.36),
            new GradientStop(Color.FromRgb(255, 112, 174), 0.55),
            new GradientStop(Color.FromRgb(255, 198, 92), 0.76),
            new GradientStop(Color.FromRgb(124, 237, 174), 1)
        }
    };

    public static TextBlock Label(string text, double size, IBrush? foreground = null, bool bold = false) => new()
    {
        Text = text,
        FontFamily = UiFont,
        FontSize = size,
        Foreground = foreground ?? PrimaryText,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Windows SmallButton (StockWindow.cs L926-L953): 29 tall, radius 6, 5,1,5,2 padding.</summary>
    public static Button SmallButton(string text, double width)
    {
        var button = new Button
        {
            Content = text,
            Width = width,
            Height = 29,
            FontFamily = UiFont,
            FontSize = 10.5,
            Foreground = PrimaryText,
            Background = ButtonBackground,
            BorderBrush = ButtonBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 1, 5, 2),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ApplyPressFeedback(button);
        return button;
    }

    /// <summary>Windows InputBox (StockWindow.cs L905-L924): 34 tall, radius 7, 10,5 padding.</summary>
    public static TextBox InputBox(string hint) => new()
    {
        Height = 34,
        Padding = new Thickness(10, 5, 10, 5),
        FontFamily = UiFont,
        FontSize = 11,
        Foreground = PrimaryText,
        CaretBrush = PrimaryText,
        Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        PlaceholderText = hint,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    public static void ApplyPressFeedback(Button button) =>
        button.PropertyChanged += (_, args) =>
        {
            if (args.Property == Button.IsPressedProperty) button.Opacity = button.IsPressed ? 0.68 : 1;
        };

    /// <summary>Windows Card(Thickness): the translucent metric/value panel.</summary>
    public static Border Card(Thickness margin) => new()
    {
        CornerRadius = new CornerRadius(CardCornerRadius),
        Background = CardBackground,
        BorderBrush = CardBorder,
        BorderThickness = new Thickness(1),
        Margin = margin
    };

    public static StackPanel Metric(string label, out TextBlock value)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Label(label, 9.5, SecondaryText));
        value = Label("--", 19, PrimaryText, bold: true);
        value.Margin = new Thickness(0, 5, 0, 0);
        stack.Children.Add(value);
        return stack;
    }

    public static StackPanel PremiumMetric(string label, out OutlinedValueText value)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Label(label, 9.5, SecondaryText));
        value = PremiumValue(19);
        value.Margin = new Thickness(0, 5, 0, 0);
        stack.Children.Add(value);
        return stack;
    }

    /// <summary>Windows PremiumValue (StockWindow.cs L859-L879).</summary>
    public static OutlinedValueText PremiumValue(double size) => new()
    {
        Text = "--",
        FontFamily = UiFont,
        FontSize = size,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White,
        OutlineBrush = PremiumOutlineGradient(),
        OutlineThickness = 0.75,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center
    };
}

/// <summary>Windows CandleChart colours (StockWindow.cs L68-L133).</summary>
internal static class StockPalette
{
    public static readonly Color ChartBackground = Color.FromArgb(55, 8, 9, 12);
    public static readonly Color ChartBorder = Color.FromArgb(30, 255, 255, 255);
    public static readonly Color ChartGrid = Color.FromArgb(22, 255, 255, 255);
    public static readonly Color ChartLabel = Color.FromArgb(125, 255, 255, 255);
    public static readonly Color EmptyText = Color.FromArgb(135, 255, 255, 255);
    /// <summary>Line mode stroke: Color.FromRgb(238, 92, 94).</summary>
    public static readonly Color LineStroke = Color.FromRgb(238, 92, 94);
    /// <summary>Rising candle: Color.FromRgb(239, 88, 91).</summary>
    public static readonly Color UpCandle = Color.FromRgb(239, 88, 91);
    /// <summary>Falling candle: Color.FromRgb(156, 161, 171).</summary>
    public static readonly Color DownCandle = Color.FromRgb(156, 161, 171);
}
