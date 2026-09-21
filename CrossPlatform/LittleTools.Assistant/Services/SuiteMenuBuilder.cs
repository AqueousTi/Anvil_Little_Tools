namespace LittleTools.Assistant.Services;

internal enum SuiteMenuKind
{
    Separator,
    Action,
    Toggle
}

/// <summary>A platform independent description of one tray menu entry.</summary>
internal sealed record SuiteMenuItem(SuiteMenuKind Kind, string Id, string Title, bool IsEnabled = true, bool IsChecked = false)
{
    public static SuiteMenuItem Separator() => new(SuiteMenuKind.Separator, string.Empty, string.Empty);
    public static SuiteMenuItem Action(string id, string title, bool enabled = true) => new(SuiteMenuKind.Action, id, title, enabled);
    public static SuiteMenuItem Toggle(string id, string title, bool checkedState) => new(SuiteMenuKind.Toggle, id, title, true, checkedState);
}

/// <summary>
/// Builds the tray menu so it matches the Windows suite host: the three assistant
/// entries, the module switches, autostart, the tool directory and exit.
/// Modules that have not been ported yet stay visible but disabled, which keeps the
/// menu recognisable and states the porting status instead of hiding it.
/// </summary>
internal static class SuiteMenuBuilder
{
    public const string Translate = "translate";
    public const string Chat = "chat";
    public const string Screenshot = "screenshot";
    public const string Monitor = "monitor";
    public const string Assistant = "assistant";
    public const string Todo = "todo";
    public const string Stock = "stock";
    public const string Autostart = "autostart";
    public const string OpenDirectory = "opendir";
    public const string Exit = "exit";

    /// <summary>Modules that exist on this platform. Later phases flip these on.</summary>
    public static bool IsModuleAvailable(string id, bool linux) => id switch
    {
        Monitor => !linux,
        // Todo and stock are implemented natively on both platforms.
        Stock => true,
        Todo => true,
        _ => true
    };

    public static IReadOnlyList<SuiteMenuItem> Build(SuiteSettings settings, bool autostartEnabled, bool linux)
    {
        var items = new List<SuiteMenuItem>
        {
            SuiteMenuItem.Action(Translate, "翻译"),
            SuiteMenuItem.Action(Chat, "问答"),
            SuiteMenuItem.Action(Screenshot, "截图翻译"),
            SuiteMenuItem.Separator(),
            SuiteMenuItem.Toggle(Monitor, "余量监控", settings.MonitorEnabled) with { IsEnabled = IsModuleAvailable(Monitor, linux) },
            SuiteMenuItem.Toggle(Assistant, "AI 翻译与快问", settings.TranslateEnabled),
            SuiteMenuItem.Toggle(Todo, "每日待办", settings.TodoNotesEnabled) with { IsEnabled = IsModuleAvailable(Todo, linux) },
            SuiteMenuItem.Toggle(Stock, "股票观察", settings.StockEnabled) with { IsEnabled = IsModuleAvailable(Stock, linux) },
            SuiteMenuItem.Separator(),
            SuiteMenuItem.Toggle(Autostart, "开机自启", autostartEnabled) with { IsEnabled = linux }
        };
        items.Add(SuiteMenuItem.Separator());
        items.Add(SuiteMenuItem.Action(OpenDirectory, "打开工具目录"));
        items.Add(SuiteMenuItem.Separator());
        items.Add(SuiteMenuItem.Action(Exit, "退出"));
        return items;
    }
}
