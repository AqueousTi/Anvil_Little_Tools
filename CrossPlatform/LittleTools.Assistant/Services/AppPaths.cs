namespace LittleTools.Assistant.Services;

internal static class AppPaths
{
    public static string ConfigDirectory => Ensure(GetConfigDirectory());
    public static string DataDirectory => Ensure(GetDataDirectory());
    public static string CacheDirectory => Ensure(GetCacheDirectory());

    /// <summary>
    /// The suite module switches. On Windows this is the file the WPF tray host
    /// already owns, so both platforms read and write one definition.
    /// </summary>
    public static string ManagerSettingsPath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "manager.json")
        : Path.Combine(GetConfigDirectory(), "manager.json");

    /// <summary>XDG autostart directory; empty on platforms without one.</summary>
    public static string AutostartDirectory => OperatingSystem.IsWindows()
        ? string.Empty
        : Path.Combine(ReadXdg("XDG_CONFIG_HOME", ".config"), "autostart");

    /// <summary>XDG application entry directory used by install scripts.</summary>
    public static string ApplicationsDirectory => OperatingSystem.IsWindows()
        ? string.Empty
        : Path.Combine(ReadXdg("XDG_DATA_HOME", Path.Combine(".local", "share")), "applications");

    /// <summary>Icon shipped next to the executable, used by .desktop entries.</summary>
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "little-tools.png");

    /// <summary>Daily todo data, alongside the other suite data on Linux.</summary>
    public static string TodoDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "DailyTodo")
        : Ensure(Path.Combine(GetDataDirectory(), "todo"));

    /// <summary>
    /// Stock watch settings. Windows keeps them in
    /// <c>%LocalAppData%\LittleTools\StockMonitor</c>; on Linux they live next to
    /// the other suite data, and <see cref="Stock.StockStore"/> adopts the Windows
    /// file when one is present.
    /// </summary>
    public static string StockDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "StockMonitor")
        : Ensure(Path.Combine(GetDataDirectory(), "stock"));

    private static string GetConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "Assistant");
        return Path.Combine(ReadXdg("XDG_CONFIG_HOME", ".config"), "little-tools");
    }

    private static string GetDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "Assistant");
        return Path.Combine(ReadXdg("XDG_DATA_HOME", Path.Combine(".local", "share")), "little-tools");
    }

    private static string GetCacheDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "Assistant", "cache");
        return Path.Combine(ReadXdg("XDG_CACHE_HOME", ".cache"), "little-tools");
    }

    private static string ReadXdg(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, fallback);
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
