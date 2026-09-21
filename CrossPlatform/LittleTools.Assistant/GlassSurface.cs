using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LittleTools.Assistant;

/// <summary>
/// The frosted panel surface shared by every Linux suite window: a nearly opaque
/// dark base with a barely visible vertical sheen and the existing hairline
/// border.
///
/// Why nearly opaque: the Windows panels are translucent (the capsule is
/// ARGB(62, 17, 20, 27), StockMonitor/StockWindow.cs L963; the todo capsule is
/// ARGB(72, ...), TodoTheme) which reads as a crisp white on white text only on a
/// dark wallpaper. On a white desktop 0.28 * (17, 20, 27) + 0.72 * 255 is a light
/// grey, so the white glyphs lose almost all contrast and look smeared. This is
/// an intentional, user requested readability deviation from Windows.
///
/// Why the gradient: with the blur unavailable (see
/// <see cref="TransparencyLevels"/>) a flat fill at this alpha looks like a plain
/// solid slab; a 10 step sheen reads as a frosted pane without becoming a visible
/// gradient band.
/// </summary>
internal static class GlassSurface
{
    /// <summary>Panel alpha for the shells: 0xF0, about 94%.</summary>
    public const byte ShellAlpha = 0xF0;

    /// <summary>Hover state: a little brighter, still readable on white.</summary>
    public const byte HoverAlpha = 0xF8;

    /// <summary>Dialog and drawer alpha, keeping the Windows 0xF4-0xF6 values.</summary>
    public const byte DialogAlpha = 0xF6;

    // 0x11141B lifted/dropped by about 10 per channel: enough to feel like glass,
    // too little to read as a gradient.
    private const byte TopR = 0x17, TopG = 0x1B, TopB = 0x23;
    private const byte BottomR = 0x0D, BottomG = 0x10, BottomB = 0x16;

    /// <summary>
    /// The transparency levels every suite window asks for, most capable first.
    /// Avalonia's X11 backend reports Blur only when the window manager is KWin
    /// (Avalonia.X11.TransparencyHelper.IsSupported: <c>WmName == "KWin"</c>) and
    /// never reports AcrylicBlur, so on GNOME this falls back to Transparent and
    /// the opaque surface above carries the look; on KDE the window really gets
    /// <c>_KDE_NET_WM_BLUR_BEHIND_REGION</c>.
    /// </summary>
    public static readonly IReadOnlyList<WindowTransparencyLevel> TransparencyLevels =
    [
        WindowTransparencyLevel.AcrylicBlur,
        WindowTransparencyLevel.Blur,
        WindowTransparencyLevel.Transparent
    ];

    /// <summary>The shell surface: 0xF0 alpha, top slightly lighter than bottom.</summary>
    public static IBrush Shell(byte alpha = ShellAlpha) => Surface(alpha);

    /// <summary>The hover surface, used while the pointer is over a capsule.</summary>
    public static IBrush Hover() => Surface(HoverAlpha);

    /// <summary>A dialog or expanded shell surface at the Windows alpha.</summary>
    public static IBrush Dialog(byte alpha = DialogAlpha) => Surface(alpha);

    /// <summary>The same RGB ramp at an explicit alpha, for the contrast sweep.</summary>
    public static IBrush Surface(byte alpha) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(alpha, TopR, TopG, TopB), 0),
            new GradientStop(Color.FromArgb(alpha, BottomR, BottomG, BottomB), 1)
        }
    };
}
