using Avalonia;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var renderIndex = Array.FindIndex(args, value => string.Equals(value, "--render-test", StringComparison.OrdinalIgnoreCase));
        App.RenderTestPath = renderIndex >= 0 && renderIndex + 1 < args.Length ? Path.GetFullPath(args[renderIndex + 1]) : null;
        var layoutIndex = Array.FindIndex(args, value => value == "--layout-smoke");
        App.LayoutSmokePath = layoutIndex >= 0 && layoutIndex + 1 < args.Length ? Path.GetFullPath(args[layoutIndex + 1]) : null;
        var chatTestIndex = Array.FindIndex(args, value => value == "--chat-image-smoke");
        App.ChatImageSmokePath = chatTestIndex >= 0 && chatTestIndex + 1 < args.Length ? Path.GetFullPath(args[chatTestIndex + 1]) : null;
        var translationTestIndex = Array.FindIndex(args, value => string.Equals(value, "--translation-smoke", StringComparison.OrdinalIgnoreCase));
        App.TranslationSmokePath = translationTestIndex >= 0 && translationTestIndex + 1 < args.Length ? Path.GetFullPath(args[translationTestIndex + 1]) : null;
        var screenshotInputIndex = Array.FindIndex(args, value => string.Equals(value, "--screenshot-input", StringComparison.OrdinalIgnoreCase));
        App.ScreenshotSmokeInputPath = screenshotInputIndex >= 0 && screenshotInputIndex + 1 < args.Length ? Path.GetFullPath(args[screenshotInputIndex + 1]) : null;
        var annotationIndex = Array.FindIndex(args, value => string.Equals(value, "--annotation-test", StringComparison.OrdinalIgnoreCase));
        App.AnnotationTestInputPath = annotationIndex >= 0 && annotationIndex + 1 < args.Length ? Path.GetFullPath(args[annotationIndex + 1]) : null;
        App.AnnotationTestOutputPath = annotationIndex >= 0 && annotationIndex + 2 < args.Length ? Path.GetFullPath(args[annotationIndex + 2]) : null;
        var diagnoseIndex = Array.FindIndex(args, value => string.Equals(value, "--diagnose", StringComparison.OrdinalIgnoreCase));
        App.DiagnosePath = diagnoseIndex >= 0 && diagnoseIndex + 1 < args.Length ? Path.GetFullPath(args[diagnoseIndex + 1]) : null;
        var todoSmokeIndex = Array.FindIndex(args, value => string.Equals(value, "--todo-smoke", StringComparison.OrdinalIgnoreCase));
        App.TodoSmokePath = todoSmokeIndex >= 0 && todoSmokeIndex + 1 < args.Length ? Path.GetFullPath(args[todoSmokeIndex + 1]) : null;
        App.SmokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase)
            || App.TodoSmokePath is not null
            || App.RenderTestPath is not null
            || App.LayoutSmokePath is not null
            || App.ChatImageSmokePath is not null
            || App.TranslationSmokePath is not null
            || App.AnnotationTestInputPath is not null;
        App.ManagedMode = args.Contains("--managed", StringComparer.OrdinalIgnoreCase);
        var command = args.Contains("--exit", StringComparer.OrdinalIgnoreCase)
            ? AppCommand.Exit
            : App.AnnotationTestInputPath is not null
                ? AppCommand.Background
            : args.Contains("--background", StringComparer.OrdinalIgnoreCase)
                ? AppCommand.Background
                : args.Contains("--screenshot", StringComparer.OrdinalIgnoreCase)
                    ? AppCommand.Screenshot
                    : args.Contains("--chat", StringComparer.OrdinalIgnoreCase)
                        ? AppCommand.ShowChat
                        : args.Contains("--todo", StringComparer.OrdinalIgnoreCase)
                            ? AppCommand.ShowTodo
                        : args.Contains("--toggle", StringComparer.OrdinalIgnoreCase)
                            ? AppCommand.Toggle
                            : App.DiagnosePath is not null
                                ? AppCommand.Background
                                : AppCommand.ShowTranslation;

        var autostartRequest = Array.Find(args, value =>
            value.Equals("--autostart-enable", StringComparison.OrdinalIgnoreCase)
            || value.Equals("--autostart-disable", StringComparison.OrdinalIgnoreCase)
            || value.Equals("--autostart-status", StringComparison.OrdinalIgnoreCase));
        if (autostartRequest is not null) return RunAutostartCommand(autostartRequest);

        if (!App.SmokeTest && !App.ManagedMode && command != AppCommand.Exit && SuiteLauncher.TryLaunch(command))
            return 0;

        using var instance = new SingleInstanceCoordinator(App.SmokeTest);
        var isPrimary = instance.IsPrimary && !(!App.SmokeTest && instance.HasLivePeer());
        if (isPrimary && command == AppCommand.Exit) return 0;
        if (!isPrimary && !App.SmokeTest)
        {
            return instance.SendAsync(command, App.ManagedMode).GetAwaiter().GetResult() ? 0 : 2;
        }

        App.Coordinator = instance;
        App.StartupCommand = command;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Headless autostart control, used by the install script and by users who
    /// prefer the terminal. It never opens a window and needs no display.
    /// </summary>
    private static int RunAutostartCommand(string request)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("开机自启命令仅在 Linux 上可用。");
            return 3;
        }

        var service = new AutostartService();
        if (request.Equals("--autostart-status", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(service.IsEnabled ? "enabled" : "disabled");
            Console.WriteLine(service.EntryPath);
            return service.IsEnabled ? 0 : 1;
        }

        var enable = request.Equals("--autostart-enable", StringComparison.OrdinalIgnoreCase);
        if (!service.Set(enable))
        {
            Console.Error.WriteLine("无法写入 " + service.EntryPath);
            return 2;
        }

        Console.WriteLine(enable ? "enabled" : "disabled");
        Console.WriteLine(service.EntryPath);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
