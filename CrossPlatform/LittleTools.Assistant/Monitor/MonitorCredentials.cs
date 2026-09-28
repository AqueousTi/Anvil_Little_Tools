using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// Where a monitor provider key really came from. Reported by <c>--diagnose</c>
/// and by the provider settings dialog; never carries the key itself.
/// </summary>
internal enum MonitorKeySource
{
    None,
    Environment,
    Manual,
    Keyring,
    Credentials
}

/// <summary>
/// One provider's resolved key plus its origin. <see cref="Storage"/> keeps the
/// finer platform detail (<c>keyring</c>/<c>config</c>/<c>legacy</c>/<c>dpapi</c>)
/// so the dialog can name the exact store while <see cref="Source"/> stays the
/// five value vocabulary the diagnostics promise.
/// </summary>
internal readonly record struct MonitorKeyResolution(
    MonitorKeySource Source,
    SecretSource Storage,
    string? Value)
{
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);
}

/// <summary>
/// The monitor's key resolution, ported from
/// <c>ProviderSettingsStore.ResolveKey</c>/<c>ResolveGlmKey</c>
/// (AIUsageMonitor/Program.cs L133-L150).
///
/// Windows split the two sources like this: "Environment" reads the named
/// environment variable (GLM additionally falls back to five known aliases), and
/// "Manual" unprotects the DPAPI blob stored in <c>providers.json</c>. Linux has no
/// DPAPI, so the manual secret lives in the suite's platform secret store (GNOME
/// keyring, then <c>credentials.json</c>) - the same place the assistant's own GLM
/// and DeepSeek keys live (<see cref="SettingsStore.SaveKey"/>). Deliberate
/// deviation, documented in the porting notes: one key entered in either module
/// serves both, and a Windows DPAPI blob cannot be decrypted here, which the
/// settings dialog reports instead of silently ignoring it.
///
/// Linux addition (deliberate deviation, README item 18): a monitor file that was
/// adopted from Windows still says <c>Source = "Environment"</c> with the Windows
/// variable name, and those variables simply do not exist on this machine - so the
/// provider looked unconfigured while the key was sitting in the platform secret
/// store all along. When the configured source yields nothing (an unset *or empty*
/// variable), the resolution now falls through to the platform store, in the same
/// order the assistant itself uses: keyring, then the 0600 credentials file, then
/// the legacy Windows file. A key that the user typed into the dialog still wins
/// over that fallback, because <c>Source = "Manual"</c> is resolved from the store
/// first and the environment is never consulted for it.
/// </summary>
internal static class MonitorCredentials
{
    /// <summary>Windows ProviderSettingsStore.ResolveGlmKey aliases (Program.cs L143).</summary>
    private static readonly string[] GlmAliases =
        ["ZAI_CODING_CN_API_KEY", "ZHIPUAI_API_KEY", "ZHIPU_API_KEY", "GLM_API_KEY", "BIGMODEL_API_KEY"];

    /// <summary>True when the settings name "Manual" rather than "Environment".</summary>
    public static bool IsManual(string? source) =>
        string.Equals(source, "Manual", StringComparison.OrdinalIgnoreCase);

    /// <summary>The key for one provider, or null when nothing is configured.</summary>
    public static string? Resolve(ProviderSettings providers, bool glm) =>
        ResolveDetailed(providers, glm).Value;

    /// <summary>
    /// The key for one provider together with the store that answered.
    ///
    /// Order: the configured source first ("Manual" = the platform secret store,
    /// "Environment" = the named variable and, for GLM, the Windows aliases), then
    /// - only when that yields nothing - the platform secret store, so a Windows
    /// imported <c>providers.json</c> keeps working against a Linux keyring. An
    /// environment variable that exists but is blank counts as unset and keeps the
    /// fallback alive, exactly like the assistant's own resolver.
    /// </summary>
    public static MonitorKeyResolution ResolveDetailed(ProviderSettings providers, bool glm)
    {
        var kind = glm ? ProviderKind.Glm : ProviderKind.DeepSeek;
        var store = new SettingsStore();
        if (IsManual(glm ? providers.GlmSource : providers.DeepSeekSource))
        {
            // A manual key is whatever the platform store holds; the environment is
            // deliberately ignored for it (Windows ProviderSettingsStore L133-L137).
            var manual = store.ResolveStoredKeyDetailed(kind);
            return manual.HasValue
                ? new MonitorKeyResolution(MonitorKeySource.Manual, manual.Source, manual.Value)
                : new MonitorKeyResolution(MonitorKeySource.None, SecretSource.None, null);
        }

        var fromEnvironment = ResolveEnvironment(providers, glm);
        if (fromEnvironment is not null)
            return new MonitorKeyResolution(MonitorKeySource.Environment, SecretSource.Environment, fromEnvironment);

        var stored = store.ResolveStoredKeyDetailed(kind);
        if (!stored.HasValue) return new MonitorKeyResolution(MonitorKeySource.None, SecretSource.None, null);
        var source = stored.Source == SecretSource.LinuxKeyring ? MonitorKeySource.Keyring : MonitorKeySource.Credentials;
        return new MonitorKeyResolution(source, stored.Source, stored.Value);
    }

    /// <summary>
    /// The configured environment variable, then the Windows GLM aliases. A
    /// variable that is present but blank is treated as unset, so the caller can
    /// keep falling back instead of resolving an empty key.
    /// </summary>
    private static string? ResolveEnvironment(ProviderSettings providers, bool glm)
    {
        var configured = glm ? providers.GlmEnvironment : providers.DeepSeekEnvironment;
        var value = Read(configured);
        if (value is not null) return value;
        if (!glm) return null;
        foreach (var alias in GlmAliases)
        {
            value = Read(alias);
            if (value is not null) return value;
        }
        return null;
    }

    private static string? Read(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var value = Environment.GetEnvironmentVariable(name.Trim());
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Stores a manually entered key in the platform secret store.</summary>
    public static bool SaveManualKey(bool glm, string key)
    {
        try
        {
            new SettingsStore().SaveKey(glm ? ProviderKind.Glm : ProviderKind.DeepSeek, key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The one line the settings dialog shows under a provider: which store the
    /// monitor will really read, or that there is nothing to read. It never echoes
    /// the key, only the origin.
    /// </summary>
    public static string Describe(ProviderSettings providers, bool glm)
    {
        var enabled = glm ? providers.GlmEnabled : providers.DeepSeekEnabled;
        if (!enabled) return "未启用";
        var name = glm ? providers.GlmEnvironment : providers.DeepSeekEnvironment;
        var resolution = ResolveDetailed(providers, glm);
        return resolution.Source switch
        {
            MonitorKeySource.Environment => "已从环境变量 " + (string.IsNullOrWhiteSpace(name)
                ? (glm ? "ZHIPUAI_API_KEY" : "DEEPSEEK_API_KEY") : name.Trim()) + " 读取",
            MonitorKeySource.Manual => "已从" + StorageName(resolution.Storage) + "读取手动保存的 Key",
            MonitorKeySource.Keyring => "已从系统钥匙串读取（环境变量 "
                + (string.IsNullOrWhiteSpace(name) ? (glm ? "ZHIPUAI_API_KEY" : "DEEPSEEK_API_KEY") : name.Trim())
                + " 未设置，已回退）",
            MonitorKeySource.Credentials => "已从" + StorageName(resolution.Storage) + "读取（环境变量未设置，已回退）",
            _ => "未配置 API Key"
        };
    }

    /// <summary>
    /// The short name of the store that answered, for the dialog and
    /// <c>--diagnose</c>.
    /// </summary>
    public static string SourceName(MonitorKeySource source) => source switch
    {
        MonitorKeySource.Environment => "env",
        MonitorKeySource.Manual => "manual",
        MonitorKeySource.Keyring => "keyring",
        MonitorKeySource.Credentials => "credentials",
        _ => "none"
    };

    /// <summary>The platform store behind <see cref="MonitorKeyResolution.Storage"/>.</summary>
    public static string StorageName(SecretSource source) => source switch
    {
        SecretSource.Environment => "环境变量",
        SecretSource.WindowsDpapi => "Windows DPAPI",
        SecretSource.LinuxKeyring => "系统钥匙串",
        SecretSource.ConfigFile => "凭据文件",
        SecretSource.LegacyConfig => "旧版 appsettings.json",
        _ => "未保存"
    };

    /// <summary>
    /// True when the providers file carries a Windows DPAPI blob that this platform
    /// cannot decrypt, so the dialog can say "re-enter the key" instead of showing a
    /// configured provider that never resolves.
    /// </summary>
    public static bool HasUndecryptableWindowsKey(ProviderSettings providers, bool glm)
    {
        if (OperatingSystem.IsWindows()) return false;
        var blob = glm ? providers.GlmProtectedKey : providers.DeepSeekProtectedKey;
        if (string.IsNullOrWhiteSpace(blob)) return false;
        return !IsManual(glm ? providers.GlmSource : providers.DeepSeekSource)
            || string.IsNullOrWhiteSpace(new SettingsStore().ResolveStoredKey(
                glm ? ProviderKind.Glm : ProviderKind.DeepSeek));
    }
}
