using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

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

    // Sub item panel, mirroring the Windows BuildSubItemsPanel colours.
    /// <summary>Windows sub item progress turns green when every child is done.</summary>
    public static readonly IBrush SubItemDone = Brush(0xFF, 0x7E, 0xD3, 0x90);
    /// <summary>Windows separator above the rows: ARGB(35, 255, 255, 255).</summary>
    public static readonly IBrush SubItemSeparator = Brush(0x23, 0xFF, 0xFF, 0xFF);
    /// <summary>Windows BuildSubItemInput host: ARGB(11) fill with an ARGB(32) underline.</summary>
    public static readonly IBrush SubItemInputBackground = Brush(0x0B, 0xFF, 0xFF, 0xFF);
    public static readonly IBrush SubItemInputBorder = Brush(0x20, 0xFF, 0xFF, 0xFF);
    /// <summary>Windows "添加一步…" placeholder: ARGB(95, 220, 224, 232).</summary>
    public static readonly IBrush SubItemPlaceholder = Brush(0x5F, 0xDC, 0xE0, 0xE8);
    /// <summary>Windows enter hint: ARGB(75, 220, 224, 232).</summary>
    public static readonly IBrush SubItemHint = Brush(0x4B, 0xDC, 0xE0, 0xE8);
    /// <summary>Windows capsule sub item tick border: ARGB(75, 235, 238, 244).</summary>
    public static readonly IBrush CompactSubItemBorder = Brush(0x4B, 0xEB, 0xEE, 0xF4);

    // Windows MinimalScrollBarStyle thumb colours.
    public static readonly IBrush ScrollThumb = Brush(0x66, 0x8F, 0x94, 0x9E);
    public static readonly IBrush ScrollThumbHover = Brush(0xA6, 0xB8, 0xBD, 0xC7);
    public static readonly IBrush ScrollThumbPressed = Brush(0xD6, 0xDD, 0xE1, 0xE8);

    /// <summary>Windows card padding: 11 left, 6 top, 8 right, 8 bottom.</summary>
    public static readonly Thickness CardPadding = new(11, 6, 8, 8);

    public const double CompactWidth = 316;
    /// <summary>Windows CompactBaseHeight; the capsule grows from here with sub items.</summary>
    public const double CompactHeight = TodoLogic.CompactBaseHeight;
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

    /// <summary>
    /// Windows MinimalScrollBarStyle: an 8px wide transparent bar whose thumb is a
    /// 3px radius grip, inset by 1,2 and at least 26px long. Like the Windows
    /// version this replaces the whole look, so the track and the paging arrows
    /// disappear.
    /// </summary>
    public static void ApplyMinimalScrollBarStyle(StyledElement target)
    {
        var bar = new Style(selector => selector.OfType<ScrollBar>());
        bar.Setters.Add(new Setter(Layoutable.WidthProperty, 8d));
        bar.Setters.Add(new Setter(Layoutable.MinWidthProperty, 8d));
        bar.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(2, 0, 0, 0)));
        bar.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        target.Styles.Add(bar);

        AddThumbStyle(target, selector => selector.OfType<Thumb>(), ScrollThumb);
        AddThumbStyle(target, selector => selector.OfType<Thumb>().Class(":pointerover"), ScrollThumbHover);
        AddThumbStyle(target, selector => selector.OfType<Thumb>().Class(":pressed"), ScrollThumbPressed);
    }

    /// <summary>
    /// Avalonia's Fluent ScrollBar template fixes its thumb to a 16px minimum
    /// length inline, which no style can win against. The Windows value (26) is
    /// therefore pinned on the realized thumb as soon as a bar gets its template.
    /// Call this for every scroller that should carry the thin bar.
    /// </summary>
    public static void WatchScrollBars(TemplatedControl host)
    {
        host.TemplateApplied += (_, _) => AttachScrollBars(host);
        AttachScrollBars(host);
    }

    private static void AttachScrollBars(TemplatedControl host)
    {
        foreach (var bar in host.GetVisualDescendants().OfType<ScrollBar>())
        {
            bar.TemplateApplied -= OnBarTemplateApplied;
            bar.TemplateApplied += OnBarTemplateApplied;
            PinThumbLength(bar);
        }
    }

    private static void OnBarTemplateApplied(object? sender, TemplateAppliedEventArgs args)
    {
        if (sender is ScrollBar bar) PinThumbLength(bar);
    }

    private static void PinThumbLength(ScrollBar bar)
    {
        foreach (var thumb in bar.GetVisualDescendants().OfType<Thumb>())
            if (thumb.MinHeight < 26) thumb.MinHeight = 26;
    }

    private static void AddThumbStyle(StyledElement target, Func<Selector?, Selector> selector, IBrush brush)
    {
        var style = new Style(selector);
        style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, brush));
        style.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3)));
        style.Setters.Add(new Setter(Layoutable.MinHeightProperty, 26d));
        style.Setters.Add(new Setter(Layoutable.MarginProperty, new Thickness(1, 2)));
        target.Styles.Add(style);
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
