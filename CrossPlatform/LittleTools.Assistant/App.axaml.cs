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
    internal static string? TranslationSmokePath { get; set; }
    internal static string? ScreenshotSmokeInputPath { get; set; }
    internal static string? AnnotationTestInputPath { get; set; }
    internal static string? AnnotationTestOutputPath { get; set; }

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
                _hotkey = GlobalHotkeyServiceFactory.Create();
                _hotkey.Start(command => Dispatcher.UIThread.Post(() => HandleCommand(command)));
            }
            if (!ManagedMode && !SmokeTest)
                try { CreateTray(desktop); } catch { }

            Dispatcher.UIThread.Post(() => HandleCommand(StartupCommand));
            if (TranslationSmokePath is not null)
                DispatcherTimer.RunOnce(async () =>
                {
                    try
                    {
                        await _window.RunTranslationSmokeAsync(ScreenshotSmokeInputPath, TranslationSmokePath);
                        desktop.Shutdown();
                    }
                    catch (Exception exception)
                    {
                        File.WriteAllText(TranslationSmokePath + ".error.txt", exception.Message);
                        desktop.Shutdown(1);
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
                    desktop.Shutdown();
                }, TimeSpan.FromMilliseconds(500));
            else if (RenderTestPath is not null)
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
        else if (command == AppCommand.ShowChat)
            _window.ShowChat();
        else
            _window.ShowTranslation();
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        var translate = new NativeMenuItem("翻译");
        translate.Click += (_, _) => HandleCommand(AppCommand.ShowTranslation);
        var chat = new NativeMenuItem("快问");
        chat.Click += (_, _) => HandleCommand(AppCommand.ShowChat);
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
        menu.Items.Add(translate);
        menu.Items.Add(chat);
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
        _tray.Clicked += (_, _) => HandleCommand(AppCommand.ShowTranslation);
    }

}
