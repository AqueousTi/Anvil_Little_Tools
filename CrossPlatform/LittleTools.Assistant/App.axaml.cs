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
    internal static AppCommand StartupCommand { get; set; } = AppCommand.ShowNew;
    internal static bool SmokeTest { get; set; }
    internal static bool ManagedMode { get; set; }
    internal static string? RenderTestPath { get; set; }

    private MainWindow? _window;
    private IGlobalHotkeyService? _hotkey;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _exitRequested;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) =>
            {
                _hotkey?.Dispose();
                _tray?.Dispose();
            };
            var settings = new SettingsStore();
            var history = new ConversationStore();
            _window = new MainWindow(settings, history, ScreenshotServiceFactory.Create());
            desktop.MainWindow = _window;
            _window.Closing += (_, eventArgs) =>
            {
                if (_exitRequested) return;
                eventArgs.Cancel = true;
                _window.Hide();
            };

            Coordinator?.StartListening(command => Dispatcher.UIThread.Post(() => HandleCommand(command)));
            _hotkey = GlobalHotkeyServiceFactory.Create();
            _hotkey.Start(() => Dispatcher.UIThread.Post(() => HandleCommand(AppCommand.ShowNew)));
            if (!ManagedMode)
                try { CreateTray(desktop); } catch { }

            Dispatcher.UIThread.Post(() => HandleCommand(StartupCommand));
            if (RenderTestPath is not null)
                DispatcherTimer.RunOnce(() =>
                {
                    _window.SaveRender(RenderTestPath);
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(1.5));
            else if (SmokeTest)
                DispatcherTimer.RunOnce(() => desktop.Shutdown(), TimeSpan.FromSeconds(1.5));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void HandleCommand(AppCommand command)
    {
        if (_window is null) return;
        if (command == AppCommand.Exit)
        {
            _exitRequested = true;
            _hotkey?.Dispose();
            _tray?.Dispose();
            _desktop?.Shutdown();
        }
        else if (command == AppCommand.Background)
        {
            // The legacy Windows tray host starts us this way so the first window
            // appears only when Shift+Backspace is pressed.
        }
        else if (command == AppCommand.Screenshot)
            _window.ShowForScreenshot();
        else
            _window.ShowNewConversation();
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        var show = new NativeMenuItem("新会话");
        show.Click += (_, _) => HandleCommand(AppCommand.ShowNew);
        var screenshot = new NativeMenuItem("截图翻译");
        screenshot.Click += (_, _) => HandleCommand(AppCommand.Screenshot);
        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) =>
        {
            _exitRequested = true;
            _hotkey?.Dispose();
            _tray?.Dispose();
            desktop.Shutdown();
        };
        menu.Items.Add(show);
        menu.Items.Add(screenshot);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LittleTools.Assistant/Assets/little-tools.ico"))),
            ToolTipText = "Little Tools AI Assistant",
            Menu = menu,
            IsVisible = true
        };
        _tray.Clicked += (_, _) => HandleCommand(AppCommand.ShowNew);
    }

}
