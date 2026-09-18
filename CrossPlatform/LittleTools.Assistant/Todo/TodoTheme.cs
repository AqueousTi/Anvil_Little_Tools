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

    /// <summary>Windows shell DropShadowEffect: blur 12, depth 1, opacity 0.16.</summary>
    public static readonly BoxShadows ShellShadow =
        new(new BoxShadow { Blur = 12, OffsetY = 1, Color = Color.FromArgb(41, 0, 0, 0) });
    public static readonly IBrush PrimaryText = Brushes.White;
    /// <summary>Windows SecondaryText(): ARGB(150, 220, 224, 232).</summary>
    public static readonly IBrush SecondaryText = Brush(0x96, 0xDC, 0xE0, 0xE8);
    public static readonly IBrush MutedText = Brush(0x96, 0xDC, 0xE0, 0xE8);
    public static readonly IBrush Accent = Brush(0xFF, 0x7C, 0xED, 0xAE);
    public static readonly IBrush AccentSoft = Brush(0x18, 0x7C, 0xED, 0xAE);
    public static readonly IBrush Danger = Brush(0xFF, 0xFF, 0x66, 0x77);
    public static readonly IBrush SelectedRow = Brush(0x66, 0xE1, 0x4B, 0x5A);
    public static readonly IBrush ControlBackground = Brush(0x24, 0xFF, 0xFF, 0xFF);
    /// <summary>Windows SmallButton border: ARGB(45, 255, 255, 255).</summary>
    public static readonly IBrush ControlBorder = Brush(0x2D, 0xFF, 0xFF, 0xFF);
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

    /// <summary>Windows SmallButton: 28 tall, 10pt, radius 7, 68% opacity while pressed.</summary>
    public static Button TextButton(string content, double fontSize = 10, double? width = null)
    {
        var button = new Button
        {
            Content = content,
            Height = 28,
            FontSize = fontSize,
            FontFamily = UiFont,
            Foreground = PrimaryText,
            Background = ControlBackground,
            BorderBrush = ControlBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(4, 2),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        if (width is not null) button.Width = width.Value;
        ApplyPressFeedback(button);
        return button;
    }

    /// <summary>The Windows InputBox style: translucent field with 10,7 padding.</summary>
    public static TextBox InputBox(string placeholder, double fontSize = 12)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            FontSize = fontSize,
            FontFamily = UiFont,
            Foreground = PrimaryText,
            CaretBrush = PrimaryText,
            Background = Brush(0x26, 0xFF, 0xFF, 0xFF),
            BorderBrush = Brush(0x30, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(10, 7, 10, 7),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        return box;
    }

    /// <summary>Applies the Windows pressed feedback (opacity 0.68).</summary>
    public static void ApplyPressFeedback(Button button)
    {
        button.PropertyChanged += (_, args) =>
        {
            if (args.Property == Button.IsPressedProperty)
                button.Opacity = button.IsPressed ? 0.68 : 1;
        };
    }

    /// <summary>A borderless icon button used inside cards and headers.</summary>
    public static Button IconButton(Control icon, double size = 27)
    {
        var button = new Button
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
        ApplyPressFeedback(button);
        return button;
    }
}
