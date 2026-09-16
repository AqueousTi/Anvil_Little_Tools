using System.Text.Json;

namespace LittleTools.Assistant.Services;

internal sealed class BaiduCredentials(string appId, string secretKey)
{
    public string AppId { get; } = appId;
    public string SecretKey { get; } = secretKey;

    internal static BaiduCredentials? ReadLegacy(string directory)
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("TRANSLATE_APP_CONFIG"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "TranslateApp", "appsettings.json"),
            Path.Combine(directory, "appsettings.json")
        };
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
        {
            candidates.Add(Path.Combine(parent.FullName, "TranslateApp", "appsettings.json"));
            candidates.Add(Path.Combine(parent.FullName, "TranslateApp", "bin", "appsettings.json"));
        }
        foreach (var path in candidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
        {
            var credentials = ReadFile(path!);
            if (credentials is not null) return credentials;
        }
        return null;
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
