using System.Text.Json;

namespace LittleTools.Assistant.Services;

/// <summary>
/// The platform integration snapshot written by <c>--diagnose</c>.
/// When another instance is already running the UI cannot be started again, so a
/// headless snapshot is written instead; it still answers the questions that
/// matter (which instance owns the tray, where the data lives, what is missing).
/// </summary>
internal static class Diagnostics
{
    public static void WriteHeadless(string path, int runningInstancePid)
    {
        try
        {
            var session = SessionEnvironment.Current;
            var settings = new SuiteSettingsStore();
            var todoFile = Path.Combine(AppPaths.TodoDirectory, "data.json");
            var stockFile = Path.Combine(AppPaths.StockDirectory, "settings.json");
            var monitorFile = Path.Combine(AppPaths.MonitorDirectory, "providers.json");
            var monitorSnapshot = Path.Combine(AppPaths.MonitorDirectory, "snapshot.json");
            var payload = new
            {
                platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other",
                mode = "headless",
                note = "已有实例在运行，未启动界面；托盘与快捷键状态请用退出后的完整诊断查看。",
                runningInstancePid,
                session = SessionEnvironment.Describe(session),
                sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
                display = Environment.GetEnvironmentVariable("DISPLAY"),
                waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
                configDirectory = AppPaths.ConfigDirectory,
                dataDirectory = AppPaths.DataDirectory,
                cacheDirectory = AppPaths.CacheDirectory,
                managerSettings = AppPaths.ManagerSettingsPath,
                managerSettingsExists = File.Exists(AppPaths.ManagerSettingsPath),
                switches = new
                {
                    settings.Current.MonitorEnabled,
                    settings.Current.TranslateEnabled,
                    settings.Current.TodoNotesEnabled,
                    settings.Current.StockEnabled
                },
                modules = new Dictionary<string, bool>
                {
                    [SuiteMenuBuilder.Monitor] = SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Monitor, OperatingSystem.IsLinux()),
                    [SuiteMenuBuilder.Assistant] = true,
                    [SuiteMenuBuilder.Todo] = SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Todo, OperatingSystem.IsLinux()),
                    [SuiteMenuBuilder.Stock] = SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Stock, OperatingSystem.IsLinux()),
                    [SuiteMenuBuilder.Monitor] = SuiteMenuBuilder.IsModuleAvailable(SuiteMenuBuilder.Monitor, OperatingSystem.IsLinux())
                },
                todoDataPath = todoFile,
                todoDataExists = File.Exists(todoFile),
                stockDataPath = stockFile,
                stockDataExists = File.Exists(stockFile),
                monitorDataPath = monitorFile,
                monitorDataExists = File.Exists(monitorFile),
                monitorSnapshotPath = monitorSnapshot,
                monitorSnapshotExists = File.Exists(monitorSnapshot),
                monitorFixtureReplay = Monitor.MonitorDataServiceFactory.IsFixtureReplayEnabled,
                autostartEnabled = OperatingSystem.IsLinux() && new AutostartService().IsEnabled,
                autostartEntry = OperatingSystem.IsLinux() ? new AutostartService().EntryPath : null,
                credentials = DescribeCredentials(),
                tools = new[] { "notify-send", "xdg-open", "secret-tool", "gnome-screenshot", "spectacle", "grim", "slurp", "canberra-gtk-play", "aplay" }
                    .ToDictionary(name => name, name => ExecutableLocator.Find(name) is not null)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Diagnostics must never take the app down.
        }
    }

    /// <summary>
    /// Which store each credential comes from (env / keyring / config file /
    /// legacy / none). This is the field that answers "why am I asked for the API
    /// key again" without having to open the settings dialog.
    /// </summary>
    internal static object DescribeCredentials()
    {
        try
        {
            var store = new SettingsStore();
            var baidu = store.ResolveBaidu();
            var glm = store.ResolveKeyDetailed(ProviderKind.Glm);
            var deepSeek = store.ResolveKeyDetailed(ProviderKind.DeepSeek);
            return new
            {
                baidu = new
                {
                    source = SourceName(baidu.Source),
                    appId = store.StoredBaiduAppId,
                    appIdStored = !string.IsNullOrWhiteSpace(store.StoredBaiduAppId),
                    secretStored = baidu.HasSecret,
                    environmentVariable = SettingsStore.BaiduAppIdVariable,
                    secretEnvironmentVariable = SettingsStore.BaiduSecretVariable
                },
                glm = new
                {
                    source = SourceName(glm.Source),
                    secretStored = glm.HasValue,
                    environmentVariable = store.Current.GlmEnvironmentVariable
                },
                deepSeek = new
                {
                    source = SourceName(deepSeek.Source),
                    secretStored = deepSeek.HasValue,
                    environmentVariable = store.Current.DeepSeekEnvironmentVariable
                },
                keyringAvailable = store.KeyringAvailable,
                configFile = store.CredentialsPath,
                configFileExists = File.Exists(store.CredentialsPath),
                legacyAppIdFound = BaiduCredentials.ReadLegacyAppId(AppContext.BaseDirectory) is not null
            };
        }
        catch (Exception exception)
        {
            return new { error = exception.Message };
        }
    }

    private static string SourceName(SecretSource source) => source switch
    {
        SecretSource.Environment => "env",
        SecretSource.WindowsDpapi => "dpapi",
        SecretSource.LinuxKeyring => "keyring",
        SecretSource.ConfigFile => "config",
        SecretSource.LegacyConfig => "legacy",
        _ => "none"
    };
}
