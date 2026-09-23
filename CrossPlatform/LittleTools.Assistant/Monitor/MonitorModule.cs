using Avalonia.Controls;
using Avalonia.Threading;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// The AI usage monitor as a suite module: it owns the persisted state, the
/// provider settings, the two usage trackers, the refresh cycle and the HUD
/// window. It is the Linux counterpart of the Windows <c>MonitorController</c>
/// (AIUsageMonitor/Program.cs L226-L512) with the tray left to the suite host and
/// the settings dialog moved to <see cref="MonitorProviderSettingsWindow"/>.
///
/// Refresh behaviour is the Windows one: the 2 minute timer (L267-L270), the
/// 3.5 second startup delay (L272-L274), the per provider
/// enabled/unavailable/error branches (L315-L377), the Windows error wording
/// (L399-L415), the tracker pass (L365-L366) and the snapshot write (L369).
/// </summary>
internal sealed class MonitorModule : IDisposable
{
    /// <summary>Windows timer interval (Program.cs L268).</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    /// <summary>Windows startup timer (Program.cs L272): the first refresh waits for the desktop.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3.5);

    private readonly MonitorStore _store;
    private readonly MonitorDataService _service;
    private readonly MonitorUsageTracker _tracker;
    private readonly Func<DateTime> _now;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = RefreshInterval };
    private readonly DispatcherTimer _startupTimer = new() { Interval = StartupDelay };
    private readonly INotificationService _notifications;

    private ProviderSettings _providers;
    private UsageSnapshot _state;
    private MonitorWindow? _window;
    private bool _edgeHideEnabled;
    private bool _refreshing;
    private bool _disposed;

    public MonitorModule(INotificationService notifications, bool edgeHideEnabled,
        MonitorStore? store = null, MonitorDataService? service = null, Func<DateTime>? now = null)
    {
        _notifications = notifications;
        _edgeHideEnabled = edgeHideEnabled;
        _store = store ?? new MonitorStore();
        _service = service ?? MonitorDataServiceFactory.Create();
        _now = now ?? (() => DateTime.Now);
        _tracker = new MonitorUsageTracker(_store, _now);
        _providers = _store.LoadProviders();
        _state = _store.LoadSnapshot() ?? new UsageSnapshot();
        ApplyProviderVisibility();

        // Windows MonitorController ctor (Program.cs L249-L250).
        if (!_state.FiveHourRemaining.HasValue && !_state.WeeklyRemaining.HasValue)
            _state.CodexState = "点击刷新获取";
        if (string.IsNullOrEmpty(_state.DeepSeekBalance)) _state.DeepSeekState = "正在连接";

        _refreshTimer.Tick += async (_, _) => await RefreshNowAsync();
        _startupTimer.Tick += async (_, _) =>
        {
            _startupTimer.Stop();
            await RefreshNowAsync();
        };
    }

    /// <summary>Where the shared files live, for diagnostics.</summary>
    public string DataPath => _store.Directory;

    /// <summary>Files adopted from a Windows installation, if any.</summary>
    public IReadOnlyList<string> ImportedFrom => _store.ImportedFrom;

    /// <summary>Set when a file existed but could not be parsed.</summary>
    public string? LoadWarning => _store.LoadWarning;

    public bool IsRunning => _window is not null;

    public bool IsVisible => _window is { IsVisible: true };

    public bool IsRefreshing => _refreshing;

    public bool IsFixtureReplay => _service is { IsFixtureReplay: true };

    internal UsageSnapshot State => _state;

    internal ProviderSettings Providers => _providers;

    internal MonitorDataService Service => _service;

    internal MonitorStore Store => _store;

    internal MonitorWindow? Window => _window;

    /// <summary>Raised by the detail window's 退出 button; the suite host turns the module off.</summary>
    internal event Action? ExitRequested;

    /// <summary>Windows MonitorController.StartMonitor: window first, then the first refresh.</summary>
    public void Start()
    {
        if (_window is null)
        {
            _window = new MonitorWindow(this, _edgeHideEnabled);
            _window.ExitRequested += () => ExitRequested?.Invoke();
            _window.ShowWindow();
            _window.UpdateView(_state);
            _startupTimer.Start();
        }
        else
        {
            _window.ShowWindow();
        }
        _refreshTimer.Start();
    }

    public void Stop()
    {
        _refreshTimer.Stop();
        _startupTimer.Stop();
        if (_window is null) return;
        _window.ClosePermanently();
        _window = null;
    }

    public void SetEdgeHideEnabled(bool enabled)
    {
        _edgeHideEnabled = enabled;
        _window?.SetEdgeHideEnabled(enabled);
    }

    /// <summary>
    /// Windows MonitorController.RefreshNow (Program.cs L315-L377). Every provider
    /// is independent: one failing source never clears the others, and a failure
    /// keeps the last good values with the Windows wording.
    /// </summary>
    internal async Task RefreshNowAsync()
    {
        if (_refreshing || _disposed) return;
        _refreshing = true;
        _window?.SetRefreshing(true);
        try
        {
            ApplyProviderVisibility();
            var token = _cancellation.Token;
            if (!_providers.CodexEnabled)
            {
                _state.CodexState = "未启用";
                _state.FiveHourRemaining = null;
                _state.WeeklyRemaining = null;
                _state.FiveHourReset = null;
                _state.WeeklyReset = null;
                _state.PlanType = null;
                _state.CodexCredits = null;
            }
            else if (_service.Codex.Availability == CodexAvailability.None)
            {
                _state.CodexState = "Codex 未运行 · 显示上次数据";
            }
            else
            {
                try
                {
                    ApplyCodex(await _service.Codex.ReadAsync(token).ConfigureAwait(true));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _state.CodexState = MonitorLayout.FriendlyError(exception, "Codex 暂不可用 · 显示上次数据");
                }
            }

            if (!_providers.DeepSeekEnabled)
            {
                _state.DeepSeekState = "未启用";
                _state.DeepSeekBalance = null;
                _state.DeepSeekBreakdown = null;
                _state.DeepSeekCurrentBalance = null;
            }
            else
            {
                try
                {
                    ApplyDeepSeek(await _service.ReadDeepSeekAsync(
                        MonitorCredentials.Resolve(_providers, glm: false), token).ConfigureAwait(true));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _state.DeepSeekState = MonitorLayout.FriendlyError(exception, "DeepSeek 暂不可用");
                }
            }

            if (!_providers.GlmEnabled)
            {
                _state.GlmState = "未启用";
                _state.GlmFiveHourRemaining = null;
                _state.GlmWeeklyRemaining = null;
                _state.GlmMonthlyRemaining = null;
                _state.GlmFiveHourReset = null;
                _state.GlmWeeklyReset = null;
                _state.GlmMonthlyReset = null;
                _state.GlmPlanType = null;
                _state.GlmQuotaBreakdown = null;
                _state.GlmUpdatedAt = null;
                _state.GlmBalance = null;
                _state.GlmWalletTotal = null;
                _state.GlmTotalSpend = null;
                _state.GlmCurrencySymbol = null;
                _state.GlmWalletUpdatedAt = null;
            }
            else
            {
                try
                {
                    ApplyGlm(await _service.ReadGlmAsync(
                        MonitorCredentials.Resolve(_providers, glm: true), token).ConfigureAwait(true));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _state.GlmState = MonitorLayout.FriendlyError(exception, "GLM 暂不可用");
                }
            }

            if (_disposed) return;
            _tracker.Update(_state);
            _state.UpdatedAt = _now();
            _store.SaveSnapshot(_state);
            _window?.UpdateView(_state);
        }
        finally
        {
            _refreshing = false;
            if (!_disposed) _window?.SetRefreshing(false);
        }
    }

    /// <summary>Windows MonitorController.ApplyProviderVisibility (Program.cs L392-L397).</summary>
    private void ApplyProviderVisibility()
    {
        _state.CodexEnabled = _providers.CodexEnabled;
        _state.DeepSeekEnabled = _providers.DeepSeekEnabled;
        _state.GlmEnabled = _providers.GlmEnabled;
    }

    /// <summary>Windows MonitorController.ApplyCodex (Program.cs L417-L430).</summary>
    internal void ApplyCodex(CodexUsage usage)
    {
        _state.CodexState = "正常";
        _state.CodexUpdatedAt = _now();
        _state.PlanType = usage.PlanType;
        _state.CodexCredits = usage.Credits;
        _state.FiveHourRemaining = null;
        _state.WeeklyRemaining = null;
        _state.FiveHourReset = null;
        _state.WeeklyReset = null;
        MonitorLayout.ApplyRateWindow(_state, usage.Primary);
        MonitorLayout.ApplyRateWindow(_state, usage.Secondary);
    }

    /// <summary>Windows MonitorController.ApplyDeepSeek (Program.cs L448-L455).</summary>
    internal void ApplyDeepSeek(DeepSeekUsage usage)
    {
        _state.DeepSeekState = usage.Available ? "正常" : "余额不足";
        _state.DeepSeekBalance = usage.TotalDisplay;
        _state.DeepSeekBreakdown = usage.Breakdown;
        _state.DeepSeekCurrentBalance = usage.PrimaryBalance;
        _state.DeepSeekCurrencySymbol = usage.CurrencySymbol;
    }

    /// <summary>Windows MonitorController.ApplyGlm (Program.cs L457-L484).</summary>
    internal void ApplyGlm(GlmUsage usage)
    {
        var hasPlan = usage.FiveHour is not null || usage.Weekly is not null || usage.Monthly is not null;
        _state.GlmState = usage.QuotaRead && usage.WalletRead
            ? (hasPlan ? "正常" : "余额正常 · 无 Coding Plan")
            : usage.WalletRead
                ? "余额正常 · 套餐配额不可用"
                : hasPlan ? "套餐正常 · 账户余额不可用" : "无 Coding Plan · 账户余额不可用";
        if (usage.QuotaRead)
        {
            _state.GlmFiveHourRemaining = MonitorLayout.Remaining(usage.FiveHour);
            _state.GlmWeeklyRemaining = MonitorLayout.Remaining(usage.Weekly);
            _state.GlmMonthlyRemaining = MonitorLayout.Remaining(usage.Monthly);
            _state.GlmFiveHourReset = usage.FiveHour?.ResetsAt;
            _state.GlmWeeklyReset = usage.Weekly?.ResetsAt;
            _state.GlmMonthlyReset = usage.Monthly?.ResetsAt;
            _state.GlmPlanType = usage.Level;
            _state.GlmQuotaBreakdown = usage.Breakdown;
        }
        if (usage.WalletRead)
        {
            _state.GlmBalance = usage.Balance;
            _state.GlmWalletTotal = usage.WalletTotal;
            _state.GlmTotalSpend = usage.TotalSpend;
            _state.GlmCurrencySymbol = usage.CurrencySymbol;
            _state.GlmWalletUpdatedAt = _now();
        }
        _state.GlmUpdatedAt = _now();
    }

    /// <summary>Windows MonitorController.OpenProviderSettings (Program.cs L379-L390).</summary>
    internal async Task OpenProviderSettingsAsync(Window? owner)
    {
        var dialog = new MonitorProviderSettingsWindow(_providers);
        if (!await dialog.ShowDialogAsync(owner)) return;
        _providers = dialog.Result;
        _store.SaveProviders(_providers);
        ApplyProviderVisibility();
        _window?.UpdateView(_state);
        await RefreshNowAsync();
    }

    /// <summary>Windows MonitorController.Exit: here it means "turn this module off".</summary>
    internal void RequestExit() => ExitRequested?.Invoke();

    /// <summary>A tray balloon style notice, used for import warnings.</summary>
    internal void Notify(string title, string message) => _notifications.Show(title, message, 7000);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _startupTimer.Stop();
        _refreshTimer.Stop();
        Stop();
        _cancellation.Dispose();
        _service.Dispose();
    }
}
