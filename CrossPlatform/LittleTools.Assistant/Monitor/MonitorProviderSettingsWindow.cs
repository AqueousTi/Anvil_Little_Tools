using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// The provider settings dialog, ported from the Windows
/// <c>ProviderSettingsWindow</c> (AIUsageMonitor/Program.cs L514-L589): the same
/// 430x470 shell, the same order of the Codex/DeepSeek/GLM switches, the same
/// "environment variable or manual key" pair per API provider, the same
/// <c>UpdateInputs</c> enable rule (L560-L564) and the same "only replace the
/// stored key when a new one is typed" save rule (L566-L582).
///
/// Linux difference (deliberate): Windows protects a manual key with current-user
/// DPAPI and stores the blob in <c>providers.json</c>. Linux stores it in the
/// suite's platform secret store instead (GNOME keyring, then the 0600
/// credentials.json the assistant's own keys use), so one key serves both modules;
/// the <c>GlmProtectedKey</c>/<c>DeepSeekProtectedKey</c> fields are preserved
/// unchanged so the file stays Windows readable. A Windows DPAPI blob cannot be
/// decrypted here, which the dialog says out loud instead of pretending the
/// provider is configured.
/// </summary>
internal sealed class MonitorProviderSettingsWindow : Window
{
    private readonly CheckBox _codexEnabled;
    private readonly CheckBox _deepSeekEnabled;
    private readonly CheckBox _deepSeekEnvironment;
    private readonly TextBox _deepSeekEnvironmentName;
    private readonly TextBox _deepSeekKey;
    private readonly CheckBox _glmEnabled;
    private readonly CheckBox _glmEnvironment;
    private readonly TextBox _glmEnvironmentName;
    private readonly TextBox _glmKey;
    private readonly TextBlock _hint;
    private readonly ProviderSettings _original;
    private bool _accepted;

    public MonitorProviderSettingsWindow(ProviderSettings settings)
    {
        _original = settings;
        Result = settings.Clone();

        Title = "Little Tools · 供应商设置";
        Width = MonitorLayout.SettingsWidth;
        // Windows pins the dialog at 430x470. The wider Linux font wraps the two hint
        // paragraphs onto more lines and the last hint is an addition, which pushed the
        // 保存/取消 row past the bottom edge of the fixed height (the buttons were
        // half outside the window and unreachable). The width stays the Windows one and
        // the height now grows to the content, with the Windows height as the floor.
        SizeToContent = SizeToContent.Height;
        MinHeight = MonitorLayout.SettingsHeight;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TransparencyLevelHint = GlassSurface.TransparencyLevels;
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;

        var panel = new StackPanel();
        var heading = MonitorTheme.Label("TOKEN 供应商", 14, MonitorTheme.PrimaryText, bold: true);
        panel.Children.Add(heading);
        panel.Children.Add(Hint("API Key 只在这里配置；手动输入会保存到本机密钥环，失败时退化为仅本人可读的凭据文件。"));

        _codexEnabled = MonitorTheme.Check("监控 Codex（未运行时保留上次余量）", settings.CodexEnabled);
        _codexEnabled.Margin = new Thickness(0, 18, 0, 8);
        panel.Children.Add(_codexEnabled);

        _deepSeekEnabled = MonitorTheme.Check("监控 DeepSeek", settings.DeepSeekEnabled);
        panel.Children.Add(_deepSeekEnabled);
        _deepSeekEnvironment = MonitorTheme.Check("从环境变量读取", !MonitorCredentials.IsManual(settings.DeepSeekSource));
        _deepSeekEnvironment.Margin = new Thickness(18, 7, 0, 4);
        panel.Children.Add(_deepSeekEnvironment);
        _deepSeekEnvironmentName = MonitorTheme.Input(settings.DeepSeekEnvironment ?? "DEEPSEEK_API_KEY");
        _deepSeekEnvironmentName.Margin = new Thickness(18, 0, 0, 5);
        panel.Children.Add(_deepSeekEnvironmentName);
        _deepSeekKey = MonitorTheme.Password("手动 API Key");
        _deepSeekKey.Margin = new Thickness(18, 0, 0, 14);
        panel.Children.Add(_deepSeekKey);

        _glmEnabled = MonitorTheme.Check("监控 GLM（国内智谱开放平台）", settings.GlmEnabled);
        panel.Children.Add(_glmEnabled);
        _glmEnvironment = MonitorTheme.Check("从环境变量读取", !MonitorCredentials.IsManual(settings.GlmSource));
        _glmEnvironment.Margin = new Thickness(18, 7, 0, 4);
        panel.Children.Add(_glmEnvironment);
        _glmEnvironmentName = MonitorTheme.Input(settings.GlmEnvironment ?? "ZHIPUAI_API_KEY");
        _glmEnvironmentName.Margin = new Thickness(18, 0, 0, 5);
        panel.Children.Add(_glmEnvironmentName);
        _glmKey = MonitorTheme.Password("手动 API Key");
        _glmKey.Margin = new Thickness(18, 0, 0, 12);
        panel.Children.Add(_glmKey);
        panel.Children.Add(Hint("GLM：读取普通 API 账户余额；若已订阅 Coding Plan，同时读取 5 小时、周额度与 MCP 月额度。"));
        panel.Children.Add(Hint("已保存的 Key 不会显示；留空即保持原 Key。"));

        _hint = Hint(string.Empty);
        _hint.Foreground = MonitorTheme.WarningText;
        panel.Children.Add(_hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 17, 0, 0)
        };
        var cancel = MonitorTheme.SmallButton("取消", 66);
        cancel.Height = 29;
        cancel.Click += (_, _) => Close(false);
        buttons.Children.Add(cancel);
        var save = MonitorTheme.SmallButton("保存", 72);
        save.Height = 29;
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) => Save();
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        Content = new Border
        {
            CornerRadius = new CornerRadius(17),
            Background = MonitorTheme.SettingsBackground,
            BorderBrush = MonitorTheme.SettingsBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20),
            BoxShadow = MonitorTheme.ShellShadow,
            Child = panel
        };

        PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            // Windows only starts a drag from the shell itself, never from a control.
            if (args.Source is not Border and not TextBlock) return;
            try { BeginMoveDrag(args); } catch { }
        };

        UpdateInputs();
        _deepSeekEnvironment.IsCheckedChanged += (_, _) => UpdateInputs();
        _glmEnvironment.IsCheckedChanged += (_, _) => UpdateInputs();
        UpdateWindowsKeyHint();
    }

    /// <summary>The settings the user accepted; only valid after a <c>true</c> result.</summary>
    public ProviderSettings Result { get; private set; }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontFamily = MonitorTheme.UiFont,
        FontSize = 10,
        Foreground = MonitorTheme.MetaText,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 0)
    };

    /// <summary>Windows UpdateInputs (Program.cs L560-L564).</summary>
    private void UpdateInputs()
    {
        _deepSeekEnvironmentName.IsEnabled = _deepSeekEnvironment.IsChecked == true;
        _deepSeekKey.IsEnabled = !_deepSeekEnvironmentName.IsEnabled;
        _glmEnvironmentName.IsEnabled = _glmEnvironment.IsChecked == true;
        _glmKey.IsEnabled = !_glmEnvironmentName.IsEnabled;
    }

    /// <summary>
    /// Tells the user when the only stored manual key is a Windows DPAPI blob this
    /// platform cannot read, because otherwise the provider would look configured
    /// and silently never resolve.
    /// </summary>
    private void UpdateWindowsKeyHint()
    {
        var deepSeek = MonitorCredentials.HasUndecryptableWindowsKey(_original, glm: false)
            && _deepSeekEnvironment.IsChecked != true;
        var glm = MonitorCredentials.HasUndecryptableWindowsKey(_original, glm: true)
            && _glmEnvironment.IsChecked != true;
        _hint.Text = deepSeek || glm
            ? "检测到 Windows 保存的手动 Key，本机无法解密；请重新输入一次。"
            : string.Empty;
        _hint.IsVisible = _hint.Text.Length > 0;
    }

    /// <summary>Windows Save (Program.cs L566-L582).</summary>
    private void Save()
    {
        var value = new ProviderSettings
        {
            CodexEnabled = _codexEnabled.IsChecked == true,
            DeepSeekEnabled = _deepSeekEnabled.IsChecked == true,
            DeepSeekSource = _deepSeekEnvironment.IsChecked == true ? "Environment" : "Manual",
            DeepSeekEnvironment = (_deepSeekEnvironmentName.Text ?? string.Empty).Trim(),
            DeepSeekProtectedKey = _original.DeepSeekProtectedKey,
            GlmEnabled = _glmEnabled.IsChecked == true,
            GlmSource = _glmEnvironment.IsChecked == true ? "Environment" : "Manual",
            GlmEnvironment = (_glmEnvironmentName.Text ?? string.Empty).Trim(),
            GlmProtectedKey = _original.GlmProtectedKey
        };

        var deepSeekKey = _deepSeekKey.Text ?? string.Empty;
        var glmKey = _glmKey.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(deepSeekKey)) MonitorCredentials.SaveManualKey(glm: false, deepSeekKey);
        if (!string.IsNullOrWhiteSpace(glmKey)) MonitorCredentials.SaveManualKey(glm: true, glmKey);

        Result = value;
        _accepted = true;
        // Avalonia completes ShowDialog<TResult> with the value passed to
        // Close(result); a plain Close() would resolve to default(bool) = false and
        // the caller would treat a saved dialog as cancelled (which is exactly the
        // bug the real-machine run caught).
        Close(true);
    }

    /// <summary>
    /// Shows the dialog. Avalonia's <c>ShowDialog&lt;bool&gt;</c> needs an owner, so
    /// a headless or owner-less caller gets the same modal result through the
    /// <c>Closed</c> event.
    /// </summary>
    internal async Task<bool> ShowDialogAsync(Window? owner)
    {
        if (owner is not null)
        {
            // The accepted flag is authoritative rather than the modal result, so a
            // window closed by the window manager cannot turn a save into a cancel.
            await ShowDialog<bool>(owner);
            return _accepted;
        }
        var completion = new TaskCompletionSource<bool>();
        Closed += (_, _) => completion.TrySetResult(_accepted);
        Show();
        return await completion.Task;
    }

    /// <summary>True when the user pressed 保存 and did not cancel.</summary>
    internal bool Accepted => _accepted;

    /// <summary>
    /// Smoke seam: runs the same save the 保存 button runs (including the modal
    /// result plumbing) so a render run can prove the settings really reach the
    /// store instead of only drawing the dialog.
    /// </summary>
    internal bool SaveForSmoke() => SaveAndReport();

    private bool SaveAndReport()
    {
        Save();
        return _accepted;
    }

    /// <summary>
    /// The dialog's realised state, for the render smoke: which switch is on, which
    /// input is enabled, and whether the "re-enter the key" hint is showing.
    /// </summary>
    internal string DescribeForSmoke() =>
        "codexEnabled=" + (_codexEnabled.IsChecked == true)
        + ",deepSeekEnabled=" + (_deepSeekEnabled.IsChecked == true)
        + ",deepSeekEnv=" + (_deepSeekEnvironment.IsChecked == true)
        + ",deepSeekEnvName=" + _deepSeekEnvironmentName.Text
        + ",deepSeekEnvEnabled=" + _deepSeekEnvironmentName.IsEnabled
        + ",deepSeekKeyEnabled=" + _deepSeekKey.IsEnabled
        + ",glmEnabled=" + (_glmEnabled.IsChecked == true)
        + ",glmEnv=" + (_glmEnvironment.IsChecked == true)
        + ",glmEnvName=" + _glmEnvironmentName.Text
        + ",glmKeyEnabled=" + _glmKey.IsEnabled
        + ",hint=" + (_hint.IsVisible ? _hint.Text : "none")
        // Height is NaN while SizeToContent is in charge, so the realised bounds are
        // reported instead.
        + ",size=" + Bounds.Width.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x"
        + Bounds.Height.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
