using System.Text.Json;

namespace LittleTools.Assistant.Services;

internal sealed class BaiduCredentials(string appId, string secretKey)
{
    public string AppId { get; } = appId;
    public string SecretKey { get; } = secretKey;

    internal static BaiduCredentials? ReadLegacy(string directory)
    {
        foreach (var path in LegacyPaths(directory))
        {
            var credentials = ReadFile(path);
            if (credentials is not null) return credentials;
        }
        return null;
    }

    /// <summary>
    /// The APPID of a legacy file without needing its secret. The settings dialog
    /// must be able to show a stored APPID even when the keyring is unreachable,
    /// otherwise the user is told "not configured" although the value is on disk.
    /// </summary>
    internal static string? ReadLegacyAppId(string directory)
    {
        foreach (var path in LegacyPaths(directory))
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var appId = document.RootElement.TryGetProperty("AppId", out var value) ? value.GetString() : null;
                if (!string.IsNullOrWhiteSpace(appId)) return appId.Trim();
            }
            catch
            {
                // Keep looking: a malformed file must not hide a later candidate.
            }
        }
        return null;
    }

    private static IEnumerable<string> LegacyPaths(string directory)
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("TRANSLATE_APP_CONFIG"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "TranslateApp", "appsettings.json"),
            // Where reinstall-linux.sh parks a legacy credential before it replaces
            // the program directory, so an update cannot delete it.
            Path.Combine(AppPaths.ConfigDirectory, "translate", "appsettings.json"),
            Path.Combine(directory, "appsettings.json")
        };
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
        {
            candidates.Add(Path.Combine(parent.FullName, "TranslateApp", "appsettings.json"));
            candidates.Add(Path.Combine(parent.FullName, "TranslateApp", "bin", "appsettings.json"));
        }
        return candidates.OfType<string>().Where(path => !string.IsNullOrWhiteSpace(path)).Distinct();
    }

    internal static BaiduCredentials? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var appId = root.GetProperty("AppId").GetString();
            var secret = root.GetProperty("SecretKey").GetString();
            return string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret)
                ? null : new BaiduCredentials(appId.Trim(), secret.Trim());
        }
        catch { return null; }
    }
}
