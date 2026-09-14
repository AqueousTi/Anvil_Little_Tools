using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using LittleTools.Common;

namespace AIUsageMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "LittleTools.AIUsageMonitor.SingleInstance", out created))
            {
                if (!created) return;
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var application = new Application();
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                bool managed = args != null && Array.IndexOf(args, "--managed") >= 0;
                var controller = new MonitorController(application.Dispatcher, !managed);
                application.Run(controller.Window);
                controller.Dispose();
            }
        }
    }

    internal sealed class UsageSnapshot
    {
        public bool CodexEnabled = true;
        public bool DeepSeekEnabled = true;
        public bool GlmEnabled;
        public string CodexState;
        public double? FiveHourRemaining;
        public double? WeeklyRemaining;
        public DateTime? FiveHourReset;
        public DateTime? WeeklyReset;
        public string PlanType;
        public string CodexCredits;
        public DateTime? CodexUpdatedAt;
        public string DeepSeekState;
        public string DeepSeekBalance;
        public string DeepSeekBreakdown;
        public double? DeepSeekCurrentBalance;
        public string DeepSeekCurrencySymbol;
        public string GlmState;
        public double? GlmBalance;
        public double? GlmWalletTotal;
        public double? GlmTotalSpend;
        public string GlmCurrencySymbol;
        public double? GlmFiveHourRemaining;
        public double? GlmWeeklyRemaining;
        public double? GlmMonthlyRemaining;
        public DateTime? GlmFiveHourReset;
        public DateTime? GlmWeeklyReset;
        public DateTime? GlmMonthlyReset;
        public string GlmPlanType;
        public string GlmQuotaBreakdown;
        public DateTime? GlmUpdatedAt;
        public DateTime? GlmWalletUpdatedAt;
        public double? TodayGlmSpend;
        public double? WeeklyGlmSpend;
        public double? MonthlyGlmSpend;
        public DateTime? GlmTrackingStart;
        public DateTime? GlmWeekTrackingStart;
        public DateTime? GlmMonthTrackingStart;
        public List<UsagePoint> GlmTodayPoints = new List<UsagePoint>();
        public List<UsagePoint> GlmWeekPoints = new List<UsagePoint>();
        public List<UsagePoint> GlmMonthPoints = new List<UsagePoint>();
        public double? TodayDeepSeekSpend;
        public double? WeeklyDeepSeekSpend;
        public double? MonthlyDeepSeekSpend;
        public DateTime? DeepSeekTrackingStart;
        public DateTime? DeepSeekWeekTrackingStart;
        public DateTime? DeepSeekMonthTrackingStart;
        public List<UsagePoint> DeepSeekTodayPoints = new List<UsagePoint>();
        public List<UsagePoint> DeepSeekWeekPoints = new List<UsagePoint>();
        public List<UsagePoint> DeepSeekMonthPoints = new List<UsagePoint>();
        public DateTime? UpdatedAt;
    }

    internal sealed class ProviderSettings
    {
        public bool CodexEnabled = true;
        public bool DeepSeekEnabled = true;
        public bool GlmEnabled;
        public string DeepSeekSource = "Environment";
        public string DeepSeekEnvironment = "DEEPSEEK_API_KEY";
        public string DeepSeekProtectedKey;
        public string GlmSource = "Environment";
        public string GlmEnvironment = "ZHIPUAI_API_KEY";
        public string GlmProtectedKey;
    }

    internal static class ProviderSettingsStore
    {
        private static string PathValue { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "AIUsageMonitor", "providers.json"); } }

        public static ProviderSettings Load()
        {
            try
            {
                if (File.Exists(PathValue)) return new JavaScriptSerializer().Deserialize<ProviderSettings>(File.ReadAllText(PathValue, Encoding.UTF8)) ?? new ProviderSettings();
            }
            catch { }
            return new ProviderSettings();
        }

        public static void Save(ProviderSettings settings)
        {
            AtomicFile.WriteUtf8(PathValue, new JavaScriptSerializer().Serialize(settings));
        }

        public static string ResolveKey(string source, string environment, string protectedValue)
        {
            if (string.Equals(source, "Manual", StringComparison.OrdinalIgnoreCase)) return Unprotect(protectedValue);
            return Environment.GetEnvironmentVariable(string.IsNullOrWhiteSpace(environment) ? "" : environment.Trim());
        }

        public static string ResolveGlmKey(string source, string environment, string protectedValue)
        {
            string value = ResolveKey(source, environment, protectedValue);
            if (!string.IsNullOrWhiteSpace(value) || string.Equals(source, "Manual", StringComparison.OrdinalIgnoreCase)) return value;
            string[] aliases = { "ZAI_CODING_CN_API_KEY", "ZHIPUAI_API_KEY", "ZHIPU_API_KEY", "GLM_API_KEY", "BIGMODEL_API_KEY" };
            foreach (string alias in aliases)
            {
                value = Environment.GetEnvironmentVariable(alias);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

        public static string Protect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            byte[] plain = Encoding.UTF8.GetBytes(value.Trim());
            return Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }

        private static string Unprotect(string value)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
            }
            catch { return null; }
        }
    }

    internal sealed class UsagePoint
    {
        public DateTime Timestamp;
        public double Value;
    }

    internal static class UsageSnapshotCache
    {
        private static string CachePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LittleTools", "AIUsageMonitor", "snapshot.json"); }
        }

        public static UsageSnapshot Load()
        {
            try
            {
                if (!File.Exists(CachePath)) return null;
                UsageSnapshot state = new JavaScriptSerializer().Deserialize<UsageSnapshot>(File.ReadAllText(CachePath, Encoding.UTF8));
                if (state == null) return null;
                if (state.DeepSeekTodayPoints == null) state.DeepSeekTodayPoints = new List<UsagePoint>();
                if (state.DeepSeekWeekPoints == null) state.DeepSeekWeekPoints = new List<UsagePoint>();
                if (state.DeepSeekMonthPoints == null) state.DeepSeekMonthPoints = new List<UsagePoint>();
                if (state.GlmTodayPoints == null) state.GlmTodayPoints = new List<UsagePoint>();
                if (state.GlmWeekPoints == null) state.GlmWeekPoints = new List<UsagePoint>();
                if (state.GlmMonthPoints == null) state.GlmMonthPoints = new List<UsagePoint>();
                return state;
            }
            catch { return null; }
        }

        public static void Save(UsageSnapshot state)
        {
            try
            {
                AtomicFile.WriteUtf8(CachePath, new JavaScriptSerializer().Serialize(state));
            }
            catch { }
        }
    }

    internal sealed class RateWindow
    {
        public double UsedPercent;
        public int WindowDurationMins;
        public DateTime? ResetsAt;
    }

    internal sealed class CodexUsage
    {
        public RateWindow Primary;
        public RateWindow Secondary;
        public string PlanType;
        public string Credits;
    }

    internal sealed class MonitorController : IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly DispatcherTimer timer;
        private readonly DispatcherTimer startupTimer;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ContextMenuStrip trayMenu;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly UsageSnapshot state;
        private ProviderSettings providers;
        private readonly DailyUsageTracker dailyTracker = new DailyUsageTracker();
        private readonly GlmUsageTracker glmTracker = new GlmUsageTracker();
        private bool refreshing;
        private bool disposed;

        public MonitorWindow Window { get; private set; }

        public MonitorController(Dispatcher dispatcher, bool showTray)
        {
            this.dispatcher = dispatcher;
            state = UsageSnapshotCache.Load() ?? new UsageSnapshot();
            providers = ProviderSettingsStore.Load();
            ApplyProviderVisibility();
            if (!state.FiveHourRemaining.HasValue && !state.WeeklyRemaining.HasValue) state.CodexState = "点击刷新获取";
            if (string.IsNullOrEmpty(state.DeepSeekBalance)) state.DeepSeekState = "正在连接";
            Window = new MonitorWindow(state);
            Window.RefreshRequested += delegate { RefreshNow(); };
            Window.SettingsRequested += delegate { OpenProviderSettings(); };
            Window.ExitRequested += delegate { Exit(); };

            if (showTray)
            {
                trayMenu = BuildTrayMenu();
                tray = new Forms.NotifyIcon();
                tray.Icon = System.Drawing.SystemIcons.Information;
                tray.Text = "AI Usage Monitor";
                tray.Visible = true;
                tray.DoubleClick += delegate { ToggleWindow(); };
                tray.ContextMenuStrip = trayMenu;
            }

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMinutes(2);
            timer.Tick += delegate { RefreshNow(); };
            timer.Start();

            startupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            startupTimer.Tick += delegate { startupTimer.Stop(); RefreshNow(); };
            Window.Loaded += delegate { startupTimer.Start(); };
        }

        private Forms.ContextMenuStrip BuildTrayMenu()
        {
            var menu = new Forms.ContextMenuStrip();
            var toggle = new Forms.ToolStripMenuItem("显示 / 隐藏");
            toggle.Click += delegate { dispatcher.BeginInvoke(new Action(ToggleWindow)); };
            menu.Items.Add(toggle);

            var clickThrough = new Forms.ToolStripMenuItem("鼠标穿透");
            clickThrough.CheckOnClick = true;
            clickThrough.CheckedChanged += delegate
            {
                bool enabled = clickThrough.Checked;
                dispatcher.BeginInvoke(new Action(delegate { Window.SetClickThrough(enabled); }));
            };
            menu.Items.Add(clickThrough);

            var refresh = new Forms.ToolStripMenuItem("立即刷新");
            refresh.Click += delegate { dispatcher.BeginInvoke(new Action(RefreshNow)); };
            menu.Items.Add(refresh);
            menu.Items.Add(new Forms.ToolStripSeparator());

            var exit = new Forms.ToolStripMenuItem("退出");
            exit.Click += delegate { dispatcher.BeginInvoke(new Action(Exit)); };
            menu.Items.Add(exit);
            return menu;
        }

        private void ToggleWindow()
        {
            if (Window.IsVisible)
                Window.Hide();
            else
            {
                Window.Show();
                Window.Activate();
            }
        }

        private async void RefreshNow()
        {
            if (refreshing || disposed) return;
            refreshing = true;
            Window.SetRefreshing(true);
            try
            {
                ApplyProviderVisibility();
                if (!providers.CodexEnabled)
                {
                    state.CodexState = "未启用"; state.FiveHourRemaining = null; state.WeeklyRemaining = null;
                    state.FiveHourReset = null; state.WeeklyReset = null; state.PlanType = null; state.CodexCredits = null;
                }
                else if (!CodexProvider.IsDesktopRunning()) state.CodexState = "Codex 未运行 · 显示上次数据";
                else
                {
                    try { ApplyCodex(await CodexProvider.ReadAsync(cancellation.Token)); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception exception) { state.CodexState = FriendlyError(exception, "Codex 暂不可用 · 显示上次数据"); }
                }

                if (!providers.DeepSeekEnabled)
                {
                    state.DeepSeekState = "未启用"; state.DeepSeekBalance = null; state.DeepSeekBreakdown = null; state.DeepSeekCurrentBalance = null;
                }
                else
                {
                    string key = ProviderSettingsStore.ResolveKey(providers.DeepSeekSource, providers.DeepSeekEnvironment, providers.DeepSeekProtectedKey);
                    try { ApplyDeepSeek(await DeepSeekProvider.ReadAsync(key, cancellation.Token)); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception exception) { state.DeepSeekState = FriendlyError(exception, "DeepSeek 暂不可用"); }
                }

                if (!providers.GlmEnabled)
                {
                    state.GlmState = "未启用"; state.GlmFiveHourRemaining = null; state.GlmWeeklyRemaining = null;
                    state.GlmMonthlyRemaining = null; state.GlmFiveHourReset = null; state.GlmWeeklyReset = null;
                    state.GlmMonthlyReset = null; state.GlmPlanType = null; state.GlmQuotaBreakdown = null; state.GlmUpdatedAt = null;
                    state.GlmBalance = null; state.GlmWalletTotal = null; state.GlmTotalSpend = null; state.GlmCurrencySymbol = null;
                    state.GlmWalletUpdatedAt = null;
                }
                else
                {
                    string key = ProviderSettingsStore.ResolveGlmKey(providers.GlmSource, providers.GlmEnvironment, providers.GlmProtectedKey);
                    try { ApplyGlm(await GlmProvider.ReadAsync(key, cancellation.Token)); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception exception) { state.GlmState = FriendlyError(exception, "GLM 暂不可用"); }
                }

                if (disposed) return;
                dailyTracker.Update(state);
                glmTracker.Update(state);

                state.UpdatedAt = DateTime.Now;
                UsageSnapshotCache.Save(state);
                Window.UpdateView(state);
            }
            finally
            {
                refreshing = false;
                if (!disposed) Window.SetRefreshing(false);
            }
        }

        private void OpenProviderSettings()
        {
            var dialog = new ProviderSettingsWindow(providers) { Owner = Window };
            if (dialog.ShowDialog() == true)
            {
                providers = dialog.Result;
                ProviderSettingsStore.Save(providers);
                ApplyProviderVisibility();
                Window.UpdateView(state);
                RefreshNow();
            }
        }

        private void ApplyProviderVisibility()
        {
            state.CodexEnabled = providers.CodexEnabled;
            state.DeepSeekEnabled = providers.DeepSeekEnabled;
            state.GlmEnabled = providers.GlmEnabled;
        }

        private static string FriendlyError(Exception exception, string fallback)
        {
            string message = exception.Message ?? "";
            if (message.IndexOf("authentication required", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0)
                return "需要 Codex 登录";
            if (message.IndexOf("authentication failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                return "API Key 无效";
            if (message.IndexOf("no coding plan quota", StringComparison.OrdinalIgnoreCase) >= 0)
                return "无 Coding Plan 配额";
            if (message.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("environment", StringComparison.OrdinalIgnoreCase) >= 0)
                return "未配置 API Key";
            if (message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                return "连接超时";
            return fallback;
        }

        private void ApplyCodex(CodexUsage usage)
        {
            state.CodexState = "正常";
            state.CodexUpdatedAt = DateTime.Now;
            state.PlanType = usage.PlanType;
            state.CodexCredits = usage.Credits;
            state.FiveHourRemaining = null;
            state.WeeklyRemaining = null;
            state.FiveHourReset = null;
            state.WeeklyReset = null;

            ApplyRateWindow(usage.Primary);
            ApplyRateWindow(usage.Secondary);
        }

        private void ApplyRateWindow(RateWindow window)
        {
            if (window == null) return;
            double remaining = Math.Max(0, Math.Min(100, 100 - window.UsedPercent));
            if (window.WindowDurationMins <= 360)
            {
                state.FiveHourRemaining = remaining;
                state.FiveHourReset = window.ResetsAt;
            }
            else
            {
                state.WeeklyRemaining = remaining;
                state.WeeklyReset = window.ResetsAt;
            }
        }

        private void ApplyDeepSeek(DeepSeekUsage usage)
        {
            state.DeepSeekState = usage.Available ? "正常" : "余额不足";
            state.DeepSeekBalance = usage.TotalDisplay;
            state.DeepSeekBreakdown = usage.Breakdown;
            state.DeepSeekCurrentBalance = usage.PrimaryBalance;
            state.DeepSeekCurrencySymbol = usage.CurrencySymbol;
        }

        private void ApplyGlm(GlmUsage usage)
        {
            bool hasPlan = usage.FiveHour != null || usage.Weekly != null || usage.Monthly != null;
            state.GlmState = usage.QuotaRead && usage.WalletRead
                ? (hasPlan ? "正常" : "余额正常 · 无 Coding Plan")
                : (usage.WalletRead ? "余额正常 · 套餐配额不可用" :
                    (hasPlan ? "套餐正常 · 账户余额不可用" : "无 Coding Plan · 账户余额不可用"));
            if (usage.QuotaRead)
            {
                state.GlmFiveHourRemaining = Remaining(usage.FiveHour);
                state.GlmWeeklyRemaining = Remaining(usage.Weekly);
                state.GlmMonthlyRemaining = Remaining(usage.Monthly);
                state.GlmFiveHourReset = usage.FiveHour == null ? null : usage.FiveHour.ResetsAt;
                state.GlmWeeklyReset = usage.Weekly == null ? null : usage.Weekly.ResetsAt;
                state.GlmMonthlyReset = usage.Monthly == null ? null : usage.Monthly.ResetsAt;
                state.GlmPlanType = usage.Level;
                state.GlmQuotaBreakdown = usage.Breakdown;
            }
            if (usage.WalletRead)
            {
                state.GlmBalance = usage.Balance;
                state.GlmWalletTotal = usage.WalletTotal;
                state.GlmTotalSpend = usage.TotalSpend;
                state.GlmCurrencySymbol = usage.CurrencySymbol;
                state.GlmWalletUpdatedAt = DateTime.Now;
            }
            state.GlmUpdatedAt = DateTime.Now;
        }

        private static double? Remaining(RateWindow window)
        {
            return window == null ? (double?)null : Math.Max(0, Math.Min(100, 100 - window.UsedPercent));
        }

        private void Exit()
        {
            Dispose();
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            startupTimer.Stop();
            timer.Stop();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
            }
            cancellation.Dispose();
        }
    }

    internal sealed class ProviderSettingsWindow : Window
    {
        private readonly CheckBox codexEnabled;
        private readonly CheckBox deepEnabled;
        private readonly CheckBox deepEnvironment;
        private readonly TextBox deepEnvironmentName;
        private readonly PasswordBox deepKey;
        private readonly CheckBox glmEnabled;
        private readonly CheckBox glmEnvironment;
        private readonly TextBox glmEnvironmentName;
        private readonly PasswordBox glmKey;
        private readonly ProviderSettings original;
        public ProviderSettings Result { get; private set; }

        public ProviderSettingsWindow(ProviderSettings settings)
        {
            original = settings;
            Title = "Little Tools · 供应商设置";
            Width = 430; Height = 470; AllowsTransparency = true; Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var shell = new Border { CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(Color.FromArgb(235, 17, 20, 27)), BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), BorderThickness = new Thickness(1), Padding = new Thickness(20) };
            var panel = new StackPanel();
            var title = ProviderText("TOKEN 供应商", 14, Brushes.White); title.FontWeight = FontWeights.SemiBold; panel.Children.Add(title);
            panel.Children.Add(ProviderText("API Key 只在这里配置；手动输入会使用 Windows 用户级加密保存。", 10, new SolidColorBrush(Color.FromArgb(145,255,255,255))));
            codexEnabled = ProviderCheck("监控 Codex（未运行时保留上次余量）", settings.CodexEnabled); codexEnabled.Margin = new Thickness(0, 18, 0, 8); panel.Children.Add(codexEnabled);

            deepEnabled = ProviderCheck("监控 DeepSeek", settings.DeepSeekEnabled); panel.Children.Add(deepEnabled);
            deepEnvironment = ProviderCheck("从环境变量读取", settings.DeepSeekSource != "Manual"); deepEnvironment.Margin = new Thickness(18, 7, 0, 4); panel.Children.Add(deepEnvironment);
            deepEnvironmentName = ProviderInput(settings.DeepSeekEnvironment ?? "DEEPSEEK_API_KEY"); deepEnvironmentName.Margin = new Thickness(18, 0, 0, 5); panel.Children.Add(deepEnvironmentName);
            deepKey = ProviderPassword(); deepKey.Margin = new Thickness(18, 0, 0, 14); deepKey.ToolTip = string.IsNullOrEmpty(settings.DeepSeekProtectedKey) ? "手动 API Key" : "已保存；留空保持原 Key"; panel.Children.Add(deepKey);

            glmEnabled = ProviderCheck("监控 GLM（国内智谱开放平台）", settings.GlmEnabled); panel.Children.Add(glmEnabled);
            glmEnvironment = ProviderCheck("从环境变量读取", settings.GlmSource != "Manual"); glmEnvironment.Margin = new Thickness(18, 7, 0, 4); panel.Children.Add(glmEnvironment);
            glmEnvironmentName = ProviderInput(settings.GlmEnvironment ?? "ZHIPUAI_API_KEY"); glmEnvironmentName.Margin = new Thickness(18, 0, 0, 5); panel.Children.Add(glmEnvironmentName);
            glmKey = ProviderPassword(); glmKey.Margin = new Thickness(18, 0, 0, 12); glmKey.ToolTip = string.IsNullOrEmpty(settings.GlmProtectedKey) ? "手动 API Key" : "已保存；留空保持原 Key"; panel.Children.Add(glmKey);
            panel.Children.Add(ProviderText("GLM：读取普通 API 账户余额；若已订阅 Coding Plan，同时读取 5 小时、周额度与 MCP 月额度。", 9.5, new SolidColorBrush(Color.FromArgb(125,255,255,255))));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 17, 0, 0) };
            var cancel = ProviderButton("取消", 66); cancel.Click += delegate { DialogResult = false; }; buttons.Children.Add(cancel);
            var save = ProviderButton("保存", 72); save.Margin = new Thickness(8,0,0,0); save.Click += Save; buttons.Children.Add(save); panel.Children.Add(buttons);
            shell.Child = panel; Content = shell;
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args) { if (args.OriginalSource is Border || args.OriginalSource is TextBlock) try { DragMove(); } catch { } };
            UpdateInputs(); deepEnvironment.Checked += delegate { UpdateInputs(); }; deepEnvironment.Unchecked += delegate { UpdateInputs(); }; glmEnvironment.Checked += delegate { UpdateInputs(); }; glmEnvironment.Unchecked += delegate { UpdateInputs(); };
        }

        private void UpdateInputs()
        {
            deepEnvironmentName.IsEnabled = deepEnvironment.IsChecked == true; deepKey.IsEnabled = !deepEnvironmentName.IsEnabled;
            glmEnvironmentName.IsEnabled = glmEnvironment.IsChecked == true; glmKey.IsEnabled = !glmEnvironmentName.IsEnabled;
        }

        private void Save(object sender, RoutedEventArgs args)
        {
            var value = new ProviderSettings {
                CodexEnabled = codexEnabled.IsChecked == true,
                DeepSeekEnabled = deepEnabled.IsChecked == true,
                DeepSeekSource = deepEnvironment.IsChecked == true ? "Environment" : "Manual",
                DeepSeekEnvironment = (deepEnvironmentName.Text ?? "").Trim(),
                DeepSeekProtectedKey = original.DeepSeekProtectedKey,
                GlmEnabled = glmEnabled.IsChecked == true,
                GlmSource = glmEnvironment.IsChecked == true ? "Environment" : "Manual",
                GlmEnvironment = (glmEnvironmentName.Text ?? "").Trim(),
                GlmProtectedKey = original.GlmProtectedKey
            };
            if (!string.IsNullOrWhiteSpace(deepKey.Password)) value.DeepSeekProtectedKey = ProviderSettingsStore.Protect(deepKey.Password);
            if (!string.IsNullOrWhiteSpace(glmKey.Password)) value.GlmProtectedKey = ProviderSettingsStore.Protect(glmKey.Password);
            Result = value; DialogResult = true;
        }

        private static CheckBox ProviderCheck(string text, bool value) { return new CheckBox { Content = text, IsChecked = value, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 11 }; }
        private static TextBlock ProviderText(string text, double size, Brush brush) { return new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, Foreground = brush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,3,0,0) }; }
        private static TextBox ProviderInput(string text) { return new TextBox { Text = text, Height = 30, Padding = new Thickness(8,4,8,4), Foreground = Brushes.White, CaretBrush = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(35,255,255,255)), BorderBrush = new SolidColorBrush(Color.FromArgb(45,255,255,255)) }; }
        private static PasswordBox ProviderPassword() { return new PasswordBox { Height = 30, Padding = new Thickness(8,4,8,4), Foreground = Brushes.White, CaretBrush = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(35,255,255,255)), BorderBrush = new SolidColorBrush(Color.FromArgb(45,255,255,255)) }; }
        private static Button ProviderButton(string text, double width) { return new Button { Content = text, Width = width, Height = 29, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(45,255,255,255)), BorderBrush = new SolidColorBrush(Color.FromArgb(55,255,255,255)) }; }
    }

    internal static class CodexProvider
    {
        public static bool IsDesktopRunning()
        {
            foreach (Process process in Process.GetProcessesByName("ChatGPT"))
            {
                try
                {
                    string path = process.MainModule.FileName ?? "";
                    if (path.IndexOf("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0
                        && path.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
                finally { try { process.Dispose(); } catch { } }
            }
            return false;
        }

        public static Task<CodexUsage> ReadAsync(CancellationToken cancellationToken)
        {
            return Task.Run(delegate { return Read(cancellationToken); }, cancellationToken);
        }

        private static CodexUsage Read(CancellationToken cancellationToken)
        {
            string executable = FindCodexExecutable();
            if (executable == null)
                throw new InvalidOperationException("Codex executable not found");

            var startInfo = new ProcessStartInfo();
            startInfo.FileName = executable;
            startInfo.Arguments = "app-server --stdio";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            using (var process = new Process())
            {
                process.StartInfo = startInfo;
                process.Start();
                process.BeginErrorReadLine();
                try
                {
                    Send(process, new Dictionary<string, object>
                    {
                        { "method", "initialize" },
                        { "id", 1 },
                        { "params", new Dictionary<string, object>
                            {
                                { "clientInfo", new Dictionary<string, object>
                                    {
                                        { "name", "ai_usage_monitor" },
                                        { "title", "AI Usage Monitor" },
                                        { "version", "0.1.0" }
                                    }
                                }
                            }
                        }
                    });

                    DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                    bool initialized = false;
                    Task<string> lineTask = process.StandardOutput.ReadLineAsync();
                    while (DateTime.UtcNow < deadline)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!lineTask.Wait(500)) continue;
                        string line = lineTask.Result;
                        if (line == null) break;
                        Dictionary<string, object> message = Json.ParseObject(line);
                        int id = Json.GetInt(message, "id", -1);
                        if (id == 1 && !initialized)
                        {
                            initialized = true;
                            Send(process, new Dictionary<string, object>
                            {
                                { "method", "initialized" },
                                { "params", new Dictionary<string, object>() }
                            });
                            Send(process, new Dictionary<string, object>
                            {
                                { "method", "account/rateLimits/read" },
                                { "id", 2 }
                            });
                        }
                        else if (id == 2)
                        {
                            Dictionary<string, object> error = Json.GetObject(message, "error");
                            if (error != null)
                                throw new InvalidOperationException(Json.GetString(error, "message") ?? "Codex query failed");
                            return ParseUsage(Json.GetObject(message, "result"));
                        }
                        lineTask = process.StandardOutput.ReadLineAsync();
                    }
                    throw new TimeoutException("Codex app-server timeout");
                }
                finally
                {
                    try
                    {
                        if (!process.HasExited) process.Kill();
                    }
                    catch { }
                }
            }
        }

        private static void Send(Process process, Dictionary<string, object> message)
        {
            process.StandardInput.WriteLine(Json.Serialize(message));
            process.StandardInput.Flush();
        }

        private static CodexUsage ParseUsage(Dictionary<string, object> result)
        {
            if (result == null) throw new InvalidOperationException("Codex returned no data");
            Dictionary<string, object> limits = Json.GetObject(result, "rateLimits");
            if (limits == null) throw new InvalidOperationException("Codex returned no rate limits");
            var usage = new CodexUsage();
            usage.Primary = ParseWindow(Json.GetObject(limits, "primary"));
            usage.Secondary = ParseWindow(Json.GetObject(limits, "secondary"));
            usage.PlanType = Json.GetString(limits, "planType");
            Dictionary<string, object> credits = Json.GetObject(limits, "credits");
            if (credits != null && !Json.GetBool(credits, "unlimited", false))
                usage.Credits = Json.GetString(credits, "balance");
            return usage;
        }

        private static RateWindow ParseWindow(Dictionary<string, object> value)
        {
            if (value == null) return null;
            var window = new RateWindow();
            window.UsedPercent = Json.GetDouble(value, "usedPercent", 0);
            window.WindowDurationMins = Json.GetInt(value, "windowDurationMins", 0);
            long timestamp = Json.GetLong(value, "resetsAt", 0);
            if (timestamp > 0)
                window.ResetsAt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(timestamp).ToLocalTime();
            return window;
        }

        private static string FindCodexExecutable()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string sandboxCopy = Path.Combine(userProfile, ".codex", ".sandbox-bin", "codex.exe");
            if (File.Exists(sandboxCopy)) return sandboxCopy;

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string folder in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    string candidate = Path.Combine(folder.Trim(), "codex.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }
    }

    internal sealed class DeepSeekUsage
    {
        public bool Available;
        public string TotalDisplay;
        public string Breakdown;
        public double? PrimaryBalance;
        public string CurrencySymbol;
    }

    internal static class DeepSeekProvider
    {
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        public static async Task<DeepSeekUsage> ReadAsync(string apiKey, CancellationToken cancellationToken)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("API key or environment variable is not configured");

            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance"))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
                request.Headers.Accept.ParseAdd("application/json");
                using (HttpResponseMessage response = await Client.SendAsync(request, cancellationToken))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("DeepSeek HTTP " + (int)response.StatusCode);
                    return Parse(body);
                }
            }
        }

        private static DeepSeekUsage Parse(string body)
        {
            Dictionary<string, object> root = Json.ParseObject(body);
            if (root == null) throw new InvalidOperationException("DeepSeek returned invalid data");
            var usage = new DeepSeekUsage();
            usage.Available = Json.GetBool(root, "is_available", false);
            object rawInfos;
            if (!root.TryGetValue("balance_infos", out rawInfos))
                throw new InvalidOperationException("DeepSeek returned no balance");
            object[] infos = rawInfos as object[];
            if (infos == null || infos.Length == 0)
                throw new InvalidOperationException("DeepSeek returned no balance");

            var totals = new List<string>();
            var details = new List<string>();
            foreach (object raw in infos)
            {
                var info = raw as Dictionary<string, object>;
                if (info == null) continue;
                string currency = Json.GetString(info, "currency") ?? "";
                string symbol = currency == "CNY" ? "¥" : (currency == "USD" ? "$" : currency + " ");
                string total = Json.GetString(info, "total_balance") ?? "0";
                string topped = Json.GetString(info, "topped_up_balance") ?? "0";
                string granted = Json.GetString(info, "granted_balance") ?? "0";
                totals.Add(symbol + TrimMoney(total));
                details.Add(string.Format("{0}充值 {1} · 赠送 {2}", symbol, TrimMoney(topped), TrimMoney(granted)));
                if (!usage.PrimaryBalance.HasValue)
                {
                    double parsed;
                    if (double.TryParse(total, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed))
                    {
                        usage.PrimaryBalance = parsed;
                        usage.CurrencySymbol = symbol;
                    }
                }
            }
            usage.TotalDisplay = string.Join(" / ", totals.ToArray());
            usage.Breakdown = string.Join("   ", details.ToArray());
            return usage;
        }

        private static string TrimMoney(string value)
        {
            decimal number;
            if (decimal.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out number))
                return number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return value;
        }
    }

    internal sealed class GlmUsage
    {
        public bool QuotaRead;
        public bool WalletRead;
        public RateWindow FiveHour;
        public RateWindow Weekly;
        public RateWindow Monthly;
        public string Level;
        public string Breakdown;
        public double? Balance;
        public double? WalletTotal;
        public double? TotalSpend;
        public string CurrencySymbol;
    }

    internal sealed class GlmWallet
    {
        public double? Balance;
        public double? WalletTotal;
        public double? TotalSpend;
        public string CurrencySymbol;
    }

    internal static class GlmProvider
    {
        private const string QuotaEndpoint = "https://open.bigmodel.cn/api/monitor/usage/quota/limit";
        private const string ReportEndpoint = "https://open.bigmodel.cn/api/biz/account/query-customer-account-report";
        private const string BalanceEndpoint = "https://open.bigmodel.cn/api/paas/v4/balance";
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

        public static async Task<GlmUsage> ReadAsync(string apiKey, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("API key or environment variable is not configured");

            string key = apiKey.Trim();
            Task<GlmUsage> quotaTask = ReadQuotaAsync(key, cancellationToken);
            Task<GlmWallet> walletTask = ReadWalletAsync(key, cancellationToken);
            GlmUsage usage = null;
            GlmWallet wallet = null;
            Exception quotaError = null;
            Exception walletError = null;
            try { usage = await quotaTask; }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { quotaError = exception; }
            try { wallet = await walletTask; }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { walletError = exception; }

            if (usage == null) usage = new GlmUsage();
            if (wallet != null)
            {
                usage.WalletRead = true;
                usage.Balance = wallet.Balance;
                usage.WalletTotal = wallet.WalletTotal;
                usage.TotalSpend = wallet.TotalSpend;
                usage.CurrencySymbol = wallet.CurrencySymbol;
            }
            if (!usage.QuotaRead && !usage.WalletRead)
            {
                if (IsAuthenticationException(quotaError) || IsAuthenticationException(walletError))
                    throw new InvalidOperationException("API key authentication failed");
                throw new InvalidOperationException("GLM quota and balance unavailable");
            }
            return usage;
        }

        private static async Task<GlmUsage> ReadQuotaAsync(string key, CancellationToken cancellationToken)
        {
            HttpResult result = await SendAsync(QuotaEndpoint, key, false, cancellationToken);
            if (IsAuthenticationFailure(result))
                result = await SendAsync(QuotaEndpoint, key, true, cancellationToken);

            if (IsAuthenticationFailure(result))
                throw new InvalidOperationException("API key authentication failed");
            if (result.StatusCode < 200 || result.StatusCode >= 300)
                throw new InvalidOperationException("GLM HTTP " + result.StatusCode);
            return Parse(result.Body);
        }

        private static async Task<GlmWallet> ReadWalletAsync(string key, CancellationToken cancellationToken)
        {
            try
            {
                HttpResult report = await SendAsync(ReportEndpoint, key, false, cancellationToken);
                return ParseReportWallet(report);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            HttpResult balance = await SendAsync(BalanceEndpoint, key, true, cancellationToken);
            return ParseV4Wallet(balance);
        }

        private static async Task<HttpResult> SendAsync(string endpoint, string key, bool bearer, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
            {
                request.Headers.TryAddWithoutValidation("Authorization", bearer ? "Bearer " + key : key);
                request.Headers.Accept.ParseAdd("application/json");
                using (HttpResponseMessage response = await Client.SendAsync(request, cancellationToken))
                {
                    return new HttpResult
                    {
                        StatusCode = (int)response.StatusCode,
                        Body = await response.Content.ReadAsStringAsync()
                    };
                }
            }
        }

        private static GlmWallet ParseReportWallet(HttpResult result)
        {
            Dictionary<string, object> root = RequireSuccess(result);
            Dictionary<string, object> data = Json.GetObject(root, "data");
            if (data == null) throw new InvalidOperationException("GLM balance report returned no data");
            double? available = OptionalDouble(data, "availableBalance") ?? OptionalDouble(data, "balance");
            if (!available.HasValue) throw new InvalidOperationException("GLM balance report returned no balance");
            return new GlmWallet
            {
                Balance = available,
                TotalSpend = OptionalDouble(data, "totalSpendAmount"),
                CurrencySymbol = CurrencySymbol(Json.GetString(data, "currency"))
            };
        }

        private static GlmWallet ParseV4Wallet(HttpResult result)
        {
            Dictionary<string, object> root = RequireSuccess(result);
            Dictionary<string, object> data = Json.GetObject(root, "data");
            if (data == null) throw new InvalidOperationException("GLM balance returned no data");
            double? available = OptionalDouble(data, "available_balance");
            double? total = OptionalDouble(data, "total_balance") ?? available;
            if (!available.HasValue && !total.HasValue) throw new InvalidOperationException("GLM balance returned no balance");
            return new GlmWallet
            {
                Balance = available ?? total,
                WalletTotal = total,
                CurrencySymbol = CurrencySymbol(Json.GetString(data, "currency"))
            };
        }

        private static Dictionary<string, object> RequireSuccess(HttpResult result)
        {
            if (IsAuthenticationFailure(result)) throw new InvalidOperationException("API key authentication failed");
            if (result.StatusCode < 200 || result.StatusCode >= 300)
                throw new InvalidOperationException("GLM HTTP " + result.StatusCode);
            Dictionary<string, object> root = Json.ParseObject(result.Body);
            if (root == null) throw new InvalidOperationException("GLM returned invalid data");
            object rawCode;
            if (root.TryGetValue("code", out rawCode) && rawCode != null)
            {
                int code;
                if (int.TryParse(Convert.ToString(rawCode, CultureInfo.InvariantCulture), out code) && code != 0 && code != 200)
                    throw new InvalidOperationException(Json.GetString(root, "msg") ?? Json.GetString(root, "message") ?? "GLM API error");
            }
            object rawSuccess;
            if (root.TryGetValue("success", out rawSuccess) && rawSuccess != null && !Convert.ToBoolean(rawSuccess))
                throw new InvalidOperationException(Json.GetString(root, "msg") ?? Json.GetString(root, "message") ?? "GLM API error");
            return root;
        }

        private static double? OptionalDouble(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return null;
            double parsed;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any,
                CultureInfo.InvariantCulture, out parsed) ? parsed : (double?)null;
        }

        private static string CurrencySymbol(string currency)
        {
            if (string.IsNullOrWhiteSpace(currency) || string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase)) return "¥";
            if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase)) return "$";
            return currency.Trim() + " ";
        }

        private static bool IsAuthenticationException(Exception exception)
        {
            return exception != null && exception.Message != null &&
                exception.Message.IndexOf("authentication failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsAuthenticationFailure(HttpResult result)
        {
            if (result.StatusCode == 401 || result.StatusCode == 403) return true;
            try
            {
                Dictionary<string, object> root = Json.ParseObject(result.Body);
                if (root == null) return false;
                object rawCode;
                int code;
                if (root.TryGetValue("code", out rawCode) && rawCode != null &&
                    int.TryParse(Convert.ToString(rawCode, CultureInfo.InvariantCulture), out code) && (code == 401 || code == 403))
                    return true;
                string message = (Json.GetString(root, "msg") ?? Json.GetString(root, "message") ?? "").ToLowerInvariant();
                Dictionary<string, object> error = Json.GetObject(root, "error");
                if (error != null) message += " " + (Json.GetString(error, "message") ?? "").ToLowerInvariant();
                return message.IndexOf("unauthorized", StringComparison.Ordinal) >= 0 ||
                    message.IndexOf("api key", StringComparison.Ordinal) >= 0 ||
                    message.IndexOf("authorization", StringComparison.Ordinal) >= 0 ||
                    message.IndexOf("token expired", StringComparison.Ordinal) >= 0 ||
                    message.IndexOf("token incorrect", StringComparison.Ordinal) >= 0 ||
                    message.IndexOf("鉴权", StringComparison.Ordinal) >= 0 ||
                    message.IndexOf("认证", StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        private static GlmUsage Parse(string body)
        {
            Dictionary<string, object> root = Json.ParseObject(body);
            if (root == null) throw new InvalidOperationException("GLM returned invalid data");

            object rawCode;
            if (root.TryGetValue("code", out rawCode) && rawCode != null)
            {
                int code;
                if (int.TryParse(Convert.ToString(rawCode, CultureInfo.InvariantCulture), out code) && code != 0 && code != 200)
                {
                    string message = Json.GetString(root, "msg") ?? Json.GetString(root, "message") ?? "GLM API error";
                    if (code == 401 || code == 403) throw new InvalidOperationException("API key authentication failed");
                    throw new InvalidOperationException(message);
                }
            }

            Dictionary<string, object> data = Json.GetObject(root, "data") ?? root;
            object rawLimits;
            object[] limits = data.TryGetValue("limits", out rawLimits) ? rawLimits as object[] : null;
            if (limits == null) limits = new object[0];

            var usage = new GlmUsage { QuotaRead = true, Level = Json.GetString(data, "level") };
            var details = new List<string>();
            foreach (object rawLimit in limits)
            {
                Dictionary<string, object> limit = rawLimit as Dictionary<string, object>;
                if (limit == null) continue;
                string type = Json.GetString(limit, "type") ?? "";
                int unit = Json.GetInt(limit, "unit", 0);
                int number = Json.GetInt(limit, "number", 0);
                RateWindow window = ParseWindow(limit, unit, number);
                if (type == "TOKENS_LIMIT" && unit == 3 && (number == 0 || number == 5))
                {
                    usage.FiveHour = window;
                    details.Add(FormatQuota("5h", limit));
                }
                else if (type == "TOKENS_LIMIT" && unit == 6)
                {
                    usage.Weekly = window;
                    details.Add(FormatQuota("周", limit));
                }
                else if (type == "TIME_LIMIT")
                {
                    usage.Monthly = window;
                    details.Add(FormatQuota("MCP 月", limit));
                }
            }

            usage.Breakdown = string.Join("  ·  ", details.ToArray());
            return usage;
        }

        private static RateWindow ParseWindow(Dictionary<string, object> limit, int unit, int number)
        {
            var window = new RateWindow
            {
                UsedPercent = Math.Max(0, Math.Min(100, Json.GetDouble(limit, "percentage", 0))),
                WindowDurationMins = unit == 3 ? Math.Max(1, number) * 60 :
                    (unit == 6 ? Math.Max(1, number) * 7 * 24 * 60 : 30 * 24 * 60)
            };
            long timestamp = Json.GetLong(limit, "nextResetTime", 0);
            if (timestamp > 0)
            {
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                window.ResetsAt = (timestamp > 100000000000L ? epoch.AddMilliseconds(timestamp) : epoch.AddSeconds(timestamp)).ToLocalTime();
            }
            return window;
        }

        private static string FormatQuota(string label, Dictionary<string, object> limit)
        {
            object current;
            object total;
            if (limit.TryGetValue("currentValue", out current) && current != null &&
                limit.TryGetValue("usage", out total) && total != null)
                return label + " " + FormatNumber(current) + "/" + FormatNumber(total);
            return label + " 已用 " + Json.GetDouble(limit, "percentage", 0).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private static string FormatNumber(object value)
        {
            double number;
            if (!double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (Math.Abs(number) >= 1000000000) return (number / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (Math.Abs(number) >= 1000000) return (number / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (Math.Abs(number) >= 1000) return (number / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return number.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private sealed class HttpResult
        {
            public int StatusCode;
            public string Body;
        }
    }

    internal sealed class BalanceSample
    {
        public DateTime Timestamp;
        public double Balance;
        public string Currency;
    }

    internal sealed class DailyUsageTracker
    {
        private readonly string historyPath;

        public DailyUsageTracker()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(local, "LittleTools", "AIUsageMonitor");
            historyPath = Path.Combine(folder, "usage-history.json");
            string legacyPath = Path.Combine(local, "AIUsageMonitor", "usage-history.json");
            try
            {
                if (!File.Exists(historyPath) && File.Exists(legacyPath))
                    AtomicFile.WriteUtf8(historyPath, File.ReadAllText(legacyPath, Encoding.UTF8));
            }
            catch { }
        }

        public void Update(UsageSnapshot state)
        {
            List<BalanceSample> samples = Load();
            samples.RemoveAll(delegate(BalanceSample sample) { return sample == null; });
            DateTime now = DateTime.Now;
            DateTime retention = now.Date.AddDays(-35);
            samples.RemoveAll(delegate(BalanceSample sample) { return sample.Timestamp < retention; });

            if (state.DeepSeekCurrentBalance.HasValue && !string.IsNullOrEmpty(state.DeepSeekCurrencySymbol))
            {
                BalanceSample latest = samples.Count > 0 ? samples[samples.Count - 1] : null;
                if (latest == null || now - latest.Timestamp >= TimeSpan.FromMinutes(1) ||
                    Math.Abs(latest.Balance - state.DeepSeekCurrentBalance.Value) > 0.000001)
                {
                    samples.Add(new BalanceSample
                    {
                        Timestamp = now,
                        Balance = state.DeepSeekCurrentBalance.Value,
                        Currency = state.DeepSeekCurrencySymbol
                    });
                    Save(samples);
                }
            }

            DateTime today = now.Date;
            int daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
            DateTime weekStart = today.AddDays(-daysSinceMonday);
            DateTime monthStart = new DateTime(now.Year, now.Month, 1);

            state.DeepSeekTodayPoints = BuildRange(samples, today, out state.TodayDeepSeekSpend, out state.DeepSeekTrackingStart);
            state.DeepSeekWeekPoints = BuildRange(samples, weekStart, out state.WeeklyDeepSeekSpend, out state.DeepSeekWeekTrackingStart);
            state.DeepSeekMonthPoints = BuildRange(samples, monthStart, out state.MonthlyDeepSeekSpend, out state.DeepSeekMonthTrackingStart);
            if (string.IsNullOrEmpty(state.DeepSeekCurrencySymbol) && samples.Count > 0)
                state.DeepSeekCurrencySymbol = samples[samples.Count - 1].Currency;
        }

        private static List<UsagePoint> BuildRange(List<BalanceSample> samples, DateTime rangeStart,
            out double? spend, out DateTime? trackingStart)
        {
            var rangeSamples = new List<BalanceSample>();
            BalanceSample baseline = null;
            foreach (BalanceSample sample in samples)
            {
                if (sample.Timestamp >= rangeStart) rangeSamples.Add(sample);
                else if (baseline == null || sample.Timestamp > baseline.Timestamp) baseline = sample;
            }
            rangeSamples.Sort(delegate(BalanceSample left, BalanceSample right) { return left.Timestamp.CompareTo(right.Timestamp); });

            var points = new List<UsagePoint>();
            spend = null;
            trackingStart = null;
            if (rangeSamples.Count == 0) return points;

            trackingStart = baseline == null ? rangeSamples[0].Timestamp : rangeStart;
            double cumulativeSpend = 0;
            points.Add(new UsagePoint { Timestamp = trackingStart.Value, Value = 0 });
            BalanceSample previous = baseline ?? rangeSamples[0];
            int firstIndex = baseline == null ? 1 : 0;
            for (int index = firstIndex; index < rangeSamples.Count; index++)
            {
                double decrease = previous.Balance - rangeSamples[index].Balance;
                if (decrease > 0) cumulativeSpend += decrease;
                points.Add(new UsagePoint
                {
                    Timestamp = rangeSamples[index].Timestamp,
                    Value = cumulativeSpend
                });
                previous = rangeSamples[index];
            }
            spend = cumulativeSpend;
            return points;
        }

        private List<BalanceSample> Load()
        {
            try
            {
                if (!File.Exists(historyPath)) return new List<BalanceSample>();
                return Json.Deserialize<List<BalanceSample>>(File.ReadAllText(historyPath, Encoding.UTF8)) ?? new List<BalanceSample>();
            }
            catch { return new List<BalanceSample>(); }
        }

        private void Save(List<BalanceSample> samples)
        {
            try
            {
                AtomicFile.WriteUtf8(historyPath, Json.Serialize(samples));
            }
            catch { }
        }
    }

    internal sealed class GlmSpendSample
    {
        public DateTime Timestamp;
        public double? Balance;
        public double? TotalSpend;
        public string Currency;
    }

    internal sealed class GlmUsageTracker
    {
        private readonly string historyPath;

        public GlmUsageTracker()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            historyPath = Path.Combine(local, "LittleTools", "AIUsageMonitor", "glm-usage-history.json");
        }

        public void Update(UsageSnapshot state)
        {
            List<GlmSpendSample> samples = Load();
            samples.RemoveAll(delegate(GlmSpendSample sample) { return sample == null; });
            DateTime now = DateTime.Now;
            DateTime retention = now.Date.AddDays(-35);
            samples.RemoveAll(delegate(GlmSpendSample sample) { return sample.Timestamp < retention; });

            bool freshWallet = state.GlmWalletUpdatedAt.HasValue && now - state.GlmWalletUpdatedAt.Value < TimeSpan.FromMinutes(1);
            if (freshWallet && (state.GlmBalance.HasValue || state.GlmTotalSpend.HasValue))
            {
                GlmSpendSample latest = samples.Count > 0 ? samples[samples.Count - 1] : null;
                if (latest == null || now - latest.Timestamp >= TimeSpan.FromMinutes(1) ||
                    !Same(latest.Balance, state.GlmBalance) || !Same(latest.TotalSpend, state.GlmTotalSpend))
                {
                    samples.Add(new GlmSpendSample
                    {
                        Timestamp = now,
                        Balance = state.GlmBalance,
                        TotalSpend = state.GlmTotalSpend,
                        Currency = state.GlmCurrencySymbol
                    });
                    Save(samples);
                }
            }

            DateTime today = now.Date;
            int daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
            DateTime weekStart = today.AddDays(-daysSinceMonday);
            DateTime monthStart = new DateTime(now.Year, now.Month, 1);
            state.GlmTodayPoints = BuildRange(samples, today, out state.TodayGlmSpend, out state.GlmTrackingStart);
            state.GlmWeekPoints = BuildRange(samples, weekStart, out state.WeeklyGlmSpend, out state.GlmWeekTrackingStart);
            state.GlmMonthPoints = BuildRange(samples, monthStart, out state.MonthlyGlmSpend, out state.GlmMonthTrackingStart);
            if (string.IsNullOrEmpty(state.GlmCurrencySymbol) && samples.Count > 0)
                state.GlmCurrencySymbol = samples[samples.Count - 1].Currency;
        }

        private static bool Same(double? first, double? second)
        {
            if (!first.HasValue || !second.HasValue) return first.HasValue == second.HasValue;
            return Math.Abs(first.Value - second.Value) <= 0.000001;
        }

        private static List<UsagePoint> BuildRange(List<GlmSpendSample> samples, DateTime rangeStart,
            out double? spend, out DateTime? trackingStart)
        {
            var rangeSamples = new List<GlmSpendSample>();
            foreach (GlmSpendSample sample in samples)
            {
                if (sample.Timestamp >= rangeStart) rangeSamples.Add(sample);
            }
            rangeSamples.Sort(delegate(GlmSpendSample left, GlmSpendSample right) { return left.Timestamp.CompareTo(right.Timestamp); });

            bool useCumulativeSpend = rangeSamples.Any(delegate(GlmSpendSample sample) { return sample.TotalSpend.HasValue; });
            rangeSamples.RemoveAll(delegate(GlmSpendSample sample)
            {
                return useCumulativeSpend ? !sample.TotalSpend.HasValue : !sample.Balance.HasValue;
            });

            GlmSpendSample baseline = null;
            foreach (GlmSpendSample sample in samples)
            {
                bool compatible = useCumulativeSpend ? sample.TotalSpend.HasValue : sample.Balance.HasValue;
                if (compatible && sample.Timestamp < rangeStart &&
                    (baseline == null || sample.Timestamp > baseline.Timestamp)) baseline = sample;
            }

            var points = new List<UsagePoint>();
            spend = null;
            trackingStart = null;
            if (rangeSamples.Count == 0) return points;

            trackingStart = baseline == null ? rangeSamples[0].Timestamp : rangeStart;
            double cumulative = 0;
            points.Add(new UsagePoint { Timestamp = trackingStart.Value, Value = 0 });
            GlmSpendSample previous = baseline ?? rangeSamples[0];
            int firstIndex = baseline == null ? 1 : 0;
            for (int index = firstIndex; index < rangeSamples.Count; index++)
            {
                double increase = useCumulativeSpend
                    ? rangeSamples[index].TotalSpend.Value - previous.TotalSpend.Value
                    : previous.Balance.Value - rangeSamples[index].Balance.Value;
                if (increase > 0) cumulative += increase;
                points.Add(new UsagePoint { Timestamp = rangeSamples[index].Timestamp, Value = cumulative });
                previous = rangeSamples[index];
            }
            spend = cumulative;
            return points;
        }

        private List<GlmSpendSample> Load()
        {
            try
            {
                if (!File.Exists(historyPath)) return new List<GlmSpendSample>();
                return Json.Deserialize<List<GlmSpendSample>>(File.ReadAllText(historyPath, Encoding.UTF8)) ?? new List<GlmSpendSample>();
            }
            catch { return new List<GlmSpendSample>(); }
        }

        private void Save(List<GlmSpendSample> samples)
        {
            try { AtomicFile.WriteUtf8(historyPath, Json.Serialize(samples)); }
            catch { }
        }
    }

    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly object Sync = new object();

        public static string Serialize(object value) { lock (Sync) return Serializer.Serialize(value); }
        public static T Deserialize<T>(string value) { lock (Sync) return Serializer.Deserialize<T>(value); }
        public static Dictionary<string, object> ParseObject(string value)
        {
            lock (Sync) return Serializer.DeserializeObject(value) as Dictionary<string, object>;
        }
        public static Dictionary<string, object> GetObject(Dictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            return source.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }
        public static string GetString(Dictionary<string, object> source, string key)
        {
            if (source == null) return null;
            object value;
            return source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : null;
        }
        public static int GetInt(Dictionary<string, object> source, string key, int fallback)
        {
            try { return Convert.ToInt32(source[key]); } catch { return fallback; }
        }
        public static long GetLong(Dictionary<string, object> source, string key, long fallback)
        {
            try { return Convert.ToInt64(source[key]); } catch { return fallback; }
        }
        public static double GetDouble(Dictionary<string, object> source, string key, double fallback)
        {
            try { return Convert.ToDouble(source[key], System.Globalization.CultureInfo.InvariantCulture); } catch { return fallback; }
        }
        public static bool GetBool(Dictionary<string, object> source, string key, bool fallback)
        {
            try { return Convert.ToBoolean(source[key]); } catch { return fallback; }
        }
    }

    internal sealed class MonitorWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x20;
        private const int WsExToolWindow = 0x80;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        private readonly TextBlock codexText;
        private readonly TextBlock deepSeekText;
        private readonly TextBlock glmText;
        private readonly TextBlock updatedText;
        private readonly Border shell;
        private readonly Grid compactGrid;
        private readonly DispatcherTimer edgeHideTimer;
        private DetailsWindow details;
        private UsageSnapshot currentState;
        private bool refreshing;
        private bool movingDetails;
        private bool movingMonitor;
        private bool edgeHideEnabled = true;
        private int hiddenEdge;
        private double expandedLeft;

        public event Action RefreshRequested;
        public event Action ExitRequested;
        public event Action SettingsRequested;

        public MonitorWindow(UsageSnapshot state)
        {
            currentState = state;
            Width = 316;
            Height = 92;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Title = "AI Usage Monitor";

            shell = new Border();
            shell.CornerRadius = new CornerRadius(15);
            shell.Background = NormalBackground();
            shell.BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255));
            shell.BorderThickness = new Thickness(1);
            shell.Padding = new Thickness(14, 10, 14, 9);
            shell.Effect = new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 1,
                Opacity = 0.16,
                Color = Colors.Black
            };

            compactGrid = new Grid();
            compactGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            compactGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            compactGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            compactGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            codexText = CreateRow(compactGrid, 0, Color.FromRgb(89, 210, 255), "CODEX", "正在连接");
            deepSeekText = CreateRow(compactGrid, 1, Color.FromRgb(124, 237, 174), "DEEPSEEK", "正在连接");
            glmText = CreateRow(compactGrid, 2, Color.FromRgb(190, 154, 255), "GLM", "未启用");
            updatedText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                Text = "等待更新",
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(updatedText, 3);
            compactGrid.Children.Add(updatedText);
            shell.Child = compactGrid;
            Content = shell;

            edgeHideTimer = new DispatcherTimer();
            edgeHideTimer.Interval = TimeSpan.FromMilliseconds(550);
            edgeHideTimer.Tick += delegate
            {
                edgeHideTimer.Stop();
                if (edgeHideEnabled && hiddenEdge != 0 && !IsMouseOver && (details == null || !details.IsVisible))
                    HideToEdge();
            };

            Loaded += delegate { PositionAtTopRight(); ApplyToolWindowStyle(); UpdateView(state); };
            LocationChanged += delegate { PositionDetails(); };
            MouseEnter += delegate
            {
                edgeHideTimer.Stop();
                shell.Background = HoverBackground();
                RevealFromEdge();
            };
            MouseLeave += delegate
            {
                shell.Background = NormalBackground();
                if (edgeHideEnabled && hiddenEdge != 0) edgeHideTimer.Start();
            };
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                RevealFromEdge();
                double originalLeft = Left;
                double originalTop = Top;
                movingMonitor = true;
                try { DragMove(); } catch { }
                finally { movingMonitor = false; }
                bool moved = Math.Abs(Left - originalLeft) > 1 || Math.Abs(Top - originalTop) > 1;
                SnapOrHideAtEdge();
                if (!moved) ToggleDetails(currentState);
                args.Handled = true;
            };
        }

        private static TextBlock CreateRow(Grid grid, int row, Color color, string label, string value)
        {
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(79) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            rowGrid.Children.Add(dot);
            var name = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(175, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 1);
            rowGrid.Children.Add(name);
            var text = new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 12.5,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 2);
            rowGrid.Children.Add(text);
            Grid.SetRow(rowGrid, row);
            grid.Children.Add(rowGrid);
            return text;
        }

        public void UpdateView(UsageSnapshot state)
        {
            currentState = state;
            SetCompactProviderVisibility(state);
            if (state.FiveHourRemaining.HasValue || state.WeeklyRemaining.HasValue)
            {
                var parts = new List<string>();
                if (state.FiveHourRemaining.HasValue) parts.Add("5h " + Percent(state.FiveHourRemaining.Value));
                if (state.WeeklyRemaining.HasValue) parts.Add("周 " + Percent(state.WeeklyRemaining.Value));
                if (!string.IsNullOrEmpty(state.CodexState) && state.CodexState.StartsWith("Codex 未运行", StringComparison.Ordinal)) parts.Add("缓存");
                codexText.Text = string.Join("  ·  ", parts.ToArray());
                codexText.Foreground = StatusBrush(Minimum(state.FiveHourRemaining, state.WeeklyRemaining));
            }
            else
            {
                codexText.Text = state.CodexState ?? "不可用";
                codexText.Foreground = MutedBrush();
            }

            if (!string.IsNullOrEmpty(state.DeepSeekBalance))
            {
                string today = state.TodayDeepSeekSpend.HasValue
                    ? " · 今约" +
                        (state.DeepSeekCurrencySymbol ?? "¥") + state.TodayDeepSeekSpend.Value.ToString("0.####", CultureInfo.InvariantCulture)
                    : "";
                deepSeekText.Text = state.DeepSeekBalance + today;
                deepSeekText.Foreground = state.DeepSeekState == "余额不足" ? WarningBrush() : Brushes.White;
            }
            else
            {
                deepSeekText.Text = state.DeepSeekState ?? "不可用";
                deepSeekText.Foreground = MutedBrush();
            }

            if (state.GlmBalance.HasValue || state.TodayGlmSpend.HasValue || state.GlmFiveHourRemaining.HasValue || state.GlmWeeklyRemaining.HasValue || state.GlmMonthlyRemaining.HasValue)
            {
                var glmParts = new List<string>();
                if (state.GlmBalance.HasValue) glmParts.Add((state.GlmCurrencySymbol ?? "¥") + FormatCompactNumber(state.GlmBalance.Value));
                if (state.TodayGlmSpend.HasValue)
                    glmParts.Add("今约" +
                        (state.GlmBalance.HasValue ? "" : (state.GlmCurrencySymbol ?? "¥")) + FormatCompactNumber(state.TodayGlmSpend.Value));
                if (state.GlmFiveHourRemaining.HasValue) glmParts.Add("5h " + Percent(state.GlmFiveHourRemaining.Value));
                if (state.GlmWeeklyRemaining.HasValue) glmParts.Add("周 " + Percent(state.GlmWeeklyRemaining.Value));
                if (!state.GlmFiveHourRemaining.HasValue && !state.GlmWeeklyRemaining.HasValue && state.GlmMonthlyRemaining.HasValue)
                    glmParts.Add("MCP " + Percent(state.GlmMonthlyRemaining.Value));
                if (!string.IsNullOrEmpty(state.GlmState) && state.GlmState != "正常" &&
                    state.GlmState.IndexOf("正常", StringComparison.Ordinal) < 0) glmParts.Add("缓存");
                glmText.Text = string.Join(" · ", glmParts.ToArray());
                glmText.Foreground = state.GlmFiveHourRemaining.HasValue || state.GlmWeeklyRemaining.HasValue || state.GlmMonthlyRemaining.HasValue
                    ? StatusBrush(Minimum(Minimum(state.GlmFiveHourRemaining, state.GlmWeeklyRemaining), state.GlmMonthlyRemaining))
                    : (state.GlmBalance.HasValue ? BalanceStatusBrush(state.GlmBalance.Value) : Brushes.White);
            }
            else
            {
                glmText.Text = state.GlmState ?? "未启用";
                glmText.Foreground = MutedBrush();
            }

            UpdateFooter();
            if (details != null && details.IsVisible) details.UpdateView(state);
        }

        private void SetCompactProviderVisibility(UsageSnapshot state)
        {
            bool[] enabled = { state.CodexEnabled, state.DeepSeekEnabled, state.GlmEnabled };
            TextBlock[] values = { codexText, deepSeekText, glmText };
            int count = enabled.Count(delegate(bool value) { return value; });
            double rowHeight = count <= 1 ? 48 : (count == 2 ? 27 : 20);
            for (int index = 0; index < 3; index++)
            {
                compactGrid.RowDefinitions[index].Height = new GridLength(enabled[index] ? rowHeight : 0);
                FrameworkElement row = values[index].Parent as FrameworkElement;
                if (row != null) row.Visibility = enabled[index] ? Visibility.Visible : Visibility.Collapsed;
            }
            compactGrid.RowDefinitions[3].Height = new GridLength(Math.Max(12, 72 - rowHeight * Math.Max(1, count)));
        }

        public void SetRefreshing(bool value)
        {
            refreshing = value;
            UpdateFooter();
        }

        private void UpdateFooter()
        {
            updatedText.Text = refreshing ? "正在刷新…" :
                (currentState != null && currentState.UpdatedAt.HasValue
                    ? "更新 " + currentState.UpdatedAt.Value.ToString("HH:mm")
                    : "等待更新");
        }

        private static double Minimum(double? first, double? second)
        {
            if (!first.HasValue) return second ?? 100;
            if (!second.HasValue) return first.Value;
            return Math.Min(first.Value, second.Value);
        }

        private static string Percent(double value) { return Math.Round(value).ToString("0") + "%"; }
        private static string FormatCompactNumber(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }
        private static Brush StatusBrush(double remaining)
        {
            if (remaining <= 10) return new SolidColorBrush(Color.FromRgb(255, 102, 119));
            if (remaining <= 25) return WarningBrush();
            return Brushes.White;
        }
        private static Brush BalanceStatusBrush(double balance)
        {
            if (balance <= 0) return new SolidColorBrush(Color.FromRgb(255, 102, 119));
            if (balance < 10) return WarningBrush();
            return Brushes.White;
        }
        private static Brush WarningBrush() { return new SolidColorBrush(Color.FromRgb(255, 198, 92)); }
        private static Brush MutedBrush() { return new SolidColorBrush(Color.FromArgb(180, 210, 214, 224)); }
        private static Brush NormalBackground() { return new SolidColorBrush(Color.FromArgb(62, 17, 20, 27)); }
        private static Brush HoverBackground() { return new SolidColorBrush(Color.FromArgb(112, 17, 20, 27)); }

        private void ToggleDetails(UsageSnapshot state)
        {
            if (details != null && details.IsVisible)
            {
                details.Close();
                details = null;
                return;
            }
            details = new DetailsWindow();
            edgeHideTimer.Stop();
            RevealFromEdge();
            details.Owner = this;
            details.RefreshRequested += delegate { if (RefreshRequested != null) RefreshRequested(); };
            details.ExitRequested += delegate { if (ExitRequested != null) ExitRequested(); };
            details.SettingsRequested += delegate { if (SettingsRequested != null) SettingsRequested(); };
            details.Deactivated += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!movingDetails && !movingMonitor && details != null && details.IsVisible
                        && !IsMouseOver && !details.IsMouseOver)
                        details.Close();
                }), DispatcherPriority.Background);
            };
            details.MoveRequested += delegate
            {
                movingDetails = true;
                try
                {
                    RevealFromEdge();
                    DragMove();
                    SnapOrHideAtEdge();
                    if (details != null && details.IsVisible) details.Activate();
                }
                catch { }
                finally { movingDetails = false; }
            };
            details.Closed += delegate
            {
                details = null;
                if (hiddenEdge != 0 && !IsMouseOver) edgeHideTimer.Start();
            };
            details.UpdateView(state);
            PositionDetails();
            details.Show();
        }

        private void PositionDetails()
        {
            if (details == null) return;
            Rect workArea = SystemParameters.WorkArea;
            double desiredLeft = Left + Width - details.Width;
            double desiredTop = Top + Height + 8;
            details.Left = Math.Max(workArea.Left, Math.Min(desiredLeft, workArea.Right - details.Width));
            details.Top = Math.Max(workArea.Top, Math.Min(desiredTop, workArea.Bottom - details.Height));
        }

        private void PositionAtTopRight()
        {
            Rect workArea = SystemParameters.WorkArea;
            Left = workArea.Right - Width - 18;
            Top = workArea.Top + 18;
        }

        private void SnapOrHideAtEdge()
        {
            const double snapDistance = 28;
            Rect workArea = SystemParameters.WorkArea;
            Top = Math.Max(workArea.Top, Math.Min(Top, workArea.Bottom - Height));

            if (!edgeHideEnabled)
            {
                hiddenEdge = 0;
                Left = Math.Max(workArea.Left, Math.Min(Left, workArea.Right - Width));
            }
            else if (Left <= workArea.Left + snapDistance)
            {
                hiddenEdge = -1;
                expandedLeft = workArea.Left;
                if (details != null && details.IsVisible) Left = expandedLeft;
                else HideToEdge();
            }
            else if (Left + Width >= workArea.Right - snapDistance)
            {
                hiddenEdge = 1;
                expandedLeft = workArea.Right - Width;
                if (details != null && details.IsVisible) Left = expandedLeft;
                else HideToEdge();
            }
            else
            {
                hiddenEdge = 0;
                Left = Math.Max(workArea.Left, Math.Min(Left, workArea.Right - Width));
            }
        }

        private void RevealFromEdge()
        {
            if (hiddenEdge == 0) return;
            Left = expandedLeft;
        }

        private void HideToEdge()
        {
            if (!edgeHideEnabled || (details != null && details.IsVisible)) return;
            const double visibleStrip = 9;
            Rect workArea = SystemParameters.WorkArea;
            if (hiddenEdge < 0)
                Left = workArea.Left - Width + visibleStrip;
            else if (hiddenEdge > 0)
                Left = workArea.Right - visibleStrip;
        }

        private void ApplyToolWindowStyle()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);
        }

        public void SetClickThrough(bool enabled)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            if (enabled) style |= WsExTransparent;
            else style &= ~WsExTransparent;
            SetWindowLong(handle, GwlExStyle, style);
        }

        public void SetEdgeHideEnabled(bool enabled)
        {
            edgeHideEnabled = enabled;
            edgeHideTimer.Stop();
            if (!enabled)
            {
                RevealFromEdge();
                hiddenEdge = 0;
            }
        }
    }

    internal sealed class DetailsWindow : Window
    {
        private readonly TextBlock codexValue;
        private readonly TextBlock codexMeta;
        private readonly TextBlock deepSeekValue;
        private readonly TextBlock deepSeekMeta;
        private readonly TextBlock glmValue;
        private readonly TextBlock glmMeta;
        private readonly TextBlock todayDeepSeek;
        private readonly TextBlock todayGlm;
        private readonly TextBlock chartMaximum;
        private readonly TextBlock chartMinimum;
        private readonly TextBlock chartStart;
        private readonly TextBlock chartEnd;
        private readonly TextBlock trendTitle;
        private readonly UsageChart usageChart;
        private readonly TextBlock updated;
        private readonly Button dayRange;
        private readonly Button weekRange;
        private readonly Button monthRange;
        private readonly Button deepSeekTrend;
        private readonly Button glmTrend;
        private readonly StackPanel contentPanel;
        private UsageSnapshot latestState;
        private int selectedRange;
        private int selectedProvider;

        public event Action RefreshRequested;
        public event Action ExitRequested;
        public event Action MoveRequested;
        public event Action SettingsRequested;

        public DetailsWindow()
        {
            Width = 380;
            Height = 505;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;

            var shell = new Border
            {
                CornerRadius = new CornerRadius(17),
                Background = new SolidColorBrush(Color.FromArgb(72, 17, 20, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(19, 16, 19, 16),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 1, Opacity = 0.16 }
            };
            contentPanel = new StackPanel();
            var panel = contentPanel;
            panel.Children.Add(new TextBlock
            {
                Text = "AI USAGE",
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                Margin = new Thickness(0, 0, 0, 13)
            });
            panel.Children.Add(Label("CODEX"));
            codexValue = Value(); panel.Children.Add(codexValue);
            codexMeta = Meta(); panel.Children.Add(codexMeta);
            panel.Children.Add(Separator());

            var deepSeekHeader = new Grid();
            deepSeekHeader.ColumnDefinitions.Add(new ColumnDefinition());
            deepSeekHeader.ColumnDefinitions.Add(new ColumnDefinition());
            deepSeekHeader.Children.Add(Label("DEEPSEEK"));
            todayDeepSeek = new TextBlock
            {
                Text = "今日 --",
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(124, 237, 174)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(todayDeepSeek, 1); deepSeekHeader.Children.Add(todayDeepSeek);
            panel.Children.Add(deepSeekHeader);
            deepSeekValue = Value(); panel.Children.Add(deepSeekValue);
            deepSeekMeta = Meta(); panel.Children.Add(deepSeekMeta);
            panel.Children.Add(Separator());

            var glmHeader = new Grid();
            glmHeader.ColumnDefinitions.Add(new ColumnDefinition());
            glmHeader.ColumnDefinitions.Add(new ColumnDefinition());
            glmHeader.Children.Add(Label("GLM · 国内智谱"));
            todayGlm = new TextBlock
            {
                Text = "今日 --",
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(190, 154, 255)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(todayGlm, 1); glmHeader.Children.Add(todayGlm);
            panel.Children.Add(glmHeader);
            glmValue = Value(); glmValue.FontSize = 15; panel.Children.Add(glmValue);
            glmMeta = Meta(); glmMeta.Text = "读取 GLM 账户余额与 Coding Plan 套餐配额"; panel.Children.Add(glmMeta);
            panel.Children.Add(Separator());

            var trendHeader = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            trendHeader.ColumnDefinitions.Add(new ColumnDefinition());
            trendHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            trendTitle = Label("今日余额减少趋势");
            trendHeader.Children.Add(trendTitle);
            var rangeButtons = new StackPanel { Orientation = Orientation.Horizontal };
            deepSeekTrend = RangeButton("DS");
            glmTrend = RangeButton("GLM"); glmTrend.Width = 38;
            dayRange = RangeButton("日");
            weekRange = RangeButton("周");
            monthRange = RangeButton("月");
            deepSeekTrend.Click += delegate { SelectProvider(0); };
            glmTrend.Click += delegate { SelectProvider(1); };
            dayRange.Click += delegate { SelectRange(0); };
            weekRange.Click += delegate { SelectRange(1); };
            monthRange.Click += delegate { SelectRange(2); };
            rangeButtons.Children.Add(deepSeekTrend);
            rangeButtons.Children.Add(glmTrend);
            rangeButtons.Children.Add(dayRange);
            rangeButtons.Children.Add(weekRange);
            rangeButtons.Children.Add(monthRange);
            Grid.SetColumn(rangeButtons, 1);
            trendHeader.Children.Add(rangeButtons);
            panel.Children.Add(trendHeader);
            var chartFrame = new Grid { Height = 103, Margin = new Thickness(0, 1, 0, 0) };
            chartFrame.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            chartFrame.ColumnDefinitions.Add(new ColumnDefinition());
            chartFrame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(82) });
            chartFrame.RowDefinitions.Add(new RowDefinition { Height = new GridLength(21) });

            var yAxis = new Grid();
            chartMaximum = AxisText(); chartMaximum.VerticalAlignment = VerticalAlignment.Top; yAxis.Children.Add(chartMaximum);
            chartMinimum = AxisText(); chartMinimum.VerticalAlignment = VerticalAlignment.Bottom; yAxis.Children.Add(chartMinimum);
            chartFrame.Children.Add(yAxis);

            usageChart = new UsageChart();
            Grid.SetColumn(usageChart, 1); chartFrame.Children.Add(usageChart);
            var xAxis = new Grid();
            chartStart = AxisText(); chartStart.HorizontalAlignment = HorizontalAlignment.Left; xAxis.Children.Add(chartStart);
            chartEnd = AxisText(); chartEnd.HorizontalAlignment = HorizontalAlignment.Right; xAxis.Children.Add(chartEnd);
            Grid.SetColumn(xAxis, 1); Grid.SetRow(xAxis, 1); chartFrame.Children.Add(xAxis);
            panel.Children.Add(chartFrame);
            panel.Children.Add(Separator());

            var buttons = new Grid();
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            updated = Meta();
            updated.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(updated);
            var settings = SmallButton("设置");
            settings.Click += delegate { if (SettingsRequested != null) SettingsRequested(); };
            Grid.SetColumn(settings, 1); buttons.Children.Add(settings);
            var refresh = SmallButton("刷新");
            refresh.Click += delegate { if (RefreshRequested != null) RefreshRequested(); };
            refresh.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(refresh, 2); buttons.Children.Add(refresh);
            var exit = SmallButton("退出");
            exit.Margin = new Thickness(6, 0, 0, 0);
            exit.Click += delegate { if (ExitRequested != null) ExitRequested(); };
            Grid.SetColumn(exit, 3); buttons.Children.Add(exit);
            panel.Children.Add(buttons);
            shell.Child = panel;
            Content = shell;
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (MoveRequested != null) MoveRequested();
                args.Handled = true;
            };
            UpdateRangeButtons();
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), Margin = new Thickness(0, 0, 0, 3) };
        }
        private static TextBlock Value()
        {
            return new TextBlock { FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 20,
                Foreground = Brushes.White, Text = "正在连接" };
        }
        private static TextBlock Meta()
        {
            return new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(145, 255, 255, 255)), Margin = new Thickness(0, 3, 0, 0) };
        }
        private static TextBlock AxisText()
        {
            return new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        private static Border Separator()
        {
            return new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                Margin = new Thickness(0, 14, 0, 13) };
        }
        private static Button SmallButton(string text)
        {
            return new Button { Content = text, Height = 27, FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), BorderThickness = new Thickness(1) };
        }

        private static Button RangeButton(string text)
        {
            return new Button
            {
                Content = text, Width = 30, Height = 22, Margin = new Thickness(3, 0, 0, 0),
                FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 9.5,
                Foreground = Brushes.White, BorderThickness = new Thickness(1)
            };
        }

        private void SelectRange(int range)
        {
            selectedRange = range;
            UpdateRangeButtons();
            UpdateTrend();
        }

        private void SelectProvider(int provider)
        {
            selectedProvider = provider;
            UpdateRangeButtons();
            UpdateTrend();
        }

        private void UpdateRangeButtons()
        {
            Button[] buttons = { dayRange, weekRange, monthRange };
            for (int index = 0; index < buttons.Length; index++)
            {
                bool selected = index == selectedRange;
                buttons[index].Background = new SolidColorBrush(selected
                    ? Color.FromArgb(105, 124, 237, 174)
                    : Color.FromArgb(35, 255, 255, 255));
                buttons[index].BorderBrush = new SolidColorBrush(selected
                    ? Color.FromArgb(150, 124, 237, 174)
                    : Color.FromArgb(42, 255, 255, 255));
            }
            deepSeekTrend.Background = new SolidColorBrush(selectedProvider == 0 ? Color.FromArgb(105, 124, 237, 174) : Color.FromArgb(35, 255, 255, 255));
            deepSeekTrend.BorderBrush = new SolidColorBrush(selectedProvider == 0 ? Color.FromArgb(150, 124, 237, 174) : Color.FromArgb(42, 255, 255, 255));
            glmTrend.Background = new SolidColorBrush(selectedProvider == 1 ? Color.FromArgb(105, 190, 154, 255) : Color.FromArgb(35, 255, 255, 255));
            glmTrend.BorderBrush = new SolidColorBrush(selectedProvider == 1 ? Color.FromArgb(150, 190, 154, 255) : Color.FromArgb(42, 255, 255, 255));
        }

        public void UpdateView(UsageSnapshot state)
        {
            latestState = state;
            SetSectionVisibility(1, 4, state.CodexEnabled);
            SetSectionVisibility(5, 8, state.DeepSeekEnabled);
            SetSectionVisibility(9, 12, state.GlmEnabled);
            bool trendEnabled = state.DeepSeekEnabled || state.GlmEnabled;
            SetSectionVisibility(13, 15, trendEnabled);
            Height = Math.Max(205, 505 - (state.CodexEnabled ? 0 : 72) - (state.DeepSeekEnabled ? 0 : 72) -
                (state.GlmEnabled ? 0 : 70) - (trendEnabled ? 0 : 148));
            if (selectedProvider == 0 && !state.DeepSeekEnabled && state.GlmEnabled) selectedProvider = 1;
            if (selectedProvider == 1 && !state.GlmEnabled && state.DeepSeekEnabled) selectedProvider = 0;
            deepSeekTrend.Visibility = state.DeepSeekEnabled ? Visibility.Visible : Visibility.Collapsed;
            glmTrend.Visibility = state.GlmEnabled ? Visibility.Visible : Visibility.Collapsed;
            UpdateRangeButtons();
            var codexParts = new List<string>();
            if (state.FiveHourRemaining.HasValue) codexParts.Add("5h " + Math.Round(state.FiveHourRemaining.Value) + "%");
            if (state.WeeklyRemaining.HasValue) codexParts.Add("周 " + Math.Round(state.WeeklyRemaining.Value) + "%");
            codexValue.Text = codexParts.Count > 0 ? string.Join("   ", codexParts.ToArray()) : state.CodexState;
            var codexDetails = new List<string>();
            if (!string.IsNullOrEmpty(state.PlanType)) codexDetails.Add("计划 " + state.PlanType);
            if (state.FiveHourReset.HasValue) codexDetails.Add("5h 重置 " + state.FiveHourReset.Value.ToString("HH:mm"));
            if (state.WeeklyReset.HasValue) codexDetails.Add("周重置 " + state.WeeklyReset.Value.ToString("M/d HH:mm"));
            if (!string.IsNullOrEmpty(state.CodexCredits)) codexDetails.Add("Credits " + state.CodexCredits);
            if (!string.IsNullOrEmpty(state.CodexState) && state.CodexState != "正常") codexDetails.Add(state.CodexState);
            if (state.CodexUpdatedAt.HasValue) codexDetails.Add("额度更新 " + state.CodexUpdatedAt.Value.ToString("M/d HH:mm"));
            codexMeta.Text = codexDetails.Count > 0 ? string.Join("  ·  ", codexDetails.ToArray()) : "等待官方额度数据";

            deepSeekValue.Text = !string.IsNullOrEmpty(state.DeepSeekBalance) ? state.DeepSeekBalance : state.DeepSeekState;
            deepSeekMeta.Text = !string.IsNullOrEmpty(state.DeepSeekBreakdown) ? state.DeepSeekBreakdown : "读取 DEEPSEEK_API_KEY";
            todayDeepSeek.Text = FormatTodaySpend(state.TodayDeepSeekSpend, state.DeepSeekTrackingStart, state.DeepSeekCurrencySymbol ?? "¥");
            var glmParts = new List<string>();
            string glmCurrency = state.GlmCurrencySymbol ?? "¥";
            if (state.GlmBalance.HasValue) glmParts.Add("余额 " + glmCurrency + state.GlmBalance.Value.ToString("0.##", CultureInfo.InvariantCulture));
            if (state.GlmFiveHourRemaining.HasValue) glmParts.Add("5h " + Math.Round(state.GlmFiveHourRemaining.Value) + "%");
            if (state.GlmWeeklyRemaining.HasValue) glmParts.Add("周 " + Math.Round(state.GlmWeeklyRemaining.Value) + "%");
            if (state.GlmMonthlyRemaining.HasValue) glmParts.Add("MCP 月 " + Math.Round(state.GlmMonthlyRemaining.Value) + "%");
            glmValue.Text = glmParts.Count > 0 ? string.Join("   ", glmParts.ToArray()) : (state.GlmState ?? "未启用");
            var glmDetails = new List<string>();
            if (state.GlmTotalSpend.HasValue) glmDetails.Add("累计消费 " + glmCurrency + state.GlmTotalSpend.Value.ToString("0.##", CultureInfo.InvariantCulture));
            if (state.GlmWalletTotal.HasValue && (!state.GlmBalance.HasValue || Math.Abs(state.GlmWalletTotal.Value - state.GlmBalance.Value) > 0.000001))
                glmDetails.Add("账户总额 " + glmCurrency + state.GlmWalletTotal.Value.ToString("0.##", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(state.GlmPlanType)) glmDetails.Add("套餐 " + state.GlmPlanType);
            if (!string.IsNullOrEmpty(state.GlmQuotaBreakdown)) glmDetails.Add(state.GlmQuotaBreakdown);
            if (state.GlmFiveHourReset.HasValue) glmDetails.Add("5h 重置 " + state.GlmFiveHourReset.Value.ToString("HH:mm"));
            if (state.GlmWeeklyReset.HasValue) glmDetails.Add("周重置 " + state.GlmWeeklyReset.Value.ToString("M/d HH:mm"));
            if (state.GlmMonthlyReset.HasValue) glmDetails.Add("月重置 " + state.GlmMonthlyReset.Value.ToString("M/d HH:mm"));
            if (!string.IsNullOrEmpty(state.GlmState) && state.GlmState != "正常")
                glmDetails.Add(state.GlmState + (glmParts.Count > 0 && state.GlmState.IndexOf("正常", StringComparison.Ordinal) < 0 ? " · 显示上次数据" : ""));
            if (state.GlmUpdatedAt.HasValue) glmDetails.Add("额度更新 " + state.GlmUpdatedAt.Value.ToString("M/d HH:mm"));
            glmMeta.Text = glmDetails.Count > 0 ? string.Join("  ·  ", glmDetails.ToArray()) : "读取 GLM 账户余额与 Coding Plan 套餐配额";
            todayGlm.Text = FormatTodaySpend(state.TodayGlmSpend, state.GlmTrackingStart, glmCurrency);
            UpdateTrend();
            updated.Text = state.UpdatedAt.HasValue ? "更新 " + state.UpdatedAt.Value.ToString("HH:mm:ss") : "尚未更新";
        }

        private void SetSectionVisibility(int first, int last, bool visible)
        {
            for (int index = first; index <= last && index < contentPanel.Children.Count; index++)
                contentPanel.Children[index].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string FormatTodaySpend(double? spend, DateTime? trackingStart, string currency)
        {
            if (!spend.HasValue) return "今日 --";
            string scope = trackingStart.HasValue && trackingStart.Value <= DateTime.Today.AddMinutes(1) ? "今日约 " : "监控后 ";
            return scope + currency + spend.Value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private void UpdateTrend()
        {
            if (latestState == null) return;
            UsageSnapshot state = latestState;
            List<UsagePoint> points;
            double? spend;
            DateTime rangeStart;
            DateTime? trackingStart;
            string label;
            bool glm = selectedProvider == 1;
            if (selectedRange == 1)
            {
                int daysSinceMonday = ((int)DateTime.Now.DayOfWeek + 6) % 7;
                rangeStart = DateTime.Now.Date.AddDays(-daysSinceMonday);
                points = glm ? state.GlmWeekPoints : state.DeepSeekWeekPoints;
                spend = glm ? state.WeeklyGlmSpend : state.WeeklyDeepSeekSpend;
                trackingStart = glm ? state.GlmWeekTrackingStart : state.DeepSeekWeekTrackingStart;
                label = "本周";
            }
            else if (selectedRange == 2)
            {
                rangeStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                points = glm ? state.GlmMonthPoints : state.DeepSeekMonthPoints;
                spend = glm ? state.MonthlyGlmSpend : state.MonthlyDeepSeekSpend;
                trackingStart = glm ? state.GlmMonthTrackingStart : state.DeepSeekMonthTrackingStart;
                label = "本月";
            }
            else
            {
                rangeStart = DateTime.Now.Date;
                points = glm ? state.GlmTodayPoints : state.DeepSeekTodayPoints;
                spend = glm ? state.TodayGlmSpend : state.TodayDeepSeekSpend;
                trackingStart = glm ? state.GlmTrackingStart : state.DeepSeekTrackingStart;
                label = "今日";
            }

            bool partial = trackingStart.HasValue && trackingStart.Value > rangeStart.AddMinutes(1);
            string scope = partial ? (selectedRange == 0 ? "监控后" : label + "监控后") : label;
            trendTitle.Text = (glm ? "GLM · " : "DeepSeek · ") + scope + "消费趋势（估算）";
            string currency = glm ? (state.GlmCurrencySymbol ?? "¥") : (state.DeepSeekCurrencySymbol ?? "¥");
            usageChart.SetData(points, rangeStart, glm ? Color.FromRgb(190, 154, 255) : Color.FromRgb(124, 237, 174));
            double maximum = 0;
            foreach (UsagePoint point in points) maximum = Math.Max(maximum, point.Value);
            chartMaximum.Text = currency + maximum.ToString("0.####", CultureInfo.InvariantCulture);
            chartMinimum.Text = currency + "0";
            DateTime visibleStart = trackingStart ?? rangeStart;
            chartStart.Text = selectedRange == 0 ? visibleStart.ToString("HH:mm") : visibleStart.ToString("M/d");
            chartEnd.Text = selectedRange == 0 ? DateTime.Now.ToString("HH:mm") : DateTime.Now.ToString("M/d");
        }
    }

    internal sealed class UsageChart : FrameworkElement
    {
        private List<UsagePoint> points = new List<UsagePoint>();
        private DateTime visibleStart = DateTime.Now;
        private Color seriesColor = Color.FromRgb(124, 237, 174);

        public void SetData(List<UsagePoint> values, DateTime rangeStart, Color color)
        {
            points = values ?? new List<UsagePoint>();
            visibleStart = rangeStart;
            seriesColor = color;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            double width = Math.Max(1, ActualWidth);
            var background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
            drawingContext.DrawRoundedRectangle(background, null, new Rect(0, 0, width, Math.Max(1, ActualHeight)), 8, 8);

            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
            drawingContext.DrawLine(gridPen, new Point(7, 7), new Point(width - 7, 7));
            drawingContext.DrawLine(gridPen, new Point(7, ActualHeight / 2), new Point(width - 7, ActualHeight / 2));
            drawingContext.DrawLine(gridPen, new Point(7, ActualHeight - 7), new Point(width - 7, ActualHeight - 7));
            DrawSeries(drawingContext, points, new Rect(7, 7, width - 14, ActualHeight - 14), seriesColor);
        }

        private void DrawSeries(DrawingContext drawingContext, List<UsagePoint> points, Rect area, Color color)
        {
            if (points == null || points.Count == 0 || area.Width <= 0 || area.Height <= 0) return;
            DateTime start = visibleStart;
            DateTime end = DateTime.Now;
            if (end <= start) end = start.AddMinutes(1);
            double maximum = 0;
            foreach (UsagePoint point in points) maximum = Math.Max(maximum, point.Value);
            if (maximum <= 0) maximum = 1;

            var geometry = new StreamGeometry();
            Point lastPoint = new Point(area.Left, area.Bottom);
            using (StreamGeometryContext context = geometry.Open())
            {
                for (int index = 0; index < points.Count; index++)
                {
                    double timeRatio = (points[index].Timestamp - start).TotalSeconds / (end - start).TotalSeconds;
                    timeRatio = Math.Max(0, Math.Min(1, timeRatio));
                    double valueRatio = Math.Max(0, Math.Min(1, points[index].Value / maximum));
                    Point plotted = new Point(area.Left + area.Width * timeRatio, area.Bottom - area.Height * valueRatio);
                    if (index == 0) context.BeginFigure(plotted, false, false);
                    else context.LineTo(plotted, true, false);
                    lastPoint = plotted;
                }
            }
            geometry.Freeze();
            var brush = new SolidColorBrush(Color.FromArgb(225, color.R, color.G, color.B));
            var pen = new Pen(brush, 1.8);
            drawingContext.DrawGeometry(null, pen, geometry);
            drawingContext.DrawEllipse(brush, null, lastPoint, 2.2, 2.2);
        }
    }
}
