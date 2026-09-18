using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Visual constants shared by the todo windows. Values follow the Windows module:
/// a dark translucent rounded shell, a stacked card deck and a teal accent.
/// </summary>
internal static class TodoTheme
{
    public static readonly IBrush ShellBackground = Brush(0x48, 0x11, 0x14, 0x1B);
    public static readonly IBrush ShellBackgroundHover = Brush(0x70, 0x11, 0x14, 0x1B);
    public static readonly IBrush ShellBorder = Brush(0x30, 0xFF, 0xFF, 0xFF);
    public static readonly IBrush PrimaryText = Brushes.White;
    public static readonly IBrush SecondaryText = Brush(0x99, 0xFF, 0xFF, 0xFF);
    public static readonly IBrush MutedText = Brush(0x72, 0xFF, 0xFF, 0xFF);
    public static readonly IBrush Accent = Brush(0xFF, 0x7C, 0xED, 0xAE);
    public static readonly IBrush AccentSoft = Brush(0x18, 0x7C, 0xED, 0xAE);
    public static readonly IBrush Danger = Brush(0xFF, 0xFF, 0x66, 0x77);
    public static readonly IBrush SelectedRow = Brush(0x66, 0xE1, 0x4B, 0x5A);
    public static readonly IBrush ControlBackground = Brush(0x24, 0xFF, 0xFF, 0xFF);
    public static readonly IBrush ControlBorder = Brush(0x30, 0xFF, 0xFF, 0xFF);
    public static readonly IBrush CanvasBackground = Brush(0x37, 0x08, 0x09, 0x0C);

    // Dialogs and the backlog drawer are almost opaque in the Windows module;
    // only the main window uses the translucent shell above.
    public static readonly IBrush DialogBackground = Brush(0xF6, 0x11, 0x14, 0x1B);
    public static readonly IBrush DialBackground = Brush(0xF4, 0x11, 0x14, 0x1B);
    public static readonly IBrush DateBackground = Brush(0xF5, 0x11, 0x14, 0x1B);
    public static readonly IBrush DialogBorder = Brush(0x41, 0xFF, 0xFF, 0xFF);
    public static readonly IBrush CardBorder = Brush(0x34, 0xFF, 0xFF, 0xFF);
    public static readonly IBrush ListCardBackground = Brush(0x64, 0x1D, 0x20, 0x28);
    public static readonly IBrush DialTickMinor = Brush(0xA5, 0xEB, 0xEE, 0xF4);
    public static readonly IBrush DialTickMajor = Brush(0xEB, 0xF7, 0xDC, 0xDC);
    public static readonly IBrush DialRed = Brush(0xFF, 0xE7, 0x46, 0x3F);

    public static readonly Thickness CardPadding = new(11, 8, 8, 11);

    public const double CompactWidth = 316;
    public const double CompactHeight = 92;
    public const double ExpandedWidth = 430;
    public const double ExpandedHeight = 530;
    public const double ShellCornerRadius = 15;
    public const double CardCornerRadius = 12;

    /// <summary>Card background for a deck position, matching ApplyCardLayer.</summary>
    public static IBrush CardBackground(int index)
    {
        var alpha = TodoLogic.CardAlpha(index);
        return Brush((byte)alpha, 25, 27, 33);
    }

    /// <summary>Deck shadow: lifted while dragging, then two depth levels.</summary>
    public static BoxShadows CardShadow(int index, bool lifted = false)
    {
        if (lifted)
            return new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 5, Color = Color.FromArgb(71, 0, 0, 0) });
        return index == 0
            ? new BoxShadows(new BoxShadow { Blur = 11, OffsetY = 3, Color = Color.FromArgb(51, 0, 0, 0) })
            : new BoxShadows(new BoxShadow { Blur = 5, OffsetY = 1, Color = Color.FromArgb(20, 0, 0, 0) });
    }

    public static readonly FontFamily UiFont = new("Segoe UI,Inter,Noto Sans CJK SC,Sans Serif");

    public static IBrush Brush(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));

    public static TextBlock Label(string text, double size = 12, IBrush? foreground = null, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = UiFont,
        Foreground = foreground ?? PrimaryText,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    public static Border Rounded(Control child, double radius, IBrush? background = null, IBrush? border = null,
        Thickness? padding = null) => new()
    {
        Child = child,
        CornerRadius = new CornerRadius(radius),
        Background = background,
        BorderBrush = border,
        BorderThickness = border is null ? default : new Thickness(1),
        Padding = padding ?? default
    };

    /// <summary>A dark, flat text button matching the widget style.</summary>
    public static Button TextButton(string content, double fontSize = 10.5, double? width = null)
    {
        var button = new Button
        {
            Content = content,
            FontSize = fontSize,
            FontFamily = UiFont,
            Foreground = PrimaryText,
            Background = ControlBackground,
            BorderBrush = ControlBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 4),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        if (width is not null) button.Width = width.Value;
        return button;
    }

    /// <summary>A borderless icon button used inside cards and headers.</summary>
    public static Button IconButton(Control icon, double size = 27)
    {
        return new Button
        {
            Content = icon,
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Focusable = false
        };
    }
}
