using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant;

public sealed partial class SettingsWindow : Window
{
    private const string SavedPlaceholder = "已保存（留空=不修改）";
    private const string EmptyPlaceholder = "未保存，请填写";

    private readonly SettingsStore _store;

    public SettingsWindow() : this(new SettingsStore()) { }

    internal SettingsWindow(SettingsStore store)
    {
        _store = store;
        AvaloniaXamlLoader.Load(this);
        var settings = store.Current;

        // The APPID comes from the stored value, never from the resolved pair: an
        // unreachable keyring must not make a saved APPID look lost.
        Find<TextBox>("BaiduAppId").Text = store.StoredBaiduAppId ?? string.Empty;
        Find<TextBox>("GlmEnvironment").Text = settings.GlmEnvironmentVariable;
        Find<TextBox>("DeepSeekEnvironment").Text = settings.DeepSeekEnvironmentVariable;

        var baidu = store.ResolveBaidu();
        Placeholder(Find<TextBox>("BaiduKey"), baidu.HasSecret);
        Placeholder(Find<TextBox>("GlmKey"), store.ResolveKeyDetailed(ProviderKind.Glm).HasValue);
        Placeholder(Find<TextBox>("DeepSeekKey"), store.ResolveKeyDetailed(ProviderKind.DeepSeek).HasValue);

        var canSave = store.CanSaveKeys;
        Find<TextBox>("GlmKey").IsEnabled = canSave;
        Find<TextBox>("DeepSeekKey").IsEnabled = canSave;
        Find<TextBox>("BaiduKey").IsEnabled = canSave;
        Find<TextBlock>("BaiduKeyHint").Text = BaiduHint(baidu, canSave);
        Find<TextBlock>("SettingsStatus").Text = canSave
            ? "配置状态：百度 " + BaiduState(baidu)
                + "，GLM " + Describe(store.ResolveKeyDetailed(ProviderKind.Glm))
                + "，DeepSeek " + Describe(store.ResolveKeyDetailed(ProviderKind.DeepSeek))
            : "无法写入 " + store.CredentialsPath + "，请检查配置目录权限，或改用环境变量。";

        Find<Button>("CancelButton").Click += (_, _) => Close(false);
        Find<Button>("SaveButton").Click += (_, _) => Save();
    }

    private void Save()
    {
        try
        {
            if (_store.CanSaveKeys)
                _store.SaveBaiduCredentials(Find<TextBox>("BaiduAppId").Text, Find<TextBox>("BaiduKey").Text);
            var settings = _store.Current;
            settings.GlmEnvironmentVariable = (Find<TextBox>("GlmEnvironment").Text ?? "ZHIPUAI_API_KEY").Trim();
            settings.DeepSeekEnvironmentVariable = (Find<TextBox>("DeepSeekEnvironment").Text ?? "DEEPSEEK_API_KEY").Trim();
            _store.Save(settings);
            if (_store.CanSaveKeys)
            {
                _store.SaveKey(ProviderKind.Glm, Find<TextBox>("GlmKey").Text);
                _store.SaveKey(ProviderKind.DeepSeek, Find<TextBox>("DeepSeekKey").Text);
            }
            Close(true);
        }
        catch (Exception exception)
        {
            Find<TextBlock>("SettingsStatus").Text = "保存失败：" + exception.Message;
        }
    }

    private static void Placeholder(TextBox box, bool saved) =>
        box.PlaceholderText = saved ? SavedPlaceholder : EmptyPlaceholder;

    private string BaiduHint(BaiduResolution resolution, bool canSave)
    {
        if (!canSave) return "当前无法保存密钥，请改用环境变量。";
        return resolution.Source switch
        {
            SecretSource.Environment => "密钥来自环境变量 " + SettingsStore.BaiduAppIdVariable + " / " + SettingsStore.BaiduSecretVariable + "。",
            SecretSource.WindowsDpapi or SecretSource.LinuxKeyring or SecretSource.ConfigFile
                => "密钥已保存（" + Location(resolution.Source) + "），留空即保持不变。",
            SecretSource.LegacyConfig
                => "密钥目前只在旧版 appsettings.json 里；点“保存”会迁移到 " + _store.CredentialsPath + "，更新程序不会再删掉它。",
            _ when !string.IsNullOrWhiteSpace(resolution.AppId) && _store.KeyringAvailable
                => "已保存 APPID；本次读不到钥匙串里的密钥，留空保存不会删除它，也可以直接重新填写。",
            _ when !string.IsNullOrWhiteSpace(resolution.AppId)
                => "已保存 APPID；未检测到系统钥匙串，填写密钥后会保存到 " + _store.CredentialsPath + "（重装不会丢）。",
            _ => "填写 APPID 和密钥后即可使用百度翻译。"
        };
    }

    private static string BaiduState(BaiduResolution resolution)
    {
        if (resolution.HasSecret) return "已保存（" + Location(resolution.Source) + "）";
        return string.IsNullOrWhiteSpace(resolution.AppId) ? "未配置" : "只有 APPID，缺少可用密钥";
    }

    private static string Describe(SecretResolution resolution) =>
        resolution.HasValue ? "已保存（" + Location(resolution.Source) + "）" : "未配置";

    private static string Location(SecretSource source) => source switch
    {
        SecretSource.Environment => "环境变量",
        SecretSource.WindowsDpapi => "Windows DPAPI",
        SecretSource.LinuxKeyring => "系统钥匙串",
        SecretSource.ConfigFile => "配置文件",
        SecretSource.LegacyConfig => "旧版 appsettings.json",
        _ => "未保存"
    };

    private T Find<T>(string name) where T : Control => this.FindControl<T>(name)
        ?? throw new InvalidOperationException($"Control '{name}' was not found.");
}
