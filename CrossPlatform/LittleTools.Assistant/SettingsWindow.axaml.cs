using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;

    public SettingsWindow() : this(new SettingsStore()) { }

    internal SettingsWindow(SettingsStore store)
    {
        _store = store;
        AvaloniaXamlLoader.Load(this);
        var settings = store.Current;
        Find<TextBox>("GlmEnvironment").Text = settings.GlmEnvironmentVariable;
        Find<TextBox>("DeepSeekEnvironment").Text = settings.DeepSeekEnvironmentVariable;
        Find<TextBlock>("SettingsStatus").Text = store.CanSaveKeys
            ? "检测到的 Key 状态：GLM " + Ready(ProviderKind.Glm) + "，DeepSeek " + Ready(ProviderKind.DeepSeek)
            : "Linux 请安装 libsecret-tools，或配置 ZHIPUAI_API_KEY / DEEPSEEK_API_KEY 环境变量。";
        Find<TextBox>("GlmKey").IsEnabled = store.CanSaveKeys;
        Find<TextBox>("DeepSeekKey").IsEnabled = store.CanSaveKeys;
        Find<Button>("CancelButton").Click += (_, _) => Close(false);
        Find<Button>("SaveButton").Click += (_, _) => Save();
    }

    private void Save()
    {
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

    private string Ready(ProviderKind provider) => string.IsNullOrWhiteSpace(_store.ResolveKey(provider)) ? "未配置" : "已就绪";

    private T Find<T>(string name) where T : Control => this.FindControl<T>(name)
        ?? throw new InvalidOperationException($"Control '{name}' was not found.");
}
