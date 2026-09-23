using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// Deterministic render check for the monitor, mirroring the todo and stock render
/// smokes. It drives the real HUD, detail window and settings dialog with the
/// recorded provider answers and writes one PNG per screen, then asserts the
/// composed texts, the realised chart geometry, the drawn series colours and the
/// collapse geometry from the live control tree, so the Windows wording, the
/// per-provider row heights and the trend chart cannot regress silently.
///
/// Determinism rules (same as StockSmoke):
///   * the run owns its state: a throwaway data directory is recreated here with a
///     seeded providers.json, XDG_DATA_HOME is redirected into it and
///     LITTLETOOLS_MONITOR_DATA is cleared, so neither this machine's real
///     keyring/Windows import path nor a previous run can decide the result;
///   * the module's refresh timers are switched off and every refresh is awaited;
///   * failure branches are triggered by a refusing handler or a cleared key, never
///     by the network.
/// </summary>
internal static class MonitorSmoke
{
    /// <summary>The clock the run uses, so every date in the renders is stable.</summary>
    private static readonly DateTime FixedNow = new(2026, 9, 21, 13, 0, 0);

    /// <summary>Colour tolerance for the pixel scan, in one channel.</summary>
    private const int Tolerance = 2;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>The exact renders a successful run produces, asserted at the end.</summary>
    private static readonly string[] ExpectedArtifacts =
    [
        "monitor-compact-all.png", "monitor-compact-autherror.png", "monitor-compact-nokey.png",
        "monitor-compact-none.png", "monitor-compact-noplan.png", "monitor-compact-single.png",
        "monitor-details-ds-day.png", "monitor-details-ds-month.png", "monitor-details-ds-week.png",
        "monitor-details-empty.png", "monitor-details-glm-day.png", "monitor-details-glm-week.png",
        "monitor-details-nokey.png", "monitor-details-none.png", "monitor-details-noplan.png",
        "monitor-settings.png"
    ];

    public static async Task RunAsync(string directory, bool live)
    {
        Directory.CreateDirectory(directory);
        var previousData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var previousOverride = Environment.GetEnvironmentVariable(MonitorStore.DirectoryVariable);
        var previousDeepSeek = Environment.GetEnvironmentVariable(DeepSeekVariable);
        var previousGlm = Environment.GetEnvironmentVariable(GlmVariable);
        try
        {
            // Isolate the import path: the monitor adopts files from
            // XDG_DATA_HOME/LittleTools/AIUsageMonitor, and a real folder there would
            // otherwise decide the state this run starts from.
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(directory, "xdg"));
            Environment.SetEnvironmentVariable(MonitorStore.DirectoryVariable, null);
            Environment.SetEnvironmentVariable(DeepSeekVariable, SmokeKey);
            Environment.SetEnvironmentVariable(GlmVariable, SmokeKey);

            var report = new List<string>();
            await RunFixturePhasesAsync(directory, report);
            await RunSettingsPhaseAsync(directory, report);
            await RunRefusalPhasesAsync(directory, report);

            var produced = Directory.GetFiles(directory, "*.png").Select(Path.GetFileName).OfType<string>()
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            report.Add("artifacts: " + produced.Length + " -> " + string.Join(", ", produced));
            Expect(produced.SequenceEqual(ExpectedArtifacts.OrderBy(name => name, StringComparer.Ordinal)),
                "rendered artifact set", string.Join(", ", produced));

            var text = string.Join('\n', report);
            Console.WriteLine(text);
            File.WriteAllText(Path.Combine(directory, "monitor-chart.txt"), text + Environment.NewLine);

            if (live) await RunLiveAsync(directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previousData);
            Environment.SetEnvironmentVariable(MonitorStore.DirectoryVariable, previousOverride);
            Environment.SetEnvironmentVariable(DeepSeekVariable, previousDeepSeek);
            Environment.SetEnvironmentVariable(GlmVariable, previousGlm);
        }
    }

    private const string DeepSeekVariable = "LITTLETOOLS_MONITOR_SMOKE_DEEPSEEK";
    private const string GlmVariable = "LITTLETOOLS_MONITOR_SMOKE_GLM";
    private const string SmokeKey = "smoke-key";

    // ------------------------------------------------------------ fixture run

    private static async Task RunFixturePhasesAsync(string directory, List<string> report)
    {
        var store = CreateStore(directory, "fixture");
        store.SaveProviders(AllProviders());
        SeedHistory(store);
        using var module = new MonitorModule(new NullNotificationService(), edgeHideEnabled: false,
            store, MonitorFixtures.CreateService(MonitorFixtureScenario.Success), () => FixedNow)
        {
            TimersEnabled = false
        };
        module.Start();
        var window = module.Window ?? throw new InvalidOperationException("The monitor HUD did not open.");
        // The smoke drives the windows on a shared X display, where focus can move at
        // any moment; the Windows deactivate-to-close would take the render target away.
        window.AutoCloseDetailsOnDeactivate = false;
        await SmokeWaiter.WaitOrThrowAsync(() => window.IsVisible, Wait, () => "the HUD to show");

        await module.RefreshNowAsync();
        var compact = await WaitForDescriptionAsync(window.DescribeCompactForSmoke,
            text => text.Contains("¥16.98", StringComparison.Ordinal), "the first fixture refresh");
        Save(window, Path.Combine(directory, "monitor-compact-all.png"));
        report.Add("compact-all: " + compact);
        Expect(compact.Contains("codex=[5h 100%  ·  周 51%/#FFFFFFFF/h=20/vis=True]", StringComparison.Ordinal),
            "the codex row shows both windows", compact);
        Expect(compact.Contains("deepseek=[¥16.98 · 今约¥8.02/#FFFFFFFF/h=20/vis=True]", StringComparison.Ordinal),
            "the deepseek row shows the balance and today's estimate", compact);
        // Windows only falls back to the MCP label when neither the 5h nor the weekly
        // window is present (Program.cs L1636-L1637), so with a full plan it is absent.
        Expect(compact.Contains("glm=[¥3.58 · 今约16.42 · 5h 62% · 周 38%/#FFFFFFFF/h=20/vis=True]",
            StringComparison.Ordinal),
            "the glm row shows the balance, the plan windows and the remaining amounts", compact);
        Expect(compact.Contains("glmTip=¥3.58 · 今约16.42 · 5h 62% · 周 38%", StringComparison.Ordinal),
            "an ellipsised row keeps its full text in a tooltip", compact);
        Expect(compact.Contains("footer=[更新 13:00]", StringComparison.Ordinal)
            && compact.Contains("footerH=12", StringComparison.Ordinal)
            && compact.Contains("size=316x92", StringComparison.Ordinal),
            "the footer and the HUD size are the Windows ones", compact);

        // The detail window: section content and the collapse geometry.
        window.ToggleDetails();
        await SmokeWaiter.WaitOrThrowAsync(() => window.IsDetailsOpen, Wait, () => "the detail window to open");
        var details = window.DetailsForSmoke ?? throw new InvalidOperationException("The detail window is missing.");
        await SmokeWaiter.WaitOrThrowAsync(
            () => details.DescribeDetailsForSmoke().Contains("5h 62%", StringComparison.Ordinal), Wait,
            () => "the detail window to render: " + details.DescribeDetailsForSmoke());
        var rendered = details.DescribeDetailsForSmoke();
        report.Add("details-all: " + rendered);
        Expect(rendered.Contains("codex=5h 100%   周 51%|计划 plus  ·  5h 重置 19:42  ·  周重置 9/24 20:28"
            + "  ·  Credits 0  ·  额度更新 9/21 13:00", StringComparison.Ordinal),
            "the codex section shows the plan, both resets and the credits", rendered);
        Expect(rendered.Contains("deepseek=¥16.98|¥充值 16.98 · 赠送 0|今日约 ¥8.02", StringComparison.Ordinal),
            "the deepseek section", rendered);
        Expect(rendered.Contains(
            "glm=余额 ¥3.58   5h 62%   周 38%   MCP 月 82%|累计消费 ¥16.42  ·  套餐 pro  ·  5h 1.23M/5M"
            + "  ·  周 3.1M/5M  ·  MCP 月 90/500  ·  5h 重置 19:42  ·  周重置 9/24 20:28"
            + "  ·  月重置 9/24 20:28  ·  额度更新 9/21 13:00", StringComparison.Ordinal),
            "the glm section", rendered);
        Expect(rendered.Contains("sections=11111111111111111", StringComparison.Ordinal)
            && rendered.Contains("windowHeight=505", StringComparison.Ordinal),
            "every section is visible and the window is full height", rendered);
        Expect(details.SectionCount == 17, "the detail panel keeps the 17 Windows children", rendered);

        // The trend chart, one render per range and provider, with the realised
        // geometry and the drawn colour asserted from the pixels.
        await RenderTrendAsync(details, report, directory, "monitor-details-ds-day.png",
            MonitorTrendRange.Day, MonitorTrendProvider.DeepSeek, expectPoints: true);
        await RenderTrendAsync(details, report, directory, "monitor-details-ds-week.png",
            MonitorTrendRange.Week, MonitorTrendProvider.DeepSeek, expectPoints: true);
        await RenderTrendAsync(details, report, directory, "monitor-details-ds-month.png",
            MonitorTrendRange.Month, MonitorTrendProvider.DeepSeek, expectPoints: true);
        await RenderTrendAsync(details, report, directory, "monitor-details-glm-day.png",
            MonitorTrendRange.Day, MonitorTrendProvider.Glm, expectPoints: true);
        await RenderTrendAsync(details, report, directory, "monitor-details-glm-week.png",
            MonitorTrendRange.Week, MonitorTrendProvider.Glm, expectPoints: true);

        // An empty series must draw the background and grid only.
        details.ChartForSmoke.SetData(null, FixedNow.Date, MonitorTheme.DeepSeekColor);
        await WaitForChartAsync(details, info => info.Empty);
        Save(details, Path.Combine(directory, "monitor-details-empty.png"));
        var empty = details.ChartForSmoke.LastRender!.Value;
        report.Add("chart-empty: " + empty);
        Expect(empty.PointCount == 0 && empty.Empty, "an empty series reports no points", empty.ToString());
        var emptyPixels = CountSeriesPixels(details, ChartRect(details));
        report.Add("pixels-monitor-details-empty.png: " + emptyPixels);
        Expect(emptyPixels.Green == 0 && emptyPixels.Purple == 0,
            "an empty chart draws no series", emptyPixels.ToString());

        // A single enabled provider gets the tall row; none leaves the HUD empty.
        module.ApplyProvidersForSmoke(new ProviderSettings
        {
            CodexEnabled = true, DeepSeekEnabled = false, GlmEnabled = false,
            DeepSeekSource = "Environment", DeepSeekEnvironment = DeepSeekVariable,
            GlmSource = "Environment", GlmEnvironment = GlmVariable
        });
        details.UpdateView(module.State);
        await SmokeWaiter.PumpAsync();
        Save(window, Path.Combine(directory, "monitor-compact-single.png"));
        var single = window.DescribeCompactForSmoke();
        report.Add("compact-single: " + single);
        Expect(single.Contains("codex=[5h 100%  ·  周 51%/#FFFFFFFF/h=48/vis=True]", StringComparison.Ordinal)
            && single.Contains("deepseek=[¥16.98 · 今约¥8.02/#FFFFFFFF/h=0/vis=False]", StringComparison.Ordinal)
            && single.Contains("footerH=24", StringComparison.Ordinal),
            "one provider gets the 48 tall row and the others collapse", single);
        // Children 0..16: the codex block stays, deepseek and glm collapse, the trend
        // collapses and the button row (child 16) is never hidden.
        Expect(details.DescribeDetailsForSmoke().Contains("sections=11111000000000001", StringComparison.Ordinal)
            && details.Height == 215,
            "the detail window keeps the codex block and shrinks by the other two and the trend",
            details.DescribeDetailsForSmoke());

        module.ApplyProvidersForSmoke(new ProviderSettings
        {
            CodexEnabled = false, DeepSeekEnabled = false, GlmEnabled = false,
            DeepSeekSource = "Environment", DeepSeekEnvironment = DeepSeekVariable,
            GlmSource = "Environment", GlmEnvironment = GlmVariable
        });
        await module.RefreshNowAsync();
        details.UpdateView(module.State);
        await SmokeWaiter.PumpAsync();
        Save(window, Path.Combine(directory, "monitor-compact-none.png"));
        Save(details, Path.Combine(directory, "monitor-details-none.png"));
        var none = window.DescribeCompactForSmoke();
        report.Add("compact-none: " + none);
        report.Add("details-none: " + details.DescribeDetailsForSmoke());
        // Windows hides a disabled provider's row instead of leaving a placeholder,
        // and it does not clear the already computed today estimate (Program.cs
        // L348-L355), so the collapsed glm row can still carry one. Only the
        // geometric collapse and the codex/deepseek states are asserted here; the
        // visible muted wording is pinned by the no-key phase below.
        Expect(none.Contains("codex=[未启用/#B4D2D6E0/h=0/vis=False]", StringComparison.Ordinal)
            && none.Contains("deepseek=[未启用/#B4D2D6E0/h=0/vis=False]", StringComparison.Ordinal)
            && none.Contains("glm=[", StringComparison.Ordinal)
            && none.Contains("/h=0/vis=False],footer=", StringComparison.Ordinal),
            "disabled providers collapse their rows and keep the muted state text", none);
        // Child 0 (the AI USAGE header) and child 16 (the buttons) are never collapsed.
        Expect(details.Height == 205 && details.DescribeDetailsForSmoke().Contains("sections=10000000000000001",
            StringComparison.Ordinal), "no provider leaves the detail window at its 205 floor",
            details.DescribeDetailsForSmoke());

        // Enabled but no key: the Windows "未配置 API Key" branch. Only DeepSeek is
        // left on so the muted wording is asserted on a visible row (a provider that
        // just went from enabled to disabled only clears its own fields in Windows,
        // Program.cs L336-L346, so its collapsed row can still hold stale text).
        module.ApplyProvidersForSmoke(new ProviderSettings
        {
            CodexEnabled = false, DeepSeekEnabled = true, GlmEnabled = false,
            DeepSeekSource = "Environment", DeepSeekEnvironment = DeepSeekVariable,
            GlmSource = "Environment", GlmEnvironment = GlmVariable
        });
        Environment.SetEnvironmentVariable(DeepSeekVariable, null);
        Environment.SetEnvironmentVariable(GlmVariable, null);
        await module.RefreshNowAsync();
        details.UpdateView(module.State);
        await SmokeWaiter.PumpAsync();
        Save(window, Path.Combine(directory, "monitor-compact-nokey.png"));
        Save(details, Path.Combine(directory, "monitor-details-nokey.png"));
        var noKey = window.DescribeCompactForSmoke();
        report.Add("compact-nokey: " + noKey);
        Expect(noKey.Contains("deepseek=[未配置 API Key/#B4D2D6E0/h=48/vis=True]", StringComparison.Ordinal)
            && noKey.Contains("footerH=24", StringComparison.Ordinal),
            "a missing key shows the Windows wording muted in the tall single row", noKey);

        window.CloseDetails();
        await SmokeWaiter.WaitOrThrowAsync(() => !window.IsDetailsOpen, Wait, () => "the detail window to close");
    }

    private static async Task RunSettingsPhaseAsync(string directory, List<string> report)
    {
        var providers = new ProviderSettings
        {
            CodexEnabled = true,
            DeepSeekEnabled = false,
            GlmEnabled = true,
            DeepSeekSource = "Environment",
            DeepSeekEnvironment = "DEEPSEEK_API_KEY",
            GlmSource = "Manual",
            GlmEnvironment = "ZHIPUAI_API_KEY"
        };
        var dialog = new MonitorProviderSettingsWindow(providers);
        dialog.Show();
        await SmokeWaiter.WaitOrThrowAsync(() => dialog.IsVisible, Wait, () => "the settings dialog to show");
        await SmokeWaiter.PumpAsync();
        Save(dialog, Path.Combine(directory, "monitor-settings.png"));
        var described = dialog.DescribeForSmoke();
        report.Add("settings: " + described);
        Expect(described.Contains("codexEnabled=True", StringComparison.Ordinal)
            && described.Contains("deepSeekEnv=True,deepSeekEnvName=DEEPSEEK_API_KEY,deepSeekEnvEnabled=True",
                StringComparison.Ordinal)
            && described.Contains("deepSeekKeyEnabled=False", StringComparison.Ordinal)
            && described.Contains("glmEnv=False", StringComparison.Ordinal)
            && described.Contains("glmKeyEnabled=True", StringComparison.Ordinal),
            "the dialog mirrors the stored switches and enables the matching input", described);
        dialog.Close();
        await SmokeWaiter.WaitOrThrowAsync(() => !dialog.IsVisible, Wait, () => "the settings dialog to close");
    }

    /// <summary>
    /// The refusal branches: a rejected key, and the real pay-as-you-go GLM answer
    /// (quota API error + account report wallet, which is what this machine's key
    /// really produces).
    /// </summary>
    private static async Task RunRefusalPhasesAsync(string directory, List<string> report)
    {
        // The no-key phase above deliberately cleared the smoke variables.
        Environment.SetEnvironmentVariable(DeepSeekVariable, SmokeKey);
        Environment.SetEnvironmentVariable(GlmVariable, SmokeKey);
        {
            var authStore = CreateStore(directory, "auth");
            authStore.SaveProviders(AllProviders());
            using var auth = new MonitorModule(new NullNotificationService(), edgeHideEnabled: false,
                authStore, MonitorFixtures.CreateService(MonitorFixtureScenario.AuthFailure), () => FixedNow)
            {
                TimersEnabled = false
            };
            auth.Start();
            await auth.RefreshNowAsync();
            var authWindow = auth.Window!;
            authWindow.AutoCloseDetailsOnDeactivate = false;
            await SmokeWaiter.PumpAsync();
            Save(authWindow, Path.Combine(directory, "monitor-compact-autherror.png"));
            var described = authWindow.DescribeCompactForSmoke();
            report.Add("compact-autherror: " + described);
            // Windows maps the GLM Chinese authentication body to "API Key 无效", but
            // the DeepSeek failure is only "DeepSeek HTTP 401", which matches none of
            // FriendlyError's patterns and therefore keeps the generic wording
            // (Program.cs L399-L415).
            Expect(described.Contains("deepseek=[DeepSeek 暂不可用/#B4D2D6E0/", StringComparison.Ordinal)
                && described.Contains("glm=[API Key 无效/#B4D2D6E0/", StringComparison.Ordinal),
                "a rejected key shows the Windows wording on both providers", described);
            auth.Stop();
        }

        {
            var planStore = CreateStore(directory, "noplan");
            planStore.SaveProviders(AllProviders());
            using var module = new MonitorModule(new NullNotificationService(), edgeHideEnabled: false,
                planStore, MonitorFixtures.CreateService(MonitorFixtureScenario.NoCodingPlanApiError), () => FixedNow)
            {
                TimersEnabled = false
            };
            module.Start();
            await module.RefreshNowAsync();
            var window = module.Window!;
            window.AutoCloseDetailsOnDeactivate = false;
            await SmokeWaiter.PumpAsync();
            Save(window, Path.Combine(directory, "monitor-compact-noplan.png"));
            var compact = window.DescribeCompactForSmoke();
            report.Add("compact-noplan: " + compact);
            Expect(compact.Contains("glm=[¥3.58 · 今约0/#FFFFC65C/h=20/vis=True]", StringComparison.Ordinal),
                "the real no-coding-plan answer keeps the wallet and turns amber", compact);
            Expect(compact.Contains("codex=[5h 100%  ·  周 51%/#FFFFFFFF/h=20/vis=True]", StringComparison.Ordinal),
                "the codex row is untouched by the glm degradation", compact);

            window.ToggleDetails();
            await SmokeWaiter.WaitOrThrowAsync(() => window.IsDetailsOpen, Wait, () => "the detail window to open");
            var details = window.DetailsForSmoke!;
            await SmokeWaiter.PumpAsync();
            Save(details, Path.Combine(directory, "monitor-details-noplan.png"));
            var rendered = details.DescribeDetailsForSmoke();
            report.Add("details-noplan: " + rendered);
            Expect(rendered.Contains("glm=余额 ¥3.58|累计消费 ¥16.42  ·  余额正常 · 套餐配额不可用",
                StringComparison.Ordinal),
                "the detail window reports the quota outage in the Windows wording", rendered);
            window.CloseDetails();
        }
    }

    // --------------------------------------------------------------- rendering

    private static async Task RenderTrendAsync(MonitorDetailsWindow details, List<string> report, string directory,
        string file, MonitorTrendRange range, MonitorTrendProvider provider, bool expectPoints)
    {
        details.SelectRange(range);
        details.SelectProvider(provider);
        await SmokeWaiter.PumpAsync();
        details.UpdateTrend();
        RenderOnce(details);
        Save(details, Path.Combine(directory, file));

        var info = details.ChartForSmoke.LastRender
            ?? throw new InvalidOperationException(file + ": the chart did not paint.");
        var currency = provider == MonitorTrendProvider.Glm ? "¥" : "¥";
        report.Add("chart-" + file + ": " + info + " | " + details.DescribeDetailsForSmoke());
        Expect(info.PointCount > 0 == expectPoints, file + " point count", info.ToString());
        _ = expectPoints;
        Expect(info.PlotWidth > 250 && info.PlotHeight > 60, file + " plot is laid out", info.ToString());
        Expect(info.Maximum > 0, file + " has a positive maximum", info.ToString());
        Expect(info.LastX >= 0 && info.LastX <= info.Width && info.LastY >= 0 && info.LastY <= info.Height,
            file + " last point is inside the chart", info.ToString());
        Expect(details.ChartForSmoke.LastSeriesColor == MonitorTheme.SeriesColor(provider == MonitorTrendProvider.Glm),
            file + " series colour", details.ChartForSmoke.LastSeriesColor.ToString());
        Expect(details.DescribeDetailsForSmoke().Contains("axis=" + currency, StringComparison.Ordinal),
            file + " axis carries the currency", details.DescribeDetailsForSmoke());
        // The axis follows the injected clock, so the day range is exactly the
        // seeded span and the week/month ranges start on their boundary.
        // Windows labels the axis from the tracking start: with a baseline before the
        // range it is the range boundary (midnight, Monday), without one it is the
        // first sample (the month range has no August sample, so it starts 9/15).
        // The DeepSeek maximum is larger over the month because the seed contains a
        // rise that the Windows approximation does not net out of the later drops.
        var expectedAxis = (range, provider) switch
        {
            (MonitorTrendRange.Day, MonitorTrendProvider.Glm) => "axis=¥16.4155,¥0,00:00,13:00",
            (MonitorTrendRange.Day, _) => "axis=¥8.02,¥0,00:00,13:00",
            (MonitorTrendRange.Week, MonitorTrendProvider.Glm) => "axis=¥16.4155,¥0,9/21,9/21",
            (MonitorTrendRange.Week, _) => "axis=¥8.02,¥0,9/21,9/21",
            (MonitorTrendRange.Month, MonitorTrendProvider.Glm) => "axis=¥16.4155,¥0,9/15,9/21",
            _ => "axis=¥28.02,¥0,9/15,9/21"
        };
        Expect(details.DescribeDetailsForSmoke().Contains(expectedAxis, StringComparison.Ordinal),
            file + " axis labels", details.DescribeDetailsForSmoke());

        var pixels = CountSeriesPixels(details, ChartRect(details));
        report.Add("pixels-" + file + ": " + pixels);
        if (provider == MonitorTrendProvider.DeepSeek)
            Expect(pixels.Green > 0 && pixels.Purple == 0, file + " draws the green DeepSeek series",
                pixels.ToString());
        else
            Expect(pixels.Purple > 0 && pixels.Green == 0, file + " draws the purple GLM series",
                pixels.ToString());
    }

    /// <summary>The opt-in real pass: the live hosts through the real service.</summary>
    private static async Task RunLiveAsync(string directory)
    {
        var store = CreateStore(directory, "live");
        // Manual source: the suite's keyring/credentials.json holds this machine's keys.
        store.SaveProviders(new ProviderSettings
        {
            CodexEnabled = true,
            DeepSeekEnabled = true,
            GlmEnabled = true,
            DeepSeekSource = "Manual",
            DeepSeekEnvironment = "DEEPSEEK_API_KEY",
            GlmSource = "Manual",
            GlmEnvironment = "ZHIPUAI_API_KEY"
        });
        using var module = new MonitorModule(new NullNotificationService(), edgeHideEnabled: false, store, null,
            () => DateTime.Now)
        {
            TimersEnabled = false
        };
        module.Start();
        var window = module.Window!;
        window.AutoCloseDetailsOnDeactivate = false;
        try
        {
            await module.RefreshNowAsync();
            await SmokeWaiter.PumpAsync();
            var liveDirectory = Path.Combine(directory, "live");
            Directory.CreateDirectory(liveDirectory);
            Save(window, Path.Combine(liveDirectory, "monitor-live-compact.png"));
            window.ToggleDetails();
            await SmokeWaiter.WaitOrThrowAsync(() => window.IsDetailsOpen, TimeSpan.FromSeconds(3),
                () => "the live detail window to open");
            var details = window.DetailsForSmoke
                ?? throw new InvalidOperationException("the live detail window did not open");
            await SmokeWaiter.PumpAsync();
            details.UpdateView(module.State);
            await SmokeWaiter.PumpAsync();
            Save(details, Path.Combine(liveDirectory, "monitor-live-details.png"));

            var state = module.State;
            var lines = new List<string>
            {
                "codexAvailability=" + module.Service.Codex.Describe(),
                "codexState=" + (state.CodexState ?? "none"),
                "codex5h=" + state.FiveHourRemaining + " weekly=" + state.WeeklyRemaining
                    + " plan=" + (state.PlanType ?? "none") + " credits=" + (state.CodexCredits ?? "none"),
                "deepseekState=" + (state.DeepSeekState ?? "none") + " balance=" + (state.DeepSeekBalance ?? "none")
                    + " today=" + state.TodayDeepSeekSpend,
                "glmState=" + (state.GlmState ?? "none") + " balance=" + state.GlmBalance
                    + " totalSpend=" + state.GlmTotalSpend + " 5h=" + state.GlmFiveHourRemaining
                    + " weekly=" + state.GlmWeeklyRemaining + " monthly=" + state.GlmMonthlyRemaining,
                "walletFallbacks=" + module.Service.WalletFallbackCount + " last="
                    + (module.Service.LastWalletFallbackReason ?? "none"),
                "compact=" + window.DescribeCompactForSmoke(),
                "details=" + details.DescribeDetailsForSmoke()
            };
            File.WriteAllText(Path.Combine(liveDirectory, "monitor-live.txt"),
                string.Join('\n', lines) + Environment.NewLine);
            Console.WriteLine(string.Join('\n', lines));
            Expect(state.UpdatedAt.HasValue, "the live refresh updated the state", "UpdatedAt is null");
            window.CloseDetails();
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(directory, "monitor-live.error.txt"), exception.ToString());
            throw new InvalidOperationException("The live monitor pass failed: " + exception.Message, exception);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static ProviderSettings AllProviders() => new()
    {
        CodexEnabled = true,
        DeepSeekEnabled = true,
        GlmEnabled = true,
        DeepSeekSource = "Environment",
        DeepSeekEnvironment = DeepSeekVariable,
        GlmSource = "Environment",
        GlmEnvironment = GlmVariable
    };

    /// <summary>
    /// Seeds the two history files so the trend has a real curve: the samples span
    /// the morning, one sample sits before today (the baseline the tracker reuses)
    /// and one before this week, so the day, week and month ranges all differ.
    /// </summary>
    private static void SeedHistory(MonitorStore store)
    {
        store.SaveBalanceSamples(
        [
            new BalanceSample { Timestamp = new DateTime(2026, 9, 15, 10, 0, 0), Balance = 40, Currency = "¥" },
            new BalanceSample { Timestamp = new DateTime(2026, 9, 20, 10, 0, 0), Balance = 20, Currency = "¥" },
            new BalanceSample { Timestamp = new DateTime(2026, 9, 21, 9, 0, 0), Balance = 25, Currency = "¥" },
            new BalanceSample { Timestamp = new DateTime(2026, 9, 21, 11, 0, 0), Balance = 22, Currency = "¥" },
            new BalanceSample { Timestamp = new DateTime(2026, 9, 21, 12, 30, 0), Balance = 20, Currency = "¥" }
        ]);
        store.SaveGlmSamples(
        [
            new GlmSpendSample
            {
                Timestamp = new DateTime(2026, 9, 15, 10, 0, 0), Balance = 40, TotalSpend = 0, Currency = "¥"
            },
            new GlmSpendSample
            {
                Timestamp = new DateTime(2026, 9, 21, 9, 0, 0), Balance = 20, TotalSpend = 0, Currency = "¥"
            },
            new GlmSpendSample
            {
                Timestamp = new DateTime(2026, 9, 21, 11, 0, 0), Balance = 18, TotalSpend = 4.5, Currency = "¥"
            },
            new GlmSpendSample
            {
                Timestamp = new DateTime(2026, 9, 21, 12, 30, 0), Balance = 16, TotalSpend = 9, Currency = "¥"
            }
        ]);
    }

    private static MonitorStore CreateStore(string directory, string name)
    {
        var path = Path.Combine(directory, "data-" + name);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        Directory.CreateDirectory(path);
        return new MonitorStore(path);
    }

    private static async Task<string> WaitForDescriptionAsync(Func<string> describe, Func<string, bool> accept,
        string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        var last = describe();
        while (!accept(last) && DateTime.UtcNow < deadline)
        {
            await SmokeWaiter.PumpAsync();
            last = describe();
        }
        if (!accept(last)) throw new InvalidOperationException("Timed out waiting for " + what + ": " + last);
        return last;
    }

    private static async Task WaitForChartAsync(MonitorDetailsWindow details, Func<MonitorChartRenderInfo, bool> accept)
    {
        var deadline = DateTime.UtcNow + Wait;
        MonitorChartRenderInfo? last = null;
        while (DateTime.UtcNow < deadline)
        {
            RenderOnce(details);
            last = details.ChartForSmoke.LastRender;
            if (last is not null && accept(last.Value)) return;
            await SmokeWaiter.PumpAsync();
        }
        throw new InvalidOperationException("The chart never reached the expected state. Last render: "
            + (last?.ToString() ?? "no-render"));
    }

    private static RenderTargetBitmap CreateTarget(Window window)
    {
        var scale = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale)));
        return new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
    }

    private static void RenderOnce(Window window)
    {
        using var bitmap = CreateTarget(window);
        bitmap.Render(window);
    }

    private static void Save(Window window, string path)
    {
        using var bitmap = CreateTarget(window);
        bitmap.Render(window);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    private static PixelRect ChartRect(MonitorDetailsWindow details)
    {
        var chart = details.ChartForSmoke;
        var scale = details.RenderScaling <= 0 ? 1 : details.RenderScaling;
        var origin = chart.TranslatePoint(new Point(0, 0), details) ?? new Point(0, 0);
        return new PixelRect(
            (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
            Math.Max(1, (int)Math.Round(chart.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Round(chart.Bounds.Height * scale)));
    }

    /// <summary>
    /// Counts the series pixels inside the chart rectangle by hue rather than by an
    /// exact colour, because the Windows pen is ARGB(225) and therefore composites
    /// over the (translucent) panel. Green means "clearly more green than red and
    /// blue" (DeepSeek 124,237,174), purple means "more red and blue than green"
    /// (GLM 190,154,255); the panel itself is neutral, so neither predicate fires on
    /// the background, the grid or the axis labels.
    /// </summary>
    private static (int Green, int Purple, int Neutral) CountSeriesPixels(MonitorDetailsWindow details, PixelRect rect)
    {
        using var bitmap = CreateTarget(details);
        bitmap.Render(details);
        var size = bitmap.PixelSize;
        var width = Math.Min(rect.Width, size.Width - rect.X);
        var height = Math.Min(rect.Height, size.Height - rect.Y);
        if (width <= 0 || height <= 0) return (0, 0, 0);
        var source = new PixelRect(rect.X, rect.Y, width, height);
        var stride = width * 4;
        var buffer = Marshal.AllocHGlobal(stride * height);
        try
        {
            bitmap.CopyPixels(source, buffer, stride * height, stride);
            var bytes = new byte[stride * height];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var bgra = (bitmap.Format ?? PixelFormat.Bgra8888).Equals(PixelFormat.Bgra8888);
            int green = 0, purple = 0, neutral = 0;
            for (var index = 0; index + 3 < bytes.Length; index += 4)
            {
                var c0 = bytes[index];
                var c1 = bytes[index + 1];
                var c2 = bytes[index + 2];
                int blue = bgra ? c0 : c2;
                int greenChannel = c1;
                int red = bgra ? c2 : c0;
                if (greenChannel - red >= 6 && greenChannel - blue >= 6) green++;
                else if (red - greenChannel >= 6 && blue - greenChannel >= 6) purple++;
                else neutral++;
            }
            return (green, purple, neutral);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void Expect(bool condition, string name, string detail)
    {
        if (!condition)
            throw new InvalidOperationException("Unexpected " + name + ": " + detail
                + " (tolerance " + Tolerance.ToString(CultureInfo.InvariantCulture) + ")");
    }
}
