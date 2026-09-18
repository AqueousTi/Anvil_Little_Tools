using LittleTools.Assistant;
using LittleTools.Assistant.Platform;
using LittleTools.Assistant.Services;
using LittleTools.Common;

/// <summary>
/// Covers the Linux suite host pieces that are pure logic: the shared module
/// switches, the tray menu model, session detection and the XDG autostart entry.
/// </summary>
internal static class SuitePlatformTests
{
    public static void Run(Action<string, bool> check)
    {
        CheckSuiteSettings(check);
        CheckMenu(check);
        CheckSession(check);
        CheckAutostart(check);
        check("toggle forwards as --toggle", SuiteLauncher.ArgumentFor(AppCommand.Toggle) == "--toggle");
        CheckPeerDetection(check);
        CheckHotkeyNotice(check);
    }

    private static void CheckHotkeyNotice(Action<string, bool> check)
    {
        check("no notice when every shortcut registered",
            HotkeyNotice.Build(LinuxSessionKind.X11, "/opt/lt", [], registered: true) is null);

        var partial = HotkeyNotice.Build(LinuxSessionKind.X11, "/opt/lt", ["Ctrl+Alt+X"], registered: true);
        check("partial conflict is reported", partial is not null && partial.Contains("Ctrl+Alt+X", StringComparison.Ordinal));
        check("partial conflict still mentions the tray", partial!.Contains("托盘", StringComparison.Ordinal));

        var total = HotkeyNotice.Build(LinuxSessionKind.X11, "/opt/lt", ["Ctrl+Alt+X", "Ctrl+Backspace"], registered: false);
        check("total conflict is reported", total!.Contains("均已被其它程序占用", StringComparison.Ordinal));

        var wayland = HotkeyNotice.Build(LinuxSessionKind.Wayland, "/opt/lt", [], registered: false);
        check("wayland points at desktop shortcuts",
            wayland!.Contains("Wayland", StringComparison.Ordinal) && wayland.Contains("/opt/lt --toggle", StringComparison.Ordinal));

        check("conflict signature changes with the chords",
            HotkeyNotice.Signature(LinuxSessionKind.X11, ["Ctrl+Alt+X"]) != HotkeyNotice.Signature(LinuxSessionKind.X11, []));
    }

    /// <summary>
    /// The named mutex is per login session on Linux, so the command pipe is the
    /// authoritative single-instance check. It must answer for a live instance and
    /// stay silent once that instance is gone.
    /// </summary>
    private static void CheckPeerDetection(Action<string, bool> check)
    {
        var pipeName = "LittleTools.Tests.Peer." + Guid.NewGuid().ToString("N");
        using (var coordinator = new SingleInstanceCoordinator(isolated: true))
            check("no peer before anyone listens", !coordinator.HasLivePeer(pipeName));

        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using (var server = new CommandPipe(pipeName, received.Enqueue))
        {
            using var coordinator = new SingleInstanceCoordinator(isolated: true);
            check("live peer detected through the pipe", coordinator.HasLivePeer(pipeName));
            check("peer probe dispatches no command", received.IsEmpty);
        }

        using (var coordinator = new SingleInstanceCoordinator(isolated: true))
            check("peer disappears with the instance", !coordinator.HasLivePeer(pipeName));
    }

    private static void CheckSuiteSettings(Action<string, bool> check)
    {
        var root = TempDirectory();
        try
        {
            var path = Path.Combine(root, "manager.json");

            // Exactly what the Windows tray host writes with JavaScriptSerializer.
            File.WriteAllText(path,
                "{\"MonitorEnabled\":true,\"TranslateEnabled\":false,\"TodoNotesEnabled\":true,\"StockEnabled\":false," +
                "\"EdgeHideMonitor\":false,\"EdgeHideTranslate\":true,\"EdgeHideTodo\":false,\"EdgeHideStock\":true}");
            var store = new SuiteSettingsStore(path);
            check("windows manager.json: translate switch", !store.Current.TranslateEnabled);
            check("windows manager.json: stock switch", !store.Current.StockEnabled);
            check("windows manager.json: edge hide translate", store.Current.EdgeHideTranslate);
            check("windows manager.json: edge hide stock", store.Current.EdgeHideStock);

            store.Set(settings =>
            {
                settings.TodoNotesEnabled = false;
                return true;
            });
            var rewritten = File.ReadAllText(path);
            check("manager.json keeps PascalCase field names", rewritten.Contains("\"TodoNotesEnabled\"", StringComparison.Ordinal));
            check("manager.json keeps the Windows field set", rewritten.Contains("\"EdgeHideMonitor\"", StringComparison.Ordinal));
            check("manager.json rewrites the switch", new SuiteSettingsStore(path).Current.TodoNotesEnabled == false);

            var untouched = File.ReadAllText(path);
            store.Set(_ => false);
            check("no-op change does not rewrite", File.ReadAllText(path) == untouched);

            check("missing manager.json uses defaults", new SuiteSettingsStore(Path.Combine(root, "absent.json")).Current.TranslateEnabled);

            // A read-only store models the assistant on Windows, where the WPF
            // tray host owns manager.json and must not be overwritten.
            var readOnlyPath = Path.Combine(root, "readonly.json");
            File.WriteAllText(readOnlyPath, "{\"TranslateEnabled\":true}");
            var readOnly = new SuiteSettingsStore(readOnlyPath, persist: false);
            readOnly.Set(settings =>
            {
                settings.TranslateEnabled = false;
                return true;
            });
            check("read-only store reports no persistence", !readOnly.CanPersist);
            check("read-only store keeps the file", File.ReadAllText(readOnlyPath) == "{\"TranslateEnabled\":true}");
            check("read-only store updates memory", !readOnly.Current.TranslateEnabled);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void CheckMenu(Action<string, bool> check)
    {
        var settings = new SuiteSettings { TranslateEnabled = true, TodoNotesEnabled = false };
        var linux = SuiteMenuBuilder.Build(settings, autostartEnabled: true, linux: true);

        string[] Ids() => linux.Where(item => item.Kind != SuiteMenuKind.Separator).Select(item => item.Id).ToArray();
        check("tray keeps the Windows entry order",
            Ids().SequenceEqual(new[]
            {
                SuiteMenuBuilder.Translate, SuiteMenuBuilder.Chat, SuiteMenuBuilder.Screenshot,
                SuiteMenuBuilder.Monitor, SuiteMenuBuilder.Assistant, SuiteMenuBuilder.Todo, SuiteMenuBuilder.Stock,
                SuiteMenuBuilder.Autostart, SuiteMenuBuilder.OpenDirectory, SuiteMenuBuilder.Exit
            }));
        check("tray separator count", linux.Count(item => item.Kind == SuiteMenuKind.Separator) == 4);

        var assistant = linux.Single(item => item.Id == SuiteMenuBuilder.Assistant);
        check("assistant module is a checked toggle", assistant.Kind == SuiteMenuKind.Toggle && assistant.IsChecked && assistant.IsEnabled);
        var todo = linux.Single(item => item.Id == SuiteMenuBuilder.Todo);
        check("todo module is available on linux", todo.IsEnabled);
        check("todo module reflects its switch", !todo.IsChecked);
        check("unported modules stay disabled on linux",
            !linux.Single(item => item.Id == SuiteMenuBuilder.Monitor).IsEnabled
            && !linux.Single(item => item.Id == SuiteMenuBuilder.Stock).IsEnabled);
        var autostart = linux.Single(item => item.Id == SuiteMenuBuilder.Autostart);
        check("autostart reflects the entry state", autostart.IsChecked && autostart.IsEnabled);
        check("exit stays enabled", linux.Single(item => item.Id == SuiteMenuBuilder.Exit).IsEnabled);

        var windows = SuiteMenuBuilder.Build(settings, autostartEnabled: false, linux: false);
        check("windows keeps modules enabled", windows.Single(item => item.Id == SuiteMenuBuilder.Todo).IsEnabled);
        check("windows hides the autostart toggle", !windows.Single(item => item.Id == SuiteMenuBuilder.Autostart).IsEnabled);
        check("module availability matches platform", SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Stock, true) == false
            && SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Stock, false));
        check("todo availability matches both platforms",
            SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Todo, true)
            && SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Todo, false));
        check("todo forwards as --todo", SuiteLauncher.ArgumentFor(AppCommand.ShowTodo) == "--todo");
    }

    private static void CheckSession(Action<string, bool> check)
    {
        check("wayland by session type", SessionEnvironment.Detect("wayland", null, ":0") == LinuxSessionKind.Wayland);
        check("wayland by display variable", SessionEnvironment.Detect("", "wayland-0", ":0") == LinuxSessionKind.Wayland);
        check("x11 by session type", SessionEnvironment.Detect("x11", null, ":1") == LinuxSessionKind.X11);
        check("x11 by DISPLAY fallback", SessionEnvironment.Detect(null, null, ":1") == LinuxSessionKind.X11);
        check("unknown without display", SessionEnvironment.Detect(null, null, null) == LinuxSessionKind.Unknown);
        check("only x11 grabs global keys",
            SessionEnvironment.CanGrabGlobalKeys(LinuxSessionKind.X11)
            && !SessionEnvironment.CanGrabGlobalKeys(LinuxSessionKind.Wayland)
            && !SessionEnvironment.CanGrabGlobalKeys(LinuxSessionKind.Unknown));
        check("session description", SessionEnvironment.Describe(LinuxSessionKind.Wayland) == "Wayland");
    }

    private static void CheckAutostart(Action<string, bool> check)
    {
        check("autostart entry declarative keys",
            AutostartService.BuildEntry("/opt/little-tools/LittleTools", "/opt/little-tools/icon.png", 30)
                .StartsWith("[Desktop Entry]", StringComparison.Ordinal)
            && AutostartService.BuildEntry("/opt/little-tools/LittleTools", "/opt/little-tools/icon.png", 30)
                .Contains("X-GNOME-Autostart-enabled=true", StringComparison.Ordinal));

        var deferred = AutostartService.BuildEntry("/opt/little-tools/LittleTools", string.Empty, 30);
        check("autostart defers like the Windows task",
            deferred.Contains("sleep 30", StringComparison.Ordinal) && deferred.Contains("--background", StringComparison.Ordinal));
        check("autostart keeps a single Exec line", deferred.Split('\n').Count(line => line.StartsWith("Exec=", StringComparison.Ordinal)) == 1);
        check("autostart omits an empty icon", !deferred.Contains("Icon=", StringComparison.Ordinal));

        var plain = AutostartService.BuildEntry("/opt/little-tools/LittleTools", string.Empty, 0);
        check("zero delay stays a direct exec",
            plain.Contains("Exec=/opt/little-tools/LittleTools --background", StringComparison.Ordinal));

        check("exec quoting leaves simple paths alone", AutostartService.QuoteExecArgument("/opt/app") == "/opt/app");
        check("exec quoting escapes spaces", AutostartService.QuoteExecArgument("/opt/my app").StartsWith('"'));
        check("exec quoting escapes dollars",
            AutostartService.QuoteExecArgument("/opt/$app").Contains("\\$", StringComparison.Ordinal));

        check("dotnet launch command keeps the assembly",
            AutostartService.ResolveLaunchCommand("/usr/bin/dotnet", "/opt/app/LittleTools.dll").Contains("LittleTools.dll", StringComparison.Ordinal));
        check("apphost launch command is the executable itself",
            AutostartService.ResolveLaunchCommand("/opt/app/LittleTools", "/opt/app/LittleTools") == "/opt/app/LittleTools");

        var root = TempDirectory();
        try
        {
            var service = new AutostartService(Path.Combine(root, "autostart"));
            check("autostart starts disabled", !service.IsEnabled);
            check("autostart enables", service.Enable("/opt/app/LittleTools", "/opt/app/icon.png") && service.IsEnabled);
            check("autostart entry on disk", File.Exists(service.EntryPath));
            check("autostart has no temp leftovers", !File.Exists(service.EntryPath + ".tmp"));
            check("autostart disables", service.Disable() && !service.IsEnabled);
            check("autostart disable is idempotent", service.Disable());
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string TempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "LittleTools-platform-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        Directory.Delete(root, true);
    }
}
