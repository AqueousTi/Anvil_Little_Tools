using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace LittleTools.Assistant.Services;

internal sealed class SettingsStore
{
    private readonly string _path = Path.Combine(AppPaths.ConfigDirectory, "assistant-settings.json");
    private AppSettings _settings;

    public SettingsStore() => _settings = AtomicJson.Read<AppSettings>(_path) ?? new AppSettings();

    public AppSettings Current => _settings;

    public void Save(AppSettings settings)
    {
        _settings = settings;
        AtomicJson.Write(_path, settings);
    }

    public string? ResolveKey(ProviderKind provider)
    {
        var configuredName = provider == ProviderKind.Glm
            ? _settings.GlmEnvironmentVariable
            : _settings.DeepSeekEnvironmentVariable;
        var aliases = provider == ProviderKind.Glm
            ? new[] { configuredName, "ZHIPUAI_API_KEY", "ZHIPU_API_KEY", "GLM_API_KEY", "BIGMODEL_API_KEY" }
            : new[] { configuredName, "DEEPSEEK_API_KEY" };
        foreach (var alias in aliases.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var value = Environment.GetEnvironmentVariable(alias);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        var protectedValue = provider == ProviderKind.Glm ? _settings.GlmProtectedKey : _settings.DeepSeekProtectedKey;
        var unprotected = Unprotect(protectedValue);
        if (!string.IsNullOrWhiteSpace(unprotected)) return unprotected;

        var keyringValue = ReadLinuxKeyring(provider);
        if (!string.IsNullOrWhiteSpace(keyringValue)) return keyringValue;

        return TryReadLegacyKey(provider);
    }

    public bool CanSaveKeys => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && FindExecutable("secret-tool") is not null);

    public void SaveKey(ProviderKind provider, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (OperatingSystem.IsWindows())
        {
            var encrypted = Protect(key.Trim());
            if (provider == ProviderKind.Glm) _settings.GlmProtectedKey = encrypted;
            else _settings.DeepSeekProtectedKey = encrypted;
            Save(_settings);
            return;
        }
        if (OperatingSystem.IsLinux() && FindExecutable("secret-tool") is not null)
        {
            WriteLinuxKeyring(provider, key.Trim());
            return;
        }
        throw new PlatformNotSupportedException("请安装 libsecret-tools，或使用 ZHIPUAI_API_KEY / DEEPSEEK_API_KEY 环境变量。");
    }

    private static string? Protect(string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string? value)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadLegacyKey(ProviderKind provider)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = Path.Combine(local, "LittleTools", "AIUsageMonitor", "providers.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var sourceName = provider == ProviderKind.Glm ? "GlmSource" : "DeepSeekSource";
            var environmentName = provider == ProviderKind.Glm ? "GlmEnvironment" : "DeepSeekEnvironment";
            var protectedName = provider == ProviderKind.Glm ? "GlmProtectedKey" : "DeepSeekProtectedKey";
            var source = root.TryGetProperty(sourceName, out var sourceValue) ? sourceValue.GetString() : null;
            if (string.Equals(source, "Manual", StringComparison.OrdinalIgnoreCase))
                return root.TryGetProperty(protectedName, out var protectedValue) ? Unprotect(protectedValue.GetString()) : null;
            if (root.TryGetProperty(environmentName, out var environmentValue))
            {
                var environment = environmentValue.GetString();
                var value = string.IsNullOrWhiteSpace(environment) ? null : Environment.GetEnvironmentVariable(environment);
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        catch { }
        return null;
    }

    private static string? ReadLinuxKeyring(ProviderKind provider)
    {
        if (!OperatingSystem.IsLinux() || FindExecutable("secret-tool") is null) return null;
        try
        {
            using var process = StartSecretTool(["lookup", "service", "little-tools-assistant", "provider", ProviderName(provider)], false);
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill(true);
                return null;
            }
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLinuxKeyring(ProviderKind provider, string key)
    {
        using var process = StartSecretTool(
            ["store", "--label=Little Tools Assistant", "service", "little-tools-assistant", "provider", ProviderName(provider)],
            true);
        process.StandardInput.Write(key);
        process.StandardInput.Close();
        if (!process.WaitForExit(5000) || process.ExitCode != 0)
            throw new InvalidOperationException("无法写入 Linux 系统密钥环，请确认 GNOME Keyring 已解锁。");
    }

    private static Process StartSecretTool(IReadOnlyList<string> arguments, bool redirectInput)
    {
        var start = new ProcessStartInfo("secret-tool")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        process.Start();
        return process;
    }

    private static string ProviderName(ProviderKind provider) => provider == ProviderKind.Glm ? "glm" : "deepseek";

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator)
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
    }
}
