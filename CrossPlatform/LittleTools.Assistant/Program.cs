using Avalonia;

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
        App.SmokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase)
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
                        : AppCommand.ShowTranslation;

        if (!App.SmokeTest && !App.ManagedMode && command != AppCommand.Exit && SuiteLauncher.TryLaunch(command))
            return 0;

        using var instance = new SingleInstanceCoordinator(App.SmokeTest);
        if (instance.IsPrimary && command == AppCommand.Exit) return 0;
        if (!instance.IsPrimary && !App.SmokeTest)
        {
            return instance.SendAsync(command, App.ManagedMode).GetAwaiter().GetResult() ? 0 : 2;
        }

        App.Coordinator = instance;
        App.StartupCommand = command;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
