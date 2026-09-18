namespace LittleTools.Assistant.Services;

internal enum LinuxSessionKind
{
    Unknown,
    X11,
    Wayland
}

/// <summary>
/// Detects which Linux display session the app is running in. The inputs are
/// passed in rather than read from the environment so the rules stay testable.
/// </summary>
internal static class SessionEnvironment
{
    public static LinuxSessionKind Detect(string? sessionType, string? waylandDisplay, string? display)
    {
        if (!string.IsNullOrWhiteSpace(sessionType)
            && sessionType.Contains("wayland", StringComparison.OrdinalIgnoreCase))
            return LinuxSessionKind.Wayland;
        if (!string.IsNullOrWhiteSpace(waylandDisplay)) return LinuxSessionKind.Wayland;
        if (!string.IsNullOrWhiteSpace(sessionType)
            && sessionType.Contains("x11", StringComparison.OrdinalIgnoreCase))
            return LinuxSessionKind.X11;
        if (!string.IsNullOrWhiteSpace(display)) return LinuxSessionKind.X11;
        return LinuxSessionKind.Unknown;
    }

    public static LinuxSessionKind Current => Detect(
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
        Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>
    /// Global key grabs only exist on X11. Under Wayland the desktop must own the
    /// shortcut, so the caller falls back to the documented custom-shortcut path.
    /// </summary>
    public static bool CanGrabGlobalKeys(LinuxSessionKind session) => session == LinuxSessionKind.X11;

    public static string Describe(LinuxSessionKind session) => session switch
    {
        LinuxSessionKind.X11 => "X11",
        LinuxSessionKind.Wayland => "Wayland",
        _ => "未知会话"
    };
}
