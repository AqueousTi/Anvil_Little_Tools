using System.Diagnostics;

namespace LittleTools.Assistant;

internal static class SuiteLauncher
{
    internal static string? FindManager(string directory)
    {
        // Installed suite, portable package, published build and IDE output all
        // share an ancestor with LittleTools/bin. Never search another install.
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
        {
            var candidate = Path.Combine(parent.FullName, "LittleTools", "bin", "LittleTools.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static string ArgumentFor(AppCommand command) => command switch
    {
        AppCommand.ShowChat => "--chat",
        AppCommand.Screenshot => "--screenshot",
        AppCommand.ShowTodo => "--todo",
        AppCommand.ShowStock => "--stock",
        AppCommand.ToggleStock => "--stock",
        AppCommand.Toggle => "--toggle",
        AppCommand.Background => "--background",
        _ => "--translate"
    };

    internal static bool TryLaunch(AppCommand command)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var manager = FindManager(AppContext.BaseDirectory);
        if (manager is null) return false;
        using var process = Process.Start(new ProcessStartInfo(manager, ArgumentFor(command))
        {
            WorkingDirectory = Path.GetDirectoryName(manager),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        return process is not null;
    }
}
