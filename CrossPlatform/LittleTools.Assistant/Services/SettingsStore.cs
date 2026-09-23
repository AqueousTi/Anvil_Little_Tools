using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace LittleTools.Assistant.Services;

/// <summary>Where a resolved secret came from, for the UI and <c>--diagnose</c>.</summary>
internal enum SecretSource
{
    None,
    Environment,
    WindowsDpapi,
    LinuxKeyring,
    ConfigFile,
    LegacyConfig
}

internal readonly record struct SecretResolution(SecretSource Source, string? Value)
{
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);
}

internal readonly record struct BaiduResolution(BaiduCredentials? Credentials, SecretSource Source, string? AppId)
{
    public bool HasSecret => Credentials is not null;
}

internal sealed class SettingsStore
{
    /// <summary>Environment variables that override every stored value.</summary>
    public const string BaiduAppIdVariable = "BAIDU_TRANSLATE_APP_ID";
    public const string BaiduSecretVariable = "BAIDU_TRANSLATE_SECRET_KEY";

    private readonly string _path = Path.Combine(AppPaths.ConfigDirectory, "assistant-settings.json");
    private readonly CredentialFile _credentialFile = new();
    private AppSettings _settings;

    public SettingsStore() => _settings = AtomicJson.Read<AppSettings>(_path) ?? new AppSettings();

    public AppSettings Current => _settings;

    /// <summary>Path of the fallback secret file, shown by the settings dialog and diagnostics.</summary>
    public string CredentialsPath => _credentialFile.Path;

    /// <summary>True when the GNOME keyring can be used from this process.</summary>
    public bool KeyringAvailable => OperatingSystem.IsLinux() && FindExecutable("secret-tool") is not null;

    /// <summary>
    /// The stored APPID on its own. Reading it never depends on the secret being
    /// reachable, so the settings dialog can always show what is saved.
    /// </summary>
    public string? StoredBaiduAppId =>
        !string.IsNullOrWhiteSpace(_settings.BaiduAppId)
            ? _settings.BaiduAppId.Trim()
            : BaiduCredentials.ReadLegacyAppId(AppContext.BaseDirectory);

    /// <summary>
    /// Resolution order: environment variables, then the platform store (DPAPI on
    /// Windows, keyring on Linux) together with the stored APPID, then the durable
    /// config file, and only last the legacy <c>appsettings.json</c> that the
    /// one-shot updater can delete.
    /// </summary>
    public BaiduResolution ResolveBaidu()
    {
        var appId = Environment.GetEnvironmentVariable(BaiduAppIdVariable);
        var secret = Environment.GetEnvironmentVariable(BaiduSecretVariable);
        if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(secret))
            return new BaiduResolution(new BaiduCredentials(appId.Trim(), secret.Trim()), SecretSource.Environment, appId.Trim());

        var storedAppId = StoredBaiduAppId;
        var stored = ResolveSecret("baidu", _settings.BaiduProtectedKey);
        if (stored.HasValue && !string.IsNullOrWhiteSpace(storedAppId))
            return new BaiduResolution(new BaiduCredentials(storedAppId, stored.Value!), stored.Source, storedAppId);

        var legacy = BaiduCredentials.ReadLegacy(AppContext.BaseDirectory);
        if (legacy is not null) return new BaiduResolution(legacy, SecretSource.LegacyConfig, legacy.AppId);
        return new BaiduResolution(null, SecretSource.None, storedAppId);
    }

    public BaiduCredentials? ResolveBaiduCredentials() => ResolveBaidu().Credentials;

    public void SaveBaiduCredentials(string? appId, string? secret)
    {
        if (string.IsNullOrWhiteSpace(appId) && string.IsNullOrWhiteSpace(secret)) return;
        var current = ResolveBaidu();
        appId = appId?.Trim();
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (!string.IsNullOrWhiteSpace(appId))
            {
                // Changing the APPID without a matching key would silently pair the
                // new APPID with the old secret, so it is only refused when there is
                // no stored secret at all.
                if (!string.Equals(appId, current.AppId, StringComparison.Ordinal) && !current.HasSecret)
                    throw new InvalidOperationException("更换百度 APPID 时，请同时填写对应密钥。");
                _settings.BaiduAppId = appId;
            }
            // A credential that only exists in the legacy appsettings.json is
            // migrated on save: the updater deletes that file, this store survives.
            if (current.Source == SecretSource.LegacyConfig && current.Credentials is not null)
                PersistSecret("baidu", current.Credentials.SecretKey, value => _settings.BaiduProtectedKey = value);
            Save(_settings);
            return;
        }
        if (string.IsNullOrWhiteSpace(appId)) throw new InvalidOperationException("请填写百度 APPID。");
        PersistSecret("baidu", secret.Trim(), value => _settings.BaiduProtectedKey = value);
        _settings.BaiduAppId = appId;
        Save(_settings);
    }

    public void Save(AppSettings settings)
    {
        _settings = settings;
        AtomicJson.Write(_path, settings);
    }

    public string? ResolveKey(ProviderKind provider) => ResolveKeyDetailed(provider).Value;

    /// <summary>
    /// The stored secret only: the platform store (keyring, then the durable config
    /// file) and the legacy Windows <c>providers.json</c>, never the environment.
    /// The AI usage monitor needs this split: its "manual key" source must ignore
    /// environment variables, exactly like the Windows
    /// <c>ProviderSettingsStore.ResolveKey</c> does when the source is "Manual"
    /// (AIUsageMonitor/Program.cs L133-L137).
    /// </summary>
    public string? ResolveStoredKey(ProviderKind provider)
    {
        var protectedValue = provider == ProviderKind.Glm ? _settings.GlmProtectedKey : _settings.DeepSeekProtectedKey;
        var stored = ResolveSecret(ProviderName(provider), protectedValue);
        if (stored.HasValue) return stored.Value;
        return TryReadLegacyKey(provider);
    }

    public SecretResolution ResolveKeyDetailed(ProviderKind provider)
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
            if (!string.IsNullOrWhiteSpace(value)) return new SecretResolution(SecretSource.Environment, value.Trim());
        }

        var protectedValue = provider == ProviderKind.Glm ? _settings.GlmProtectedKey : _settings.DeepSeekProtectedKey;
        var stored = ResolveSecret(ProviderName(provider), protectedValue);
        if (stored.HasValue) return stored;

        var legacy = TryReadLegacyKey(provider);
        return string.IsNullOrWhiteSpace(legacy)
            ? new SecretResolution(SecretSource.None, null)
            : new SecretResolution(SecretSource.LegacyConfig, legacy);
    }

    /// <summary>
    /// True when this build has somewhere durable to put a secret: DPAPI on
    /// Windows, or the keyring / config file on Linux. Secret-tool is no longer
    /// required, because the config file is always available as a fallback.
    /// </summary>
    public bool CanSaveKeys => OperatingSystem.IsWindows() || _credentialFile.CanWrite;

    public void SaveKey(ProviderKind provider, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var storedViaSettings = false;
        PersistSecret(ProviderName(provider), key.Trim(), value =>
        {
            if (provider == ProviderKind.Glm) _settings.GlmProtectedKey = value;
            else _settings.DeepSeekProtectedKey = value;
            storedViaSettings = true;
        });
        if (storedViaSettings) Save(_settings);
    }

    /// <summary>
    /// Platform store first, durable config file second. The keyring only wins
    /// when it really accepted the write, so a locked keyring degrades to the file
    /// instead of losing the key.
    /// </summary>
    private void PersistSecret(string provider, string secret, Action<string> storeProtected)
    {
        if (OperatingSystem.IsWindows())
        {
            var encrypted = Protect(secret);
            if (!string.IsNullOrWhiteSpace(encrypted)) storeProtected(encrypted!);
            return;
        }
        if (KeyringAvailable && TryWriteLinuxKeyring(provider, secret)) return;
        _credentialFile.Write(provider, secret);
    }

    private SecretResolution ResolveSecret(string provider, string? protectedValue)
    {
        var unprotected = Unprotect(protectedValue);
        if (!string.IsNullOrWhiteSpace(unprotected)) return new SecretResolution(SecretSource.WindowsDpapi, unprotected);
        var keyring = ReadLinuxKeyring(provider);
        if (!string.IsNullOrWhiteSpace(keyring)) return new SecretResolution(SecretSource.LinuxKeyring, keyring);
        var file = _credentialFile.Read(provider);
        if (!string.IsNullOrWhiteSpace(file)) return new SecretResolution(SecretSource.ConfigFile, file);
        return new SecretResolution(SecretSource.None, null);
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

    private static string? ReadLinuxKeyring(string provider)
    {
        if (!OperatingSystem.IsLinux() || FindExecutable("secret-tool") is null) return null;
        try
        {
            using var process = StartSecretTool(["lookup", "service", "little-tools-assistant", "provider", provider], false);
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

    /// <summary>Writes to the keyring and reports whether it actually worked.</summary>
    private static bool TryWriteLinuxKeyring(string provider, string key)
    {
        try
        {
            using var process = StartSecretTool(
                ["store", "--label=Little Tools Assistant", "service", "little-tools-assistant", "provider", provider],
                true);
            process.StandardInput.Write(key);
            process.StandardInput.Close();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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
