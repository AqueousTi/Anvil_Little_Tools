namespace LittleTools.Assistant.Services;

/// <summary>
/// Builds the one-shot notice shown when the desktop would not hand over every
/// global shortcut. On Windows <c>RegisterHotKey</c> conflicts are silent, which
/// leaves users thinking the feature is broken; on Linux we say what happened.
/// The signature lets the caller avoid repeating the same notice every launch.
/// </summary>
internal static class HotkeyNotice
{
    public static string Signature(LinuxSessionKind session, IReadOnlyList<string> failed) =>
        SessionEnvironment.Describe(session) + ":" + string.Join(",", failed);

    /// <summary>Returns null when every requested shortcut is available.</summary>
    public static string? Build(LinuxSessionKind session, string launchCommand, IReadOnlyList<string> failed, bool registered)
    {
        if (session == LinuxSessionKind.Wayland)
        {
            return "Wayland 不允许程序注册全局快捷键，请在系统设置中添加自定义快捷键，命令："
                   + launchCommand + " --toggle";
        }

        if (failed.Count == 0) return null;

        if (session == LinuxSessionKind.Unknown)
            return "无法识别当前桌面会话，未能注册全局快捷键（" + string.Join("、", failed) + "）。可用托盘菜单唤起窗口。";

        return registered
            ? "部分全局快捷键已被其它程序占用：" + string.Join("、", failed) + "。其余快捷键与托盘菜单仍可使用。"
            : "全局快捷键均已被其它程序占用（" + string.Join("、", failed) + "）。可用托盘菜单唤起窗口。";
    }
}
