using Avalonia;

namespace LittleTools.Assistant;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var renderIndex = Array.FindIndex(args, value => string.Equals(value, "--render-test", StringComparison.OrdinalIgnoreCase));
        App.RenderTestPath = renderIndex >= 0 && renderIndex + 1 < args.Length ? Path.GetFullPath(args[renderIndex + 1]) : null;
        var annotationIndex = Array.FindIndex(args, value => string.Equals(value, "--annotation-test", StringComparison.OrdinalIgnoreCase));
        App.AnnotationTestInputPath = annotationIndex >= 0 && annotationIndex + 1 < args.Length ? Path.GetFullPath(args[annotationIndex + 1]) : null;
        App.AnnotationTestOutputPath = annotationIndex >= 0 && annotationIndex + 2 < args.Length ? Path.GetFullPath(args[annotationIndex + 2]) : null;
        App.SmokeTest = args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase)
            || App.RenderTestPath is not null
            || App.AnnotationTestInputPath is not null;
        App.ManagedMode = args.Contains("--managed", StringComparer.OrdinalIgnoreCase);
        using var instance = new SingleInstanceCoordinator();
        var command = args.Contains("--exit", StringComparer.OrdinalIgnoreCase)
            ? AppCommand.Exit
            : App.AnnotationTestInputPath is not null
                ? AppCommand.Background
            : args.Contains("--background", StringComparer.OrdinalIgnoreCase)
                ? AppCommand.Background
                : args.Contains("--screenshot", StringComparer.OrdinalIgnoreCase)
                    ? AppCommand.Screenshot
                    : AppCommand.ShowNew;

        if (!instance.IsPrimary && !App.SmokeTest)
        {
            return instance.SendAsync(command).GetAwaiter().GetResult() ? 0 : 2;
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
