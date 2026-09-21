using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Visual constants shared by the todo windows. Values follow the Windows module:
/// a dark translucent rounded shell, a stacked card deck and a teal accent.
/// </summary>
internal static class TodoTheme
{
    /// <summary>
    /// Windows keeps the capsule at ARGB(72, 17, 20, 27) (28% opaque) and the
    /// expanded shells at ARGB(0xF4-0xF6). The Linux shells all use the near
    /// opaque <see cref="GlassSurface"/> instead: at 28% alpha the white text sits
    /// on a light grey over a bright desktop and looks smeared. Intentional
    /// readability deviation, see the porting notes.
    /// </summary>
    public static readonly IBrush ShellBackground = GlassSurface.Shell();
    public static readonly IBrush ShellBackgroundHover = GlassSurface.Hover();
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
    // only the main window uses the translucent shell above. Linux keeps their
    // alpha and adds the same frosted sheen as the shells.
    public static readonly IBrush DialogBackground = GlassSurface.Dialog(0xF6);
    public static readonly IBrush DialBackground = GlassSurface.Dialog(0xF4);
    public static readonly IBrush DateBackground = GlassSurface.Dialog(0xF5);
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
    /// Windows MinimalScrollBarStyle (TodoNotes/Program.cs L2222-2279) replaces
    /// the whole ScrollBar template: an 8px transparent bar whose track has no
    /// fill and whose paging arrows are invisible but still clickable, with a
    /// 3px radius grip inset by 1,2 that is at least 26px long. The Fluent
    /// template cannot be restyled into that from the outside: it scales the idle
    /// thumb down to a hairline (an overflowing deck therefore looks unscrollable
    /// until the pointer reaches the bar) and it pins the thumb's MinHeight
    /// inline, which beats any style setter. Installing this ControlTheme keeps
    /// the source values and removes both behaviours.
    /// </summary>
    public static void ApplyMinimalScrollBarStyle(StyledElement target)
    {
        var style = new Style(selector => selector.OfType<ScrollBar>());
        style.Setters.Add(new Setter(TemplatedControl.ThemeProperty, MinimalScrollBarTheme));
        target.Styles.Add(style);
    }

    private static readonly ControlTheme MinimalScrollBarTheme = BuildMinimalScrollBarTheme();

    private static ControlTheme BuildMinimalScrollBarTheme()
    {
        var theme = new ControlTheme(typeof(ScrollBar));
        theme.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent));
        theme.Setters.Add(new Setter(TemplatedControl.TemplateProperty,
            new FuncControlTemplate<ScrollBar>(BuildMinimalScrollBarTemplate)));
        theme.Children.Add(BarOrientationStyle(":vertical", new Thickness(2, 0, 0, 0)));
        theme.Children.Add(BarOrientationStyle(":horizontal", new Thickness(0, 2, 0, 0)));
        return theme;
    }

    /// <summary>Windows: Width 8, MinWidth 8 for a vertical bar and the mirror for a horizontal one.</summary>
    private static Style BarOrientationStyle(string pseudoClass, Thickness margin)
    {
        var vertical = pseudoClass == ":vertical";
        var style = new Style(selector => selector.Nesting().Class(pseudoClass));
        style.Setters.Add(new Setter(vertical ? Layoutable.WidthProperty : Layoutable.HeightProperty, 8d));
        style.Setters.Add(new Setter(vertical ? Layoutable.MinWidthProperty : Layoutable.MinHeightProperty, 8d));
        style.Setters.Add(new Setter(Layoutable.MarginProperty, margin));
        return style;
    }

    private static Control BuildMinimalScrollBarTemplate(ScrollBar bar, INameScope scope)
    {
        var vertical = bar.Orientation == Orientation.Vertical;
        var track = new Track
        {
            Name = "PART_Track",
            Orientation = bar.Orientation,
            // Windows: IsDirectionReversed=True with PageUp as the decrease button.
            IsDirectionReversed = true,
            Focusable = false
        };
        track.Bind(Track.OrientationProperty, bar.GetObservable(ScrollBar.OrientationProperty));
        track.Bind(Track.MinimumProperty, bar.GetObservable(RangeBase.MinimumProperty));
        track.Bind(Track.MaximumProperty, bar.GetObservable(RangeBase.MaximumProperty));
        track.Bind(Track.ViewportSizeProperty, bar.GetObservable(ScrollBar.ViewportSizeProperty));
        track.Bind(Track.ValueProperty, new Binding(nameof(RangeBase.Value))
        {
            Source = bar,
            Mode = BindingMode.TwoWay,
            Priority = BindingPriority.Template
        });
        track.DecreaseButton = PageButton("PART_PageUpButton", scope);
        track.IncreaseButton = PageButton("PART_PageDownButton", scope);
        track.Thumb = new Thumb
        {
            Theme = MinimalThumbTheme,
            // Windows `<Thumb MinHeight='26'>`; the Track reads this to size the grip.
            MinHeight = vertical ? 26 : 0,
            MinWidth = vertical ? 0 : 26
        };

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(track);
        return root;
    }

    /// <summary>
    /// Windows paging repeat buttons: transparent, invisible, still hit testable.
    /// Unlike WPF, Avalonia's RepeatButton has no default ControlTheme, so it must
    /// be given a template or it renders no visual at all and the transparent track
    /// stops accepting clicks. A code built template also has to register its own
    /// names, otherwise ScrollBar.OnApplyTemplate never finds
    /// PART_PageUpButton/PART_PageDownButton and the buttons are never wired.
    /// </summary>
    private static RepeatButton PageButton(string name, INameScope scope)
    {
        var button = new RepeatButton
        {
            Name = name,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = default,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Template = PageButtonTemplate
        };
        scope.Register(name, button);
        return button;
    }

    private static readonly FuncControlTemplate<RepeatButton> PageButtonTemplate =
        new((_, _) => new Border { Background = Brushes.Transparent });

    private static readonly ControlTheme MinimalThumbTheme = BuildMinimalThumbTheme();

    private static ControlTheme BuildMinimalThumbTheme()
    {
        var theme = new ControlTheme(typeof(Thumb));
        theme.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, ScrollThumb));
        theme.Setters.Add(new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(3)));
        theme.Setters.Add(new Setter(TemplatedControl.TemplateProperty,
            new FuncControlTemplate<Thumb>((thumb, _) =>
            {
                // Windows: `<Border x:Name='Grip' Margin='1,2' CornerRadius='3'>`.
                var grip = new Border { Margin = new Thickness(1, 2) };
                grip.Bind(Border.BackgroundProperty, thumb.GetObservable(TemplatedControl.BackgroundProperty));
                grip.Bind(Border.CornerRadiusProperty, thumb.GetObservable(TemplatedControl.CornerRadiusProperty));
                return grip;
            })));
        theme.Children.Add(ThumbStateStyle(":pointerover", ScrollThumbHover));
        theme.Children.Add(ThumbStateStyle(":pressed", ScrollThumbPressed));
        return theme;
    }

    private static Style ThumbStateStyle(string pseudoClass, IBrush brush)
    {
        var style = new Style(selector => selector.Nesting().Class(pseudoClass));
        style.Setters.Add(new Setter(TemplatedControl.BackgroundProperty, brush));
        return style;
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
