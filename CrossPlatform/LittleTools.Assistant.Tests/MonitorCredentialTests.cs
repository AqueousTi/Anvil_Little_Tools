using LittleTools.Assistant.Monitor;
using LittleTools.Assistant.Services;

/// <summary>
/// The monitor's key resolution, including the Linux fallback added for the
/// "余量监控显示未配置 API" report: an imported Windows <c>providers.json</c> says
/// <c>Source = "Environment"</c> with a variable that does not exist on this
/// machine, so the key in the platform store must still be found. The keyring is
/// unreachable here (PATH points at an empty directory, exactly like
/// <see cref="CredentialTests"/>), so these cases exercise the
/// <c>credentials.json</c> half of the store; the keyring half is verified on the
/// real machine.
/// </summary>
internal static class MonitorCredentialTests
{
    private static readonly string[] ProviderVariables =
    [
        "DEEPSEEK_API_KEY", "ZHIPUAI_API_KEY", "ZHIPU_API_KEY", "GLM_API_KEY", "BIGMODEL_API_KEY",
        "ZAI_CODING_CN_API_KEY"
    ];

    public static void Run(Action<string, bool> check)
    {
        var checks = 0;
        void Counted(string name, bool result)
        {
            checks++;
            check(name, result);
        }
        RunCore(Counted);
        Console.WriteLine($"MONITOR CREDENTIALS: {checks} checks");
    }

    private static void RunCore(Action<string, bool> check)
    {
        var root = Path.GetFullPath(Path.Combine(".artifacts", "monitor-credential-test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);

        var saved = new Dictionary<string, string?>();
        foreach (var name in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "PATH", "TRANSLATE_APP_CONFIG" }
            .Concat(ProviderVariables)
            .Concat([SettingsStore.KeyringDisableVariable]))
            saved[name] = Environment.GetEnvironmentVariable(name);

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));
            Environment.SetEnvironmentVariable("TRANSLATE_APP_CONFIG", null);
            // An empty PATH means secret-tool is not found: the keyring can never be
            // read from or written to the developer's real login session here.
            Environment.SetEnvironmentVariable("PATH", Path.Combine(root, "empty-path"));
            foreach (var name in ProviderVariables) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(SettingsStore.KeyringDisableVariable, null);

            var credentialFile = new CredentialFile(AppPaths.CredentialsPath);
            credentialFile.Write("glm", "stored-glm");
            credentialFile.Write("deepseek", "stored-deepseek");

            CheckEnvironmentWins(check);
            CheckEmptyEnvironmentFallsBack(check);
            CheckStoredFallback(check);
            CheckManualSource(check);
            CheckAliases(check);
            CheckNothingConfigured(check);
            CheckDescribe(check);
            CheckFieldShape(check);
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>The exact file the user's machine was imported with.</summary>
    private static ProviderSettings Imported() => new()
    {
        CodexEnabled = true,
        DeepSeekEnabled = true,
        GlmEnabled = true,
        DeepSeekSource = "Environment",
        DeepSeekEnvironment = "DEEPSEEK_API_KEY",
        DeepSeekProtectedKey = null,
        GlmSource = "Environment",
        GlmEnvironment = "ZHIPUAI_API_KEY",
        GlmProtectedKey = null
    };

    private static void CheckEnvironmentWins(Action<string, bool> check)
    {
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "env-deepseek");
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", "env-glm");
        var deepSeek = MonitorCredentials.ResolveDetailed(Imported(), glm: false);
        var glm = MonitorCredentials.ResolveDetailed(Imported(), glm: true);
        check("a set environment variable still wins",
            deepSeek.Source == MonitorKeySource.Environment && deepSeek.Value == "env-deepseek"
            && glm.Source == MonitorKeySource.Environment && glm.Value == "env-glm");
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", null);
    }

    private static void CheckEmptyEnvironmentFallsBack(Action<string, bool> check)
    {
        // Requirement: a variable that exists but is empty counts as unset, so the
        // fallback stays alive instead of resolving an empty key.
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "   ");
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", string.Empty);
        var deepSeek = MonitorCredentials.ResolveDetailed(Imported(), glm: false);
        var glm = MonitorCredentials.ResolveDetailed(Imported(), glm: true);
        check("a blank environment variable is treated as unset for DeepSeek",
            deepSeek.Source == MonitorKeySource.Credentials && deepSeek.Value == "stored-deepseek");
        check("a blank environment variable is treated as unset for GLM",
            glm.Source == MonitorKeySource.Credentials && glm.Value == "stored-glm");
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", null);
    }

    private static void CheckStoredFallback(Action<string, bool> check)
    {
        // The reported bug: the Windows import names variables that do not exist on
        // this machine, and the key was really in the platform store.
        var deepSeek = MonitorCredentials.ResolveDetailed(Imported(), glm: false);
        var glm = MonitorCredentials.ResolveDetailed(Imported(), glm: true);
        check("an imported Environment source falls back to the stored key",
            deepSeek.Value == "stored-deepseek" && glm.Value == "stored-glm");
        check("the fallback reports credentials, not none",
            deepSeek.Source == MonitorKeySource.Credentials && glm.Source == MonitorKeySource.Credentials);
        check("the fallback keeps the finer storage detail",
            deepSeek.Storage == SecretSource.ConfigFile && glm.Storage == SecretSource.ConfigFile);
        check("Resolve() returns the same key as ResolveDetailed()",
            MonitorCredentials.Resolve(Imported(), glm: true) == "stored-glm");
    }

    private static void CheckManualSource(Action<string, bool> check)
    {
        // A key the user typed into the dialog is preferred over everything the
        // configured source would have produced, and the environment is ignored.
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", "env-glm");
        var manual = new ProviderSettings { GlmEnabled = true, GlmSource = "Manual", GlmEnvironment = "ZHIPUAI_API_KEY" };
        var resolution = MonitorCredentials.ResolveDetailed(manual, glm: true);
        check("a manual key is resolved from the platform store and wins over the environment",
            resolution.Source == MonitorKeySource.Manual && resolution.Value == "stored-glm");
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", null);
    }

    private static void CheckAliases(Action<string, bool> check)
    {
        var settings = new ProviderSettings
        {
            GlmEnabled = true, GlmSource = "Environment", GlmEnvironment = "ZHIPUAI_API_KEY"
        };
        Environment.SetEnvironmentVariable("ZHIPU_API_KEY", "alias-glm");
        var alias = MonitorCredentials.ResolveDetailed(settings, glm: true);
        check("the Windows GLM aliases still work",
            alias.Source == MonitorKeySource.Environment && alias.Value == "alias-glm");

        // The Windows name configured in the file is tried before the aliases.
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", "configured-glm");
        var configured = MonitorCredentials.ResolveDetailed(settings, glm: true);
        check("the configured variable is tried before the aliases",
            configured.Value == "configured-glm");
        Environment.SetEnvironmentVariable("ZHIPU_API_KEY", null);
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", null);

        // An empty configured name must not throw or match an empty variable.
        var nameless = new ProviderSettings { GlmEnabled = true, GlmSource = "Environment", GlmEnvironment = "  " };
        check("a blank configured variable name falls back instead of throwing",
            MonitorCredentials.ResolveDetailed(nameless, glm: true).Value == "stored-glm");
    }

    private static void CheckNothingConfigured(Action<string, bool> check)
    {
        var empty = AppPaths.CredentialsPath;
        var backup = File.ReadAllText(empty);
        File.Delete(empty);
        try
        {
            var resolution = MonitorCredentials.ResolveDetailed(Imported(), glm: true);
            check("no key anywhere resolves to none",
                resolution.Source == MonitorKeySource.None && !resolution.HasValue && resolution.Value is null);
            check("none has no storage either", resolution.Storage == SecretSource.None);
        }
        finally
        {
            File.WriteAllText(empty, backup);
        }
    }

    private static void CheckDescribe(Action<string, bool> check)
    {
        var resolution = MonitorCredentials.Describe(Imported(), glm: true);
        check("the dialog says where a fallback key came from, never 未配置",
            resolution.Contains("已从凭据文件", StringComparison.Ordinal)
            && !resolution.Contains("未配置", StringComparison.Ordinal));
        check("the dialog never echoes the key", !resolution.Contains("stored-glm", StringComparison.Ordinal));
        check("the dialog reports a disabled provider as 未启用",
            MonitorCredentials.Describe(new ProviderSettings { GlmEnabled = false }, glm: true) == "未启用");

        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", "env-glm");
        var fromEnvironment = MonitorCredentials.Describe(Imported(), glm: true);
        check("the dialog names the environment variable when that is the source",
            fromEnvironment.Contains("已从环境变量 ZHIPUAI_API_KEY 读取", StringComparison.Ordinal));
        Environment.SetEnvironmentVariable("ZHIPUAI_API_KEY", null);
    }

    private static void CheckFieldShape(Action<string, bool> check)
    {
        // The Linux enhancement must not change the Windows file: the monitor still
        // reads and writes the same fields with the same values.
        var json = MonitorJson.Serialize(Imported());
        check("providers.json keeps the Windows field names and shapes",
            json.Contains("\"DeepSeekSource\": \"Environment\"", StringComparison.Ordinal)
            && json.Contains("\"DeepSeekEnvironment\": \"DEEPSEEK_API_KEY\"", StringComparison.Ordinal)
            && json.Contains("\"DeepSeekProtectedKey\": null", StringComparison.Ordinal)
            && json.Contains("\"GlmSource\": \"Environment\"", StringComparison.Ordinal)
            && json.Contains("\"GlmEnvironment\": \"ZHIPUAI_API_KEY\"", StringComparison.Ordinal)
            && json.Contains("\"GlmProtectedKey\": null", StringComparison.Ordinal));

        // --diagnose reads providers.json without adopting a Windows folder, so a
        // report run can never create or overwrite the user's data.
        var target = Path.Combine(AppPaths.DataDirectory, "diagnose-readonly");
        var store = new MonitorStore(target);
        var providers = store.LoadProviders(importLegacyFiles: false);
        check("a diagnose read does not import or create files",
            !Directory.Exists(target) && store.ImportedFrom.Count == 0 && providers.DeepSeekEnabled);
    }
}
