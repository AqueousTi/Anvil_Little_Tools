using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Monitor;

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
    public static string? Resolve(ProviderSettings providers, bool glm)
    {
        var source = glm ? providers.GlmSource : providers.DeepSeekSource;
        if (IsManual(source))
            return new SettingsStore().ResolveStoredKey(glm ? ProviderKind.Glm : ProviderKind.DeepSeek);

        var configured = glm ? providers.GlmEnvironment : providers.DeepSeekEnvironment;
        var value = Environment.GetEnvironmentVariable(string.IsNullOrWhiteSpace(configured) ? string.Empty : configured.Trim());
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        if (!glm) return null;
        foreach (var alias in GlmAliases)
        {
            value = Environment.GetEnvironmentVariable(alias);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
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
