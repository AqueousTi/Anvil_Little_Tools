using LittleTools.Assistant;
using LittleTools.Assistant.Services;

/// <summary>
/// Covers the Linux credential store introduced for the "every update asks for the
/// Baidu API key again" report: the durable fallback file, its permissions, the
/// resolution order and the display path that no longer depends on the secret
/// being readable.
/// </summary>
internal static class CredentialTests
{
    public static void Run(Action<string, bool> check)
    {
        var checks = 0;
        void Counted(string name, bool result)
        {
            checks++;
            check(name, result);
        }
        RunCore(Counted);
        Console.WriteLine($"CREDENTIALS: {checks} checks");
    }

    private static void RunCore(Action<string, bool> check)
    {
        var root = Path.GetFullPath(Path.Combine(".artifacts", "credential-test-" + Guid.NewGuid().ToString("N")));
        var config = Path.Combine(root, "config");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(data);

        var saved = new Dictionary<string, string?>();
        foreach (var name in new[]
        {
            "XDG_CONFIG_HOME", "XDG_DATA_HOME", "PATH", "TRANSLATE_APP_CONFIG",
            SettingsStore.BaiduAppIdVariable, SettingsStore.BaiduSecretVariable, "ZHIPUAI_API_KEY", "DEEPSEEK_API_KEY"
        }) saved[name] = Environment.GetEnvironmentVariable(name);

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("TRANSLATE_APP_CONFIG", null);
            Environment.SetEnvironmentVariable(SettingsStore.BaiduAppIdVariable, null);
            Environment.SetEnvironmentVariable(SettingsStore.BaiduSecretVariable, null);
            Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", null);
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
            // An empty PATH means secret-tool is not found, so the tests can never
            // write into (or read from) the developer's real GNOME keyring.
            Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "empty-path"));

            CheckFileStore(check, root);
            CheckResolution(check);
            CheckEnvironmentOverride(check);
            CheckLegacyMigration(check);
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void CheckFileStore(Action<string, bool> check, string root)
    {
        var file = new CredentialFile(Path.Combine(root, "secrets", "credentials.json"));
        check("credential file starts empty", file.Read("baidu") is null);
        file.Write("baidu", "secret-one");
        check("credential file round trips", file.Read("baidu") == "secret-one");
        file.Write("glm", "key-two");
        check("credential file keeps every provider", file.Read("baidu") == "secret-one" && file.Read("glm") == "key-two");
        check("credential file reports a missing provider", file.Read("deepseek") is null);
        if (OperatingSystem.IsWindows()) return;
        check("credential file is 0600",
            File.GetUnixFileMode(file.Path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
        check("credential directory is 0700",
            File.GetUnixFileMode(Path.GetDirectoryName(file.Path)!) ==
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }

    private static void CheckResolution(Action<string, bool> check)
    {
        var store = new SettingsStore();
        check("a key can be saved without secret-tool", store.CanSaveKeys);
        store.SaveBaiduCredentials("appid-1", "secret-1");
        check("the APPID is stored in assistant-settings.json", new SettingsStore().StoredBaiduAppId == "appid-1");
        check("the secret falls back to the config file", store.ResolveBaidu().Source == SecretSource.ConfigFile);
        check("the config file is under the XDG config directory",
            AppPaths.CredentialsPath == Path.Combine(AppPaths.ConfigDirectory, "translate", "credentials.json")
            && File.Exists(AppPaths.CredentialsPath));
        var restarted = new SettingsStore();
        check("a fresh process resolves the saved pair",
            restarted.ResolveBaiduCredentials() is { AppId: "appid-1", SecretKey: "secret-1" });

        // The reported bug: reading the APPID must not depend on the secret.
        store.SaveKey(ProviderKind.DeepSeek, "deepseek-file");
        File.Delete(AppPaths.CredentialsPath);
        var withoutSecret = new SettingsStore();
        check("the APPID survives a missing secret", withoutSecret.StoredBaiduAppId == "appid-1");
        check("a missing secret resolves nothing", withoutSecret.ResolveBaiduCredentials() is null);
        check("a missing secret is reported as none", withoutSecret.ResolveBaidu().Source == SecretSource.None);
        check("without a keyring the config file is the only store", withoutSecret.ResolveKey(ProviderKind.DeepSeek) is null);
    }

    private static void CheckEnvironmentOverride(Action<string, bool> check)
    {
        Environment.SetEnvironmentVariable(SettingsStore.BaiduAppIdVariable, "env-app");
        Environment.SetEnvironmentVariable(SettingsStore.BaiduSecretVariable, "env-secret");
        var env = new SettingsStore().ResolveBaidu();
        check("baidu environment variables win",
            env.Source == SecretSource.Environment && env.Credentials is { AppId: "env-app", SecretKey: "env-secret" });
        Environment.SetEnvironmentVariable(SettingsStore.BaiduAppIdVariable, null);
        Environment.SetEnvironmentVariable(SettingsStore.BaiduSecretVariable, null);

        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", "glm-env");
        check("a provider environment variable wins",
            new SettingsStore().ResolveKeyDetailed(ProviderKind.Glm).Source == SecretSource.Environment);
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", null);

        new SettingsStore().SaveKey(ProviderKind.Glm, "glm-file");
        check("a provider key falls back to the config file", new SettingsStore().ResolveKey(ProviderKind.Glm) == "glm-file");
    }

    private static void CheckLegacyMigration(Action<string, bool> check)
    {
        var legacy = Path.Combine(AppPaths.ConfigDirectory, "translate", "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, """{"AppId":"legacy-app","SecretKey":"legacy-secret"}""");
        // Drop the durable stores so only the legacy file can answer.
        File.Delete(AppPaths.CredentialsPath);
        new SettingsStore().Save(new AppSettings());

        var store = new SettingsStore();
        var resolved = store.ResolveBaidu();
        check("the parked appsettings.json is read",
            resolved.Source == SecretSource.LegacyConfig && resolved.Credentials is { AppId: "legacy-app", SecretKey: "legacy-secret" });
        check("the legacy APPID is shown without the keyring", store.StoredBaiduAppId == "legacy-app");

        // Saving with an empty secret is the migration step the dialog promises.
        store.SaveBaiduCredentials("legacy-app", null);
        var migrated = new SettingsStore();
        check("saving migrates the legacy credential",
            migrated.ResolveBaidu().Source == SecretSource.ConfigFile
            && migrated.ResolveBaiduCredentials() is { AppId: "legacy-app", SecretKey: "legacy-secret" });
        File.Delete(legacy);
        check("the migrated APPID no longer needs the legacy file", new SettingsStore().StoredBaiduAppId == "legacy-app");
        check("the migrated secret no longer needs the legacy file",
            new SettingsStore().ResolveBaiduCredentials() is { SecretKey: "legacy-secret" });
    }
}
