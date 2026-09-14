namespace LittleTools.Assistant.Services;

internal static class AppPaths
{
    public static string ConfigDirectory => Ensure(GetConfigDirectory());
    public static string DataDirectory => Ensure(GetDataDirectory());
    public static string CacheDirectory => Ensure(GetCacheDirectory());

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
