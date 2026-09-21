using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using LittleTools.Assistant.Platform;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant;

public sealed partial class App : Application
{
    internal static SingleInstanceCoordinator? Coordinator { get; set; }
    internal static AppCommand StartupCommand { get; set; } = AppCommand.ShowTranslation;
    internal static bool SmokeTest { get; set; }
    internal static bool ManagedMode { get; set; }
    internal static string? RenderTestPath { get; set; }
    internal static string? LayoutSmokePath { get; set; }
    internal static string? ChatImageSmokePath { get; set; }
    internal static string? TranslationSmokePath { get; set; }
    internal static string? ScreenshotSmokeInputPath { get; set; }
    internal static string? AnnotationTestInputPath { get; set; }
    internal static string? AnnotationTestOutputPath { get; set; }
    internal static string? DiagnosePath { get; set; }
    internal static string? TodoSmokePath { get; set; }
    internal static string? StockSmokePath { get; set; }

    /// <summary>Adds a live market data pass to the stock render smoke.</summary>
    internal static bool StockLive { get; set; }

    private MainWindow? _window;
    private IGlobalHotkeyService? _hotkey;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private SuiteSettingsStore? _suiteSettings;
    private AutostartService? _autostart;
    private INotificationService _notifications = new NullNotificationService();
    private Todo.TodoModule? _todo;
    private Stock.StockModule? _stock;
    private readonly Dictionary<string, NativeMenuItem> _menuItems = new(StringComparer.Ordinal);
    private bool _exitRequested;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Avalonia's DBus tray watcher reports a task cancellation while the
            // application tears down; unhandled it aborts the process on exit, so
            // it is swallowed once shutdown has been requested.
            Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
            {
                if (_exitRequested && eventArgs.Exception is OperationCanceledException)
                    eventArgs.Handled = true;
            };
            desktop.Exit += (_, _) =>
            {
                _hotkey?.Dispose();
                _tray?.Dispose();
            };
            var settings = new SettingsStore();
            var history = new ConversationStore();
            _window = new MainWindow(settings, history, ScreenshotServiceFactory.Create());
            // A background or todo-only launch must not let the desktop lifetime
            // auto-show the assistant window.
            if (StartupCommand is not (AppCommand.Background or AppCommand.ShowTodo))
                desktop.MainWindow = _window;
            _window.Closing += (_, eventArgs) =>
            {
                if (_exitRequested) return;
                eventArgs.Cancel = true;
                _window.Hide();
            };

            Coordinator?.StartListening((command, managed) => Dispatcher.UIThread.Post(() =>
            {
                if (managed)
                {
                    ManagedMode = true;
                    _tray?.Dispose();
                    _tray = null;
                }
                HandleCommand(command);
            }));
            if (!SmokeTest)
            {
                _suiteSettings = new SuiteSettingsStore();
                _suiteSettings.EnsureFile();
                _autostart = OperatingSystem.IsLinux() ? new AutostartService() : null;
                _notifications = NotificationServiceFactory.Create();
                _hotkey = GlobalHotkeyServiceFactory.Create();
                _hotkey.Start(command => Dispatcher.UIThread.Post(() => HandleCommand(command)));
            }
            if (!ManagedMode && !SmokeTest)
                try { CreateTray(); } catch { }
            if (!ManagedMode && !SmokeTest && OperatingSystem.IsLinux())
                NotifyHotkeyFallback();
            ApplyTodoModuleState();
            ApplyStockModuleState();

            Dispatcher.UIThread.Post(() => HandleCommand(StartupCommand));
            if (TranslationSmokePath is not null)
                DispatcherTimer.RunOnce(async () =>
                {
                    try
                    {
                        await _window.RunTranslationSmokeAsync(ScreenshotSmokeInputPath, TranslationSmokePath);
                        RequestShutdown();
                    }
                    catch (Exception exception)
                    {
                        File.WriteAllText(TranslationSmokePath + ".error.txt", exception.Message);
                        RequestShutdown(1);
                    }
                }, TimeSpan.FromMilliseconds(500));
            else if (LayoutSmokePath is not null)
                DispatcherTimer.RunOnce(async () =>
                {
                    try { await _window.RunLayoutSmokeAsync(LayoutSmokePath); RequestShutdown(); }
                    catch (Exception exception) { File.WriteAllText(LayoutSmokePath + ".error.txt", exception.ToString()); RequestShutdown(1); }
                }, TimeSpan.FromMilliseconds(500));
            else if (ChatImageSmokePath is not null)
                DispatcherTimer.RunOnce(async () =>
                {
                    try
                    {
                        await _window.RunChatImageSmokeAsync(ScreenshotSmokeInputPath!, ChatImageSmokePath);
                        RequestShutdown();
                    }
                    catch (Exception exception)
                    {
                        File.WriteAllText(ChatImageSmokePath + ".error.txt", exception.Message);
                        RequestShutdown(1);
                    }
                }, TimeSpan.FromMilliseconds(500));
            else if (AnnotationTestInputPath is not null && AnnotationTestOutputPath is not null)
                DispatcherTimer.RunOnce(() =>
                {
                    ScreenshotTranslationImage.Create(
                        File.ReadAllBytes(AnnotationTestInputPath),
                        "{\"blocks\":[{\"x\":58,\"y\":330,\"width\":380,\"height\":80,\"source\":\"Open the terminal\",\"translation\":\"打开终端\"},{\"x\":80,\"y\":535,\"width\":250,\"height\":55,\"source\":\"sudo apt update\",\"translation\":\"sudo apt update\"}]}",
                        Guid.Empty,
                        AnnotationTestOutputPath);
                    RequestShutdown();
                }, TimeSpan.FromMilliseconds(500));
            else if (RenderTestPath is not null)
                DispatcherTimer.RunOnce(() =>
                {
                    _window.SaveRender(RenderTestPath);
                    RequestShutdown();
                }, TimeSpan.FromSeconds(1.5));
            else if (TodoSmokePath is not null)
                DispatcherTimer.RunOnce(async () =>
                {
                    try
                    {
                        await Todo.TodoSmoke.RunAsync(TodoSmokePath);
                        RequestShutdown();
                    }
                    catch (Exception exception)
                    {
                        File.WriteAllText(TodoSmokePath + ".error.txt", exception.ToString());
                        RequestShutdown(1);
                    }
                }, TimeSpan.FromMilliseconds(300));
            else if (StockSmokePath is not null)
                DispatcherTimer.RunOnce(async () =>
                {
                    try
                    {
                        await Stock.StockSmoke.RunAsync(StockSmokePath, StockLive);
                        RequestShutdown();
                    }
                    catch (Exception exception)
                    {
                        File.WriteAllText(StockSmokePath + ".error.txt", exception.ToString());
                        RequestShutdown(1);
                    }
                }, TimeSpan.FromMilliseconds(300));
            else if (SmokeTest)
                DispatcherTimer.RunOnce(() => RequestShutdown(), TimeSpan.FromSeconds(1.5));
            else if (DiagnosePath is not null)
                DispatcherTimer.RunOnce(() =>
                {
                    WriteDiagnostics();
                    RequestShutdown();
                }, TimeSpan.FromMilliseconds(700));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Starts or stops the daily todo module to match the persisted switch. Like
    /// the Windows host, an enabled module is running as soon as the suite starts.
    /// </summary>
    private void ApplyTodoModuleState()
    {
        if (!OperatingSystem.IsLinux() || ManagedMode || SmokeTest) return;
        if (_suiteSettings?.Current.TodoNotesEnabled == true)
        {
            EnsureTodoModule();
            _todo?.SetEdgeHideEnabled(_suiteSettings.Current.EdgeHideTodo);
            _todo?.Start();
        }
        else
        {
            _todo?.Stop();
        }
    }

    /// <summary>
    /// Starts or stops the stock monitor to match the persisted switch, like the
    /// Windows host's StartStock/StopStock.
    /// </summary>
    private void ApplyStockModuleState()
    {
        if (!OperatingSystem.IsLinux() || ManagedMode || SmokeTest) return;
        if (_suiteSettings?.Current.StockEnabled == true)
        {
            EnsureStockModule();
            _stock?.SetEdgeHideEnabled(_suiteSettings.Current.EdgeHideStock);
            _stock?.Start();
        }
        else
        {
            _stock?.Stop();
        }
    }

    private void EnsureStockModule()
    {
        if (_stock is not null) return;
        _stock = new Stock.StockModule(_notifications, _suiteSettings?.Current.EdgeHideStock ?? false);
        if (_stock.LoadWarning is { } warning) _notifications.Show("股票观察", warning, 7000);
        if (_stock.ImportedFrom is { } imported) _notifications.Show("股票观察", "已导入原有自选股设置：" + imported, 7000);
    }

    /// <summary>Shows the stock capsule and switches the module on if it was off.</summary>
    private void ShowStock()
    {
        SetModuleEnabled(SuiteMenuBuilder.Stock, true);
        EnsureStockModule();
        _stock?.Start();
    }

    /// <summary>
    /// The stock capsule's own Windows hotkey toggles visibility (StockWindow
    /// WndProc), so Ctrl+Alt+Q hides a visible capsule instead of re-showing it.
    /// </summary>
    private void ToggleStock()
    {
        SetModuleEnabled(SuiteMenuBuilder.Stock, true);
        if (_stock is { IsRunning: true }) _stock.ToggleVisibility();
        else
        {
            EnsureStockModule();
            _stock?.Start();
        }
    }

    private void EnsureTodoModule()
    {
        if (_todo is not null) return;
        _todo = new Todo.TodoModule(_notifications, _suiteSettings?.Current.EdgeHideTodo ?? false);
        if (_todo.LoadWarning is { } warning) _notifications.Show("每日待办", warning, 7000);
        if (_todo.ImportedFrom is { } imported) _notifications.Show("每日待办", "已导入原有待办数据：" + imported, 7000);
    }

    /// <summary>Opens today's todo list and switches the module on if it was off.</summary>
    private void ShowTodo()
    {
        SetModuleEnabled(SuiteMenuBuilder.Todo, true);
        EnsureTodoModule();
        _todo?.ShowToday();
    }

    /// <summary>
    /// Single exit path. The platform integrations are disposed before the lifetime
    /// stops so X11 key grabs and the tray are released deterministically.
    /// </summary>
    private void RequestShutdown(int exitCode = 0)
    {
        _exitRequested = true;
        _todo?.Dispose();
        _todo = null;
        _stock?.Dispose();
        _stock = null;
        _hotkey?.Dispose();
        _hotkey = null;
        _tray?.Dispose();
        _tray = null;
        if (exitCode == 0) _desktop?.Shutdown();
        else _desktop?.Shutdown(exitCode);
    }

    private void HandleCommand(AppCommand command)
    {
        if (_window is null) return;
        if (command == AppCommand.Exit)
        {
            RequestShutdown();
        }
        else if (command == AppCommand.Background)
        {
            // The legacy Windows tray host starts us this way so the first window
            // appears only when Shift+Backspace is pressed.
        }
        else if (command == AppCommand.ShowTodo)
        {
            ShowTodo();
        }
        else if (command == AppCommand.ShowStock)
        {
            ShowStock();
        }
        else if (command == AppCommand.ToggleStock)
        {
            ToggleStock();
        }
        else
        {
            // Windows re-enables the assistant module whenever one of its entry
            // points is used, so a tray switch can never block a hotkey.
            SetModuleEnabled(SuiteMenuBuilder.Assistant, true);
            if (command == AppCommand.Screenshot)
                _window.ShowForScreenshot();
            else if (command == AppCommand.ShowChat)
                _window.ShowChat();
            else if (command == AppCommand.Toggle)
                _window.ToggleTranslation();
            else
                _window.ShowTranslation();
        }
    }

    /// <summary>
    /// Builds the tray from the shared suite menu model so the Linux menu matches
    /// the Windows host: assistant entries, module switches, autostart, tool
    /// directory and exit.
    /// </summary>
    private void CreateTray()
    {
        var items = SuiteMenuBuilder.Build(
            _suiteSettings?.Current ?? new SuiteSettings(),
            _autostart?.IsEnabled ?? false,
            OperatingSystem.IsLinux());

        var menu = new NativeMenu();
        _menuItems.Clear();
        foreach (var item in items)
        {
            if (item.Kind == SuiteMenuKind.Separator)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }

            var entry = new NativeMenuItem(item.Title) { IsEnabled = item.IsEnabled };
            if (item.Kind == SuiteMenuKind.Toggle)
            {
                entry.ToggleType = MenuItemToggleType.CheckBox;
                entry.IsChecked = item.IsChecked;
            }
            var id = item.Id;
            var toggle = item.Kind == SuiteMenuKind.Toggle;
            entry.Click += (_, _) =>
            {
                // Avalonia's Linux tray exports the menu over DBus and raises Click
                // without applying the checkmark: DBusMenuExporter.HandleEvent only
                // calls RaiseClicked(), so NativeMenuItem.IsChecked keeps the value
                // the app last wrote. Reading it here would therefore always return
                // the previous switch state and a module could never be turned off,
                // so the switch is flipped before the command is dispatched. The
                // exporter answers with a layout update, which is what moves the
                // checkmark in the panel.
                if (toggle) entry.IsChecked = !entry.IsChecked;
                HandleMenu(id);
            };
            menu.Items.Add(entry);
            _menuItems[id] = entry;
        }

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LittleTools.Assistant/Assets/little-tools.ico"))),
            ToolTipText = "Little Tools",
            Menu = menu,
            IsVisible = true
        };
        _tray.Clicked += (_, _) => HandleCommand(AppCommand.Toggle);
    }

    private void HandleMenu(string id)
    {
        switch (id)
        {
            case SuiteMenuBuilder.Translate: HandleCommand(AppCommand.ShowTranslation); break;
            case SuiteMenuBuilder.Chat: HandleCommand(AppCommand.ShowChat); break;
            case SuiteMenuBuilder.Screenshot: HandleCommand(AppCommand.Screenshot); break;
            case SuiteMenuBuilder.Assistant:
                SetModuleEnabled(id, IsMenuChecked(id));
                break;
            case SuiteMenuBuilder.Monitor:
            case SuiteMenuBuilder.Todo:
            case SuiteMenuBuilder.Stock:
                SetModuleEnabled(id, IsMenuChecked(id));
                if (id == SuiteMenuBuilder.Todo) ApplyTodoModuleState();
                if (id == SuiteMenuBuilder.Stock) ApplyStockModuleState();
                break;
            case SuiteMenuBuilder.Autostart: ToggleAutostart(); break;
            case SuiteMenuBuilder.OpenDirectory: OpenToolDirectory(); break;
            case SuiteMenuBuilder.Exit: HandleCommand(AppCommand.Exit); break;
        }
    }

    private bool IsMenuChecked(string id) => _menuItems.TryGetValue(id, out var item) && item.IsChecked;

    private void SetModuleEnabled(string id, bool enabled)
    {
        _suiteSettings?.Set(settings =>
        {
            switch (id)
            {
                case SuiteMenuBuilder.Monitor:
                    if (settings.MonitorEnabled == enabled) return false;
                    settings.MonitorEnabled = enabled;
                    return true;
                case SuiteMenuBuilder.Assistant:
                    if (settings.TranslateEnabled == enabled) return false;
                    settings.TranslateEnabled = enabled;
                    return true;
                case SuiteMenuBuilder.Todo:
                    if (settings.TodoNotesEnabled == enabled) return false;
                    settings.TodoNotesEnabled = enabled;
                    return true;
                case SuiteMenuBuilder.Stock:
                    if (settings.StockEnabled == enabled) return false;
                    settings.StockEnabled = enabled;
                    return true;
                default:
                    return false;
            }
        });

        // Keep the checkmark in step when the module was enabled by a hotkey
        // instead of by clicking the menu entry.
        if (_menuItems.TryGetValue(id, out var item)) item.IsChecked = enabled;
        if (id == SuiteMenuBuilder.Assistant && !enabled) _window?.Hide();
    }

    /// <summary>Toggles the XDG autostart entry and mirrors the Windows balloon tip.</summary>
    private void ToggleAutostart()
    {
        if (_autostart is null) return;
        var target = !_autostart.IsEnabled;
        var ok = _autostart.Set(target);
        if (_menuItems.TryGetValue(SuiteMenuBuilder.Autostart, out var item)) item.IsChecked = _autostart.IsEnabled;
        _notifications.Show("Little Tools", !ok
            ? "无法写入开机自启配置。"
            : target ? "已开启开机自启，登录后将延迟 30 秒启动。" : "已关闭开机自启。");
    }

    private void OpenToolDirectory()
    {
        if (!ToolDirectoryLauncher.TryOpen())
            _notifications.Show("Little Tools", "无法打开工具目录。");
    }

    /// <summary>
    /// Tells the user when the desktop did not grant every global shortcut. The
    /// notice is only repeated when the set of conflicts changes, so a login is not
    /// greeted by the same message every day.
    /// </summary>
    private void NotifyHotkeyFallback()
    {
        var session = SessionEnvironment.Current;
        var linux = _hotkey as LinuxGlobalHotkeyService;
        var failed = linux?.FailedChords ?? [];
        var registered = linux?.IsRegistered ?? false;
        var message = HotkeyNotice.Build(session, AutostartService.ResolveLaunchCommand(), failed, registered);
        if (message is null) return;

        var signature = HotkeyNotice.Signature(session, failed);
        var marker = Path.Combine(AppPaths.CacheDirectory, "hotkey-conflicts.txt");
        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == signature) return;
            File.WriteAllText(marker, signature);
        }
        catch
        {
            // A cache write failure only means the notice may repeat.
        }
        _notifications.Show("Little Tools", message, 9000);
    }

    /// <summary>
    /// Writes a machine readable snapshot of the platform integration. Used by the
    /// Linux smoke tests and by users debugging a desktop that degrades silently.
    /// </summary>
    private void WriteDiagnostics()
    {
        if (DiagnosePath is null) return;
        try
        {
            var session = SessionEnvironment.Current;
            var hotkey = _hotkey as LinuxGlobalHotkeyService;
            var payload = new
            {
                platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
                session = SessionEnvironment.Describe(session),
                sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                display = Environment.GetEnvironmentVariable("DISPLAY"),
                waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
                trayCreated = _tray is not null,
                trayMenuItems = _menuItems.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                hotkeysRegistered = _hotkey?.IsRegistered ?? false,
                hotkeyFailure = hotkey?.FailureReason,
                hotkeyChords = hotkey?.RegisteredChords.ToArray(),
                hotkeyConflicts = hotkey?.FailedChords.ToArray(),
                todoRunning = _todo?.IsRunning ?? false,
                todoDataPath = _todo?.DataPath,
                todoImportedFrom = _todo?.ImportedFrom,
                stockRunning = _stock?.IsRunning ?? false,
                stockFixtureReplay = Stock.StockDataServiceFactory.IsFixtureReplayEnabled,
                stockVisible = _stock?.IsVisible ?? false,
                stockDataPath = _stock?.DataPath,
                stockImportedFrom = _stock?.ImportedFrom,
                autostartEnabled = _autostart?.IsEnabled ?? false,
                autostartEntry = _autostart?.EntryPath,
                configDirectory = AppPaths.ConfigDirectory,
                dataDirectory = AppPaths.DataDirectory,
                cacheDirectory = AppPaths.CacheDirectory,
                managerSettings = AppPaths.ManagerSettingsPath,
                modules = new Dictionary<string, bool>
                {
                    [SuiteMenuBuilder.Monitor] = SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Monitor, OperatingSystem.IsLinux()),
                    [SuiteMenuBuilder.Assistant] = true,
                    [SuiteMenuBuilder.Todo] = SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Todo, OperatingSystem.IsLinux()),
                    [SuiteMenuBuilder.Stock] = SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Stock, OperatingSystem.IsLinux())
                },
                tools = new[] { "notify-send", "xdg-open", "secret-tool", "gnome-screenshot", "spectacle", "grim", "slurp" }
                    .ToDictionary(name => name, name => ExecutableLocator.Find(name) is not null)
            };
            File.WriteAllText(DiagnosePath, System.Text.Json.JsonSerializer.Serialize(
                payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Diagnostics must never take the app down.
        }
    }

}
