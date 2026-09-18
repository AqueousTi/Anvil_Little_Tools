using System.Diagnostics;

namespace LittleTools.Assistant.Services;

/// <summary>
/// XDG autostart integration (<c>${XDG_CONFIG_HOME:-~/.config}/autostart</c>).
/// This is the Linux counterpart of the Windows scheduled task / Run value, and
/// mirrors its 30 second deferred launch so a Linux login behaves like Windows.
/// </summary>
internal sealed class AutostartService
{
    public const int DeferredStartSeconds = 30;
    private const string FileName = "little-tools.desktop";

    private readonly string _directory;

    public AutostartService(string? directory = null) => _directory = directory ?? AppPaths.AutostartDirectory;

    public string EntryPath => System.IO.Path.Combine(_directory, FileName);

    public bool IsEnabled => File.Exists(EntryPath);

    /// <summary>Writes the autostart entry. Returns false when it could not be written.</summary>
    public bool Enable(string? executable = null, string? iconPath = null, int delaySeconds = DeferredStartSeconds)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var file = BuildEntry(executable ?? ResolveLaunchCommand(), iconPath ?? AppPaths.IconPath, delaySeconds);
            var temporary = EntryPath + ".tmp";
            File.WriteAllText(temporary, file);
            File.Move(temporary, EntryPath, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            if (File.Exists(EntryPath)) File.Delete(EntryPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Set(bool enabled, string? executable = null, string? iconPath = null, int delaySeconds = DeferredStartSeconds)
        => enabled ? Enable(executable, iconPath, delaySeconds) : Disable();

    /// <summary>Builds the .desktop content. Pure so the escaping can be unit tested.</summary>
    internal static string BuildEntry(string command, string iconPath, int delaySeconds)
    {
        var exec = BuildExec(command, delaySeconds);
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("[Desktop Entry]");
        builder.AppendLine("Type=Application");
        builder.AppendLine("Version=1.0");
        builder.AppendLine("Name=Little Tools");
        builder.AppendLine("Comment=AI translation, Q&A and desktop widgets");
        builder.Append("Exec=").AppendLine(exec);
        if (!string.IsNullOrWhiteSpace(iconPath)) builder.Append("Icon=").AppendLine(iconPath);
        builder.AppendLine("Terminal=false");
        builder.AppendLine("StartupNotify=false");
        builder.AppendLine("X-GNOME-Autostart-enabled=true");
        builder.AppendLine("NoDisplay=false");
        return builder.ToString();
    }

    /// <summary>
    /// Wraps the launch in <c>sh -c</c> so the start can be deferred on any desktop.
    /// If the command cannot be quoted safely the delay moves to the GNOME specific
    /// key, which other desktops ignore instead of failing.
    /// </summary>
    internal static string BuildExec(string command, int delaySeconds)
    {
        if (!CanQuoteForShell(command))
        {
            var plain = QuoteExecArgument(command);
            return delaySeconds > 0
                ? $"{plain}\nX-GNOME-Autostart-Delay={delaySeconds}"
                : plain;
        }

        var inner = "sleep " + Math.Max(0, delaySeconds) + "; exec '" + command + "' --background";
        return delaySeconds > 0
            ? "/bin/sh -c " + QuoteExecArgument(inner)
            : QuoteExecArgument(command) + " --background";
    }

    private static bool CanQuoteForShell(string command)
        => !string.IsNullOrWhiteSpace(command)
           && command.IndexOfAny(['"', '\'', '\n', '\r', '\\', '`', '$']) < 0;

    /// <summary>Quotes one Exec argument per the freedesktop desktop entry spec.</summary>
    internal static string QuoteExecArgument(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '`', '*', '?', '#', '(', ')']) < 0)
            return value;
        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("`", "\\`")
            .Replace("$", "\\$");
        return "\"" + escaped + "\"";
    }

    /// <summary>The command that should be run at login for the running build.</summary>
    internal static string ResolveLaunchCommand(string? processPath = null, string? assemblyPath = null)
    {
        var executable = processPath ?? Environment.ProcessPath;
        var assembly = assemblyPath ?? (Environment.GetCommandLineArgs().FirstOrDefault());
        if (string.IsNullOrWhiteSpace(executable)) return string.Empty;

        var name = Path.GetFileNameWithoutExtension(executable);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(assembly))
            return QuoteExecArgument(executable) + " " + QuoteExecArgument(Path.GetFullPath(assembly));
        return executable;
    }
}

/// <summary>Opens the installed tool directory in the desktop file manager.</summary>
internal static class ToolDirectoryLauncher
{
    public static bool TryOpen(string? directory = null)
    {
        var target = directory ?? AppContext.BaseDirectory;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
                return true;
            }

            var opener = ExecutableLocator.Find("xdg-open");
            if (opener is null) return false;
            Process.Start(new ProcessStartInfo(opener, target) { UseShellExecute = false, CreateNoWindow = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class ExecutableLocator
{
    public static string? Find(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
