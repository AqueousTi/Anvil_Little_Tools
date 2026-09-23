using System.Globalization;
using LittleTools.Assistant.Monitor;

/// <summary>
/// Unit tests for the monitor's pure logic: the JavaScriptSerializer compatible
/// JSON, the shared-file store and import path, the two usage trackers, the
/// provider parsers and the composed UI strings. Everything here runs without a
/// network and without a display; the same expectations are asserted on the real
/// controls by <c>--monitor-smoke</c> (Monitor/MonitorSmoke.cs).
/// </summary>
internal static class MonitorCoreTests
{
    public static void Run(Action<string, bool> check)
    {
        CheckJson(check);
        CheckStore(check);
        CheckTrackers(check);
        CheckProviders(check);
        CheckTextAndLayout(check);
    }

    // ------------------------------------------------------------------- JSON

    private static void CheckJson(Action<string, bool> check)
    {
        var state = new UsageSnapshot
        {
            CodexState = "正常",
            FiveHourRemaining = 62.5,
            FiveHourReset = new DateTime(2026, 9, 21, 18, 30, 0),
            UpdatedAt = new DateTime(2026, 9, 21, 13, 0, 0),
            GlmEnabled = false,
            DeepSeekCurrencySymbol = "¥",
            GlmTodayPoints = [new UsagePoint { Timestamp = new DateTime(2026, 9, 21, 9, 0, 0), Value = 1.25 }]
        };
        var json = MonitorJson.Serialize(state);
        check("snapshot date uses the Windows escaped epoch form", json.Contains("\"\\/Date(", StringComparison.Ordinal));
        check("snapshot date is not ISO 8601", !json.Contains("2026-09-21T", StringComparison.Ordinal));
        check("snapshot keeps the Windows field names",
            json.Contains("\"FiveHourRemaining\"", StringComparison.Ordinal)
            && json.Contains("\"GlmTodayPoints\"", StringComparison.Ordinal)
            && json.Contains("\"Timestamp\"", StringComparison.Ordinal));
        check("snapshot writes Chinese raw, not \\uXXXX", json.Contains("正常", StringComparison.Ordinal)
            && !json.Contains("\\u6b63", StringComparison.OrdinalIgnoreCase));

        var back = MonitorJson.Deserialize<UsageSnapshot>(json)
            ?? throw new InvalidOperationException("snapshot did not round trip");
        check("snapshot round trips the reset time", back.FiveHourReset == state.FiveHourReset);
        check("snapshot round trips the pending flag", back.FiveHourRemaining == 62.5 && !back.GlmEnabled);
        check("snapshot round trips the points",
            back.GlmTodayPoints.Count == 1 && back.GlmTodayPoints[0].Value == 1.25
            && back.GlmTodayPoints[0].Timestamp == new DateTime(2026, 9, 21, 9, 0, 0));

        check("epoch milliseconds are UTC based",
            JavaScriptSerializerDateConverter.ToUnixMilliseconds(new DateTime(1970, 1, 1, 0, 0, 1, DateTimeKind.Utc)) == 1000);
        check("windows date text is parsed",
            JavaScriptSerializerDateConverter.TryParseMsDate("\\/Date(1790163759000)\\/", out var parsed)
            && parsed.ToUniversalTime() == new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(1790163759));
        check("unescaped date text is parsed too",
            JavaScriptSerializerDateConverter.TryParseMsDate("/Date(0)/", out var epoch)
            && epoch == new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime());

        // A verbatim file as JavaScriptSerializer writes it, including the bare NaN
        // literal only that serializer produces.
        var windowsHistory = "[{\"Timestamp\":\"\\/Date(1790163759000)\\/\",\"Balance\":128.4,\"Currency\":\"¥\"},"
            + "{\"Timestamp\":\"\\/Date(1790163819000)\\/\",\"Balance\":NaN,\"Currency\":null}]";
        var samples = MonitorJson.Deserialize<List<BalanceSample>>(windowsHistory)
            ?? throw new InvalidOperationException("windows history did not parse");
        check("windows history parses both samples", samples.Count == 2);
        check("windows history keeps the balance and currency",
            samples[0].Balance == 128.4 && samples[0].Currency == "¥");
        check("bare NaN is masked to a non finite double", double.IsNaN(samples[1].Balance));

        var providers = MonitorJson.Serialize(new ProviderSettings
        {
            GlmEnabled = true,
            GlmSource = "Manual",
            GlmProtectedKey = "blob"
        });
        check("providers.json keeps the Windows field names",
            providers.Contains("\"CodexEnabled\"", StringComparison.Ordinal)
            && providers.Contains("\"DeepSeekSource\"", StringComparison.Ordinal)
            && providers.Contains("\"DeepSeekProtectedKey\"", StringComparison.Ordinal)
            && providers.Contains("\"GlmEnvironment\"", StringComparison.Ordinal));
        var stored = MonitorJson.Deserialize<ProviderSettings>(
            "{\"CodexEnabled\":true,\"DeepSeekEnabled\":true,\"GlmEnabled\":false,"
            + "\"DeepSeekSource\":\"Environment\",\"DeepSeekEnvironment\":\"DEEPSEEK_API_KEY\","
            + "\"DeepSeekProtectedKey\":null,\"GlmSource\":\"Environment\","
            + "\"GlmEnvironment\":\"ZHIPUAI_API_KEY\",\"GlmProtectedKey\":null}")
            ?? throw new InvalidOperationException("providers.json did not parse");
        check("providers.json defaults match Windows",
            stored.CodexEnabled && stored.DeepSeekEnabled && !stored.GlmEnabled
            && stored.DeepSeekEnvironment == "DEEPSEEK_API_KEY"
            && stored.GlmEnvironment == "ZHIPUAI_API_KEY");
    }

    // ------------------------------------------------------------------ store

    private static void CheckStore(Action<string, bool> check)
    {
        WithTemporaryRoot(root =>
        {
            var source = Path.Combine(root, "windows", "LittleTools", "AIUsageMonitor");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "providers.json"),
                "{\"CodexEnabled\":false,\"GlmEnabled\":true,\"GlmSource\":\"Environment\"}");
            File.WriteAllText(Path.Combine(source, "snapshot.json"),
                "{\"FiveHourRemaining\":80.0,\"CodexState\":\"正常\",\"DeepSeekEnabled\":true,\"GlmEnabled\":true,"
                + "\"UpdatedAt\":\"\\/Date(1790163759000)\\/\"}");

            Environment.SetEnvironmentVariable(MonitorStore.DirectoryVariable, source);
            var target = Path.Combine(root, "monitor");
            var store = new MonitorStore(target);
            var providers = store.LoadProviders();
            check("import adopts the Windows providers.json", !providers.CodexEnabled && providers.GlmEnabled);
            check("import reports its source",
                store.ImportedFrom.Any(path => path == Path.Combine(source, "providers.json")));
            check("import copies the snapshot too", store.ImportedFrom.Count == 2);
            check("import never modifies the source",
                File.ReadAllText(Path.Combine(source, "providers.json")).Contains("\"GlmEnabled\":true", StringComparison.Ordinal));
            check("import writes into the Linux folder",
                File.Exists(Path.Combine(target, "providers.json")) && File.Exists(Path.Combine(target, "snapshot.json")));
            var state = store.LoadSnapshot() ?? throw new InvalidOperationException("imported snapshot missing");
            check("imported snapshot keeps its values", state.FiveHourRemaining == 80.0 && state.CodexState == "正常");
            check("imported snapshot gets its point lists repaired",
                state.GlmTodayPoints.Count == 0 && state.DeepSeekWeekPoints.Count == 0);

            // The second store must not overwrite an existing file.
            File.WriteAllText(Path.Combine(target, "providers.json"),
                "{\"CodexEnabled\":true,\"GlmEnabled\":false}");
            var second = new MonitorStore(target);
            var kept = second.LoadProviders();
            check("an existing Linux file is not overwritten", kept.CodexEnabled && !kept.GlmEnabled);
            check("nothing is imported over an existing file", second.ImportedFrom.Count == 0);

            // A file path in the override names the folder to import from.
            var other = Path.Combine(root, "other");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "glm-usage-history.json"),
                "[{\"Timestamp\":\"\\/Date(1790163759000)\\/\",\"Balance\":52.35,\"TotalSpend\":147.65,\"Currency\":\"CNY\"}]");
            Environment.SetEnvironmentVariable(MonitorStore.DirectoryVariable, Path.Combine(other, "glm-usage-history.json"));
            var third = new MonitorStore(Path.Combine(root, "monitor3"));
            var glmSamples = third.LoadGlmSamples();
            check("a file override imports the folder that holds it",
                glmSamples.Count == 1 && glmSamples[0].TotalSpend == 147.65);
            check("the explicit override is reported as the import source",
                third.ImportedFrom.Any(path => path.EndsWith("glm-usage-history.json", StringComparison.Ordinal)));

            Environment.SetEnvironmentVariable(MonitorStore.DirectoryVariable, null);
            var saved = new MonitorStore(Path.Combine(root, "monitor4"));
            check("saving providers succeeds", saved.SaveProviders(new ProviderSettings { GlmEnabled = true }));
            var bytes = File.ReadAllBytes(saved.ProvidersPath);
            check("shared files are UTF-8 without a BOM",
                bytes.Length > 3 && bytes[0] != 0xEF && bytes[1] != 0xBB && bytes[2] != 0xBF);
            check("no temporary file is left behind", !File.Exists(saved.ProvidersPath + ".tmp"));
            check("providers reload after a save", saved.LoadProviders().GlmEnabled);

            var candidates = MonitorStore.LegacyCandidates(Path.Combine(root, "monitor")).ToArray();
            check("legacy candidates include the Windows folder under the data home",
                candidates.Any(path => path.EndsWith(Path.Combine("LittleTools", "AIUsageMonitor"), StringComparison.Ordinal)));
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")!;
            check("legacy candidates include the old AIUsageMonitor folder",
                candidates.Contains(Path.Combine(dataHome, "AIUsageMonitor"))
                && candidates.Contains(Path.Combine(dataHome, "LittleTools", "AIUsageMonitor")));
        });
    }

    // --------------------------------------------------------------- trackers

    private static void CheckTrackers(Action<string, bool> check)
    {
        WithTemporaryRoot(root =>
        {
            var now = new DateTime(2026, 9, 21, 13, 0, 0);
            var store = new MonitorStore(Path.Combine(root, "deepseek"));
            store.SaveBalanceSamples(
            [
                new BalanceSample { Timestamp = new DateTime(2026, 9, 21, 9, 0, 0), Balance = 100, Currency = "¥" },
                new BalanceSample { Timestamp = new DateTime(2026, 9, 21, 11, 0, 0), Balance = 95.5, Currency = "¥" }
            ]);
            var tracker = new MonitorUsageTracker(store, () => now);
            var state = new UsageSnapshot
            {
                DeepSeekCurrentBalance = 90,
                DeepSeekCurrencySymbol = "¥"
            };
            tracker.UpdateDeepSeek(state, now);
            check("deepseek samples the new balance", store.LoadBalanceSamples().Count == 3);
            check("deepseek today spend sums the balance decreases", state.TodayDeepSeekSpend == 10);
            check("deepseek week and month match for a single day",
                state.WeeklyDeepSeekSpend == 10 && state.MonthlyDeepSeekSpend == 10);
            check("deepseek tracking starts at the first sample of the day",
                state.DeepSeekTrackingStart == new DateTime(2026, 9, 21, 9, 0, 0));
            check("deepseek points are cumulative with a zero origin",
                state.DeepSeekTodayPoints.Count == 3
                && state.DeepSeekTodayPoints[0].Value == 0
                && state.DeepSeekTodayPoints[1].Value == 4.5
                && state.DeepSeekTodayPoints[2].Value == 10);

            // Inside the sampling interval and unchanged: no new sample.
            tracker.UpdateDeepSeek(state, now.AddSeconds(30));
            check("deepseek does not sample twice within a minute", store.LoadBalanceSamples().Count == 3);
            // Later, or changed: it samples again.
            state.DeepSeekCurrentBalance = 88;
            tracker.UpdateDeepSeek(state, now.AddMinutes(2));
            check("deepseek samples after the interval", store.LoadBalanceSamples().Count == 4);
            check("deepseek spend follows the new balance", state.TodayDeepSeekSpend == 12);

            // A rise (top-up) is not negative spend.
            state.DeepSeekCurrentBalance = 200;
            tracker.UpdateDeepSeek(state, now.AddMinutes(4));
            check("a top-up does not reduce the spend", state.TodayDeepSeekSpend == 12);

            // The baseline from before the day boundary is reused, so a restart does
            // not lose the spend that crossed midnight.
            var cross = Path.Combine(root, "cross");
            var crossStore = new MonitorStore(cross);
            crossStore.SaveBalanceSamples(
            [
                new BalanceSample { Timestamp = new DateTime(2026, 9, 20, 23, 0, 0), Balance = 200, Currency = "¥" },
                new BalanceSample { Timestamp = new DateTime(2026, 9, 21, 8, 0, 0), Balance = 190, Currency = "¥" }
            ]);
            var crossState = new UsageSnapshot { DeepSeekCurrentBalance = 190, DeepSeekCurrencySymbol = "¥" };
            new MonitorUsageTracker(crossStore, () => new DateTime(2026, 9, 21, 9, 0, 0)).UpdateDeepSeek(crossState,
                new DateTime(2026, 9, 21, 9, 0, 0));
            check("yesterday's sample becomes the baseline",
                crossState.DeepSeekTrackingStart == new DateTime(2026, 9, 21).Date
                && crossState.DeepSeekTodayPoints.Count == 3
                && crossState.DeepSeekTodayPoints[0].Value == 0
                && crossState.TodayDeepSeekSpend == 10);

            // Retention drops anything older than 35 days.
            var old = Path.Combine(root, "old");
            var oldStore = new MonitorStore(old);
            oldStore.SaveBalanceSamples(
            [
                new BalanceSample { Timestamp = new DateTime(2026, 7, 1, 9, 0, 0), Balance = 500, Currency = "¥" },
                new BalanceSample { Timestamp = new DateTime(2026, 9, 21, 9, 0, 0), Balance = 100, Currency = "¥" }
            ]);
            var oldState = new UsageSnapshot { DeepSeekCurrentBalance = 90, DeepSeekCurrencySymbol = "¥" };
            new MonitorUsageTracker(oldStore, () => now).UpdateDeepSeek(oldState, now);
            var pruned = oldStore.LoadBalanceSamples();
            check("history older than 35 days is dropped",
                pruned.Count == 2 && pruned.All(sample => sample.Timestamp >= new DateTime(2026, 8, 17)));

            // GLM prefers the cumulative spend from the account report.
            var glmStore = new MonitorStore(Path.Combine(root, "glm"));
            glmStore.SaveGlmSamples(
            [
                new GlmSpendSample { Timestamp = new DateTime(2026, 9, 21, 10, 0, 0), Balance = 50, TotalSpend = 100, Currency = "¥" },
                new GlmSpendSample { Timestamp = new DateTime(2026, 9, 21, 12, 0, 0), Balance = 30, TotalSpend = 130, Currency = "¥" }
            ]);
            var glmState = new UsageSnapshot
            {
                GlmBalance = 20,
                GlmTotalSpend = 150,
                GlmCurrencySymbol = "¥",
                GlmWalletUpdatedAt = now
            };
            var glmTracker = new MonitorUsageTracker(glmStore, () => now);
            glmTracker.UpdateGlm(glmState, now);
            check("glm samples the wallet", glmStore.LoadGlmSamples().Count == 3);
            check("glm spend uses the cumulative account total, not the balance drop",
                glmState.TodayGlmSpend == 50);
            check("glm points are cumulative from zero",
                glmState.GlmTodayPoints.Count == 3 && glmState.GlmTodayPoints[^1].Value == 50);

            // Only the older balance endpoint answered: degrade to balance drops.
            var legacyStore = new MonitorStore(Path.Combine(root, "glm-legacy"));
            legacyStore.SaveGlmSamples(
            [
                new GlmSpendSample { Timestamp = new DateTime(2026, 9, 21, 10, 0, 0), Balance = 100 },
                new GlmSpendSample { Timestamp = new DateTime(2026, 9, 21, 12, 0, 0), Balance = 90 }
            ]);
            var legacyState = new UsageSnapshot { GlmBalance = 80, GlmWalletUpdatedAt = now };
            new MonitorUsageTracker(legacyStore, () => now).UpdateGlm(legacyState, now);
            check("glm falls back to balance drops without a cumulative total", legacyState.TodayGlmSpend == 20);

            // A stale wallet timestamp is not sampled again.
            var staleStore = new MonitorStore(Path.Combine(root, "glm-stale"));
            var staleState = new UsageSnapshot
            {
                GlmBalance = 10,
                GlmWalletUpdatedAt = now.AddMinutes(-5)
            };
            new MonitorUsageTracker(staleStore, () => now).UpdateGlm(staleState, now);
            check("a stale wallet is not sampled", staleStore.LoadGlmSamples().Count == 0);

            var (today, weekStart, monthStart) = MonitorUsageTracker.Boundaries(new DateTime(2026, 9, 23, 14, 0, 0));
            check("day boundary is midnight", today == new DateTime(2026, 9, 23));
            check("week starts on Monday", weekStart == new DateTime(2026, 9, 21) && weekStart.DayOfWeek == DayOfWeek.Monday);
            check("month starts on the first", monthStart == new DateTime(2026, 9, 1));
            var sunday = MonitorUsageTracker.Boundaries(new DateTime(2026, 9, 27, 14, 0, 0));
            check("a Sunday still belongs to the Monday week", sunday.WeekStart == new DateTime(2026, 9, 21));
            check("same() follows the Windows epsilon",
                MonitorUsageTracker.Same(1.0, 1.0000001) && !MonitorUsageTracker.Same(1.0, 1.01)
                && !MonitorUsageTracker.Same(null, 1.0) && MonitorUsageTracker.Same(null, null));
        });
    }

    // -------------------------------------------------------------- providers

    private static void CheckProviders(Action<string, bool> check)
    {
        // Codex: the recorded live reply.
        var usage = SystemCodexAppServer.ParseMessage(MonitorFixtures.ReadText("codex-rate-limits.json"));
        check("codex parses the primary window",
            usage.Primary is { UsedPercent: 0, WindowDurationMins: 300 });
        check("codex parses the secondary window",
            usage.Secondary is { UsedPercent: 49, WindowDurationMins: 10080 });
        check("codex reset time is the epoch instant",
            usage.Primary!.ResetsAt!.Value.ToUniversalTime()
                == new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(1790163759));
        check("codex parses the plan and credits", usage.PlanType == "plus" && usage.Credits == "0");

        var state = new UsageSnapshot();
        MonitorLayout.ApplyRateWindow(state, usage.Primary);
        MonitorLayout.ApplyRateWindow(state, usage.Secondary);
        check("a 300 minute window is the five hour quota",
            state.FiveHourRemaining == 100 && state.WeeklyRemaining == 51);
        check("a 10080 minute window is the weekly quota", state.WeeklyReset == usage.Secondary!.ResetsAt);

        var authError = Capture(() => SystemCodexAppServer.ParseMessage(
            MonitorFixtures.ReadText("codex-auth-required.json")));
        check("codex reports the app-server error",
            authError is InvalidOperationException && authError.Message.Contains("authentication required", StringComparison.Ordinal));
        check("codex auth error maps to the Windows login hint",
            MonitorLayout.FriendlyError(authError!, "Codex 暂不可用 · 显示上次数据") == "需要 Codex 登录");

        check("no executable and no desktop is 未运行",
            SystemCodexAppServer.Classify([], null) == CodexAvailability.None);
        check("a CLI only install is queryable",
            SystemCodexAppServer.Classify([], "/usr/bin/codex") == CodexAvailability.CliOnly);
        check("the desktop bundle counts as running",
            SystemCodexAppServer.Classify(["/usr/lib/chatgpt/ChatGPT"], null) == CodexAvailability.DesktopRunning
            && SystemCodexAppServer.Classify(["/usr/lib/chatgpt/codex-launcher"], null) == CodexAvailability.DesktopRunning);
        check("an unrelated process is not the desktop app",
            SystemCodexAppServer.Classify(["/usr/bin/firefox"], "/usr/bin/codex") == CodexAvailability.CliOnly);

        // DeepSeek.
        var balance = MonitorDataService.ParseDeepSeek(MonitorFixtures.ReadText("deepseek-balance.json"));
        check("deepseek reports availability", balance.Available);
        check("deepseek reads the recorded balance",
            balance.TotalDisplay == "¥16.98" && balance.PrimaryBalance == 16.98 && balance.CurrencySymbol == "¥");
        check("deepseek shows the topped up and granted split",
            balance.Breakdown == "¥充值 16.98 · 赠送 0");
        var multi = MonitorDataService.ParseDeepSeek(MonitorFixtures.ReadText("deepseek-balance-multi.json"));
        check("deepseek joins every currency", multi.TotalDisplay == "¥128.4 / $3.5");
        check("deepseek keeps a per currency breakdown",
            multi.Breakdown == "¥充值 120 · 赠送 8.4   $充值 3.5 · 赠送 0"
            && multi.PrimaryBalance == 128.40 && multi.CurrencySymbol == "¥");
        var drained = MonitorDataService.ParseDeepSeek(MonitorFixtures.ReadText("deepseek-empty-balance.json"));
        check("a drained account is flagged", !drained.Available && drained.PrimaryBalance == 0);
        var noBalance = Capture(() => MonitorDataService.ParseDeepSeek("{\"is_available\":true}"));
        check("a missing balance is an error",
            noBalance is InvalidOperationException && noBalance.Message == "DeepSeek returned no balance");
        check("deepseek trims money like Windows",
            MonitorDataService.TrimMoney("128.40") == "128.4" && MonitorDataService.TrimMoney("0.00") == "0"
            && MonitorDataService.TrimMoney("3.14159") == "3.14");

        // GLM quota.
        var glm = MonitorDataService.ParseGlmQuota(MonitorFixtures.ReadText("glm-quota-limits.json"));
        check("glm reads the coding plan level", glm.QuotaRead && glm.Level == "pro");
        check("glm classifies the three windows",
            glm.FiveHour is { UsedPercent: 37.5 } && glm.Weekly is { UsedPercent: 62 } && glm.Monthly is { UsedPercent: 18 });
        check("glm window durations follow unit and number",
            glm.FiveHour!.WindowDurationMins == 300 && glm.Weekly!.WindowDurationMins == 10080
            && glm.Monthly!.WindowDurationMins == 30 * 24 * 60);
        check("glm window remaining is the inverted percentage",
            MonitorLayout.Remaining(glm.FiveHour) == 62.5 && MonitorLayout.Remaining(glm.Weekly) == 38
            && MonitorLayout.Remaining(glm.Monthly) == 82);
        check("glm breakdown keeps the Windows labels and separators",
            glm.Breakdown == "5h 1.23M/5M  ·  周 3.1M/5M  ·  MCP 月 90/500");
        check("glm reset times convert milliseconds",
            glm.FiveHour!.ResetsAt == JavaScriptSerializerDateConverter.FromUnixMilliseconds(1790163759000));

        var noPlan = MonitorDataService.ParseGlmQuota(MonitorFixtures.ReadText("glm-quota-no-plan.json"));
        check("an ordinary key has no plan windows",
            noPlan.QuotaRead && noPlan.FiveHour is null && noPlan.Weekly is null && noPlan.Monthly is null
            && noPlan.Level is null && noPlan.Breakdown == string.Empty);

        // GLM authentication detection, against the four bodies measured live.
        check("the quota auth body is recognised",
            MonitorDataService.IsAuthenticationFailure(new MonitorDataService.HttpResult
            { StatusCode = 200, Body = MonitorFixtures.ReadText("glm-quota-auth-failed.json") }));
        check("the report auth body is recognised",
            MonitorDataService.IsAuthenticationFailure(new MonitorDataService.HttpResult
            { StatusCode = 200, Body = MonitorFixtures.ReadText("glm-report-auth-failed.json") }));
        check("the v4 balance 401 body is recognised through error.message",
            MonitorDataService.IsAuthenticationFailure(new MonitorDataService.HttpResult
            { StatusCode = 401, Body = MonitorFixtures.ReadText("glm-balance-auth-failed.json") }));
        check("a success body is not an auth failure",
            !MonitorDataService.IsAuthenticationFailure(new MonitorDataService.HttpResult
            { StatusCode = 200, Body = MonitorFixtures.ReadText("glm-quota-limits.json") }));
        check("an HTTP status alone is not an auth failure for 200",
            !MonitorDataService.IsAuthenticationFailure(new MonitorDataService.HttpResult { StatusCode = 200, Body = "{}" }));

        check("glm refuses a 401 code in the quota body",
            Capture(() => MonitorDataService.ParseGlmQuota("{\"code\":401,\"msg\":\"bad\"}")) is InvalidOperationException
            { Message: "API key authentication failed" });
        check("glm surfaces another API error message",
            Capture(() => MonitorDataService.ParseGlmQuota("{\"code\":1002,\"msg\":\"quota unavailable\"}"))
                is InvalidOperationException { Message: "quota unavailable" });

        // The wallet parsers.
        var report = MonitorDataService.ParseReportWallet(new MonitorDataService.HttpResult
        { StatusCode = 200, Body = MonitorFixtures.ReadText("glm-report-wallet.json") });
        check("the recorded account report carries the spend and the balance",
            report.Balance == 3.584534445 && report.TotalSpend == 16.415465555 && report.WalletTotal is null
            && report.CurrencySymbol == "¥");
        var v4 = MonitorDataService.ParseV4Wallet(new MonitorDataService.HttpResult
        { StatusCode = 200, Body = MonitorFixtures.ReadText("glm-balance-v4.json") });
        check("the v4 balance carries the wallet total", v4.Balance == 52.35 && v4.WalletTotal == 60);
        var usd = MonitorDataService.ParseReportWallet(new MonitorDataService.HttpResult
        { StatusCode = 200, Body = MonitorFixtures.ReadText("glm-wallet-usd.json") });
        check("a non CNY wallet keeps its symbol", usd.CurrencySymbol == "$");
        check("glm currency symbols match Windows",
            MonitorDataService.CurrencySymbol(null) == "¥" && MonitorDataService.CurrencySymbol("CNY") == "¥"
            && MonitorDataService.CurrencySymbol("usd") == "$" && MonitorDataService.CurrencySymbol("HKD") == "HKD ");
        check("glm formats large token counts like Windows",
            MonitorDataService.FormatNumber(999) == "999" && MonitorDataService.FormatNumber(1234567) == "1.23M"
            && MonitorDataService.FormatNumber(5000000) == "5M" && MonitorDataService.FormatNumber(2500000000) == "2.5B");
        var noCodingPlan = Capture(() => MonitorDataService.ParseGlmQuota(
            MonitorFixtures.ReadText("glm-quota-no-coding-plan.json")));
        check("the real no-coding-plan answer surfaces the API message",
            noCodingPlan is InvalidOperationException { Message: "当前用户不存在coding plan" });
        check("the real v4 404 is an HTTP failure, not an auth failure",
            !MonitorDataService.IsAuthenticationFailure(new MonitorDataService.HttpResult
            { StatusCode = 404, Body = MonitorFixtures.ReadText("glm-balance-v4-404.json") })
            && Capture(() => MonitorDataService.ParseV4Wallet(new MonitorDataService.HttpResult
            { StatusCode = 404, Body = MonitorFixtures.ReadText("glm-balance-v4-404.json") }))
                is InvalidOperationException { Message: "GLM HTTP 404" });

        check("glm falls back to a percentage breakdown without counters",
            MonitorDataService.FormatQuota("5h", System.Text.Json.JsonDocument.Parse("{\"percentage\":37.5}").RootElement)
                == "5h 已用 37.5%");

        check("a missing key is reported as unconfigured",
            MonitorLayout.FriendlyError(new InvalidOperationException("API key or environment variable is not configured"),
                "DeepSeek 暂不可用") == "未配置 API Key");
        check("an HTTP failure keeps the Windows fallback",
            MonitorLayout.FriendlyError(new InvalidOperationException("DeepSeek HTTP 401"), "DeepSeek 暂不可用")
                == "DeepSeek 暂不可用");
        check("a timeout is reported as a timeout",
            MonitorLayout.FriendlyError(new TimeoutException("Codex app-server timeout"), "fallback") == "连接超时");
        check("an auth failure is reported as an invalid key",
            MonitorLayout.FriendlyError(new InvalidOperationException("API key authentication failed"), "fallback")
                == "API Key 无效");
    }

    // --------------------------------------------------------- text + layout

    private static void CheckTextAndLayout(Action<string, bool> check)
    {
        var state = new UsageSnapshot
        {
            FiveHourRemaining = 62.5,
            WeeklyRemaining = 38,
            CodexState = "正常",
            DeepSeekBalance = "¥128.4 / $3.5",
            DeepSeekState = "正常",
            DeepSeekCurrencySymbol = "¥",
            TodayDeepSeekSpend = 10.25,
            TodayGlmSpend = 10.25,
            GlmBalance = 52.35,
            GlmCurrencySymbol = "¥",
            GlmTotalSpend = 147.65,
            PlanType = "plus",
            FiveHourReset = new DateTime(2026, 9, 21, 18, 30, 0),
            GlmPlanType = "pro",
            GlmQuotaBreakdown = "5h 1.23M/5M",
            GlmFiveHourRemaining = 62.5,
            GlmWeeklyRemaining = 38,
            GlmMonthlyRemaining = 82,
            GlmState = "正常",
            CodexUpdatedAt = new DateTime(2026, 9, 21, 13, 0, 0),
            GlmFiveHourReset = new DateTime(2026, 9, 21, 18, 30, 0),
            GlmUpdatedAt = new DateTime(2026, 9, 21, 13, 0, 0),
            DeepSeekTrackingStart = new DateTime(2026, 9, 21, 9, 0, 0),
            UpdatedAt = new DateTime(2026, 9, 21, 13, 0, 0)
        };
        var codex = MonitorText.CodexCompact(state);
        check("the codex row shows both windows rounded",
            codex.Text == "5h 62%  ·  周 38%" && codex.Tone == MonitorTone.Primary);
        var deep = MonitorText.DeepSeekCompact(state);
        check("the deepseek row appends today's estimate",
            deep.Text == "¥128.4 / $3.5 · 今约¥10.25" && deep.Tone == MonitorTone.Primary);
        var glm = MonitorText.GlmCompact(state);
        check("the glm row shows the balance, the estimate and the windows",
            glm.Text == "¥52.35 · 今约10.25 · 5h 62% · 周 38%" && glm.Tone == MonitorTone.Primary);

        var cached = new UsageSnapshot { CodexState = "Codex 未运行 · 显示上次数据", FiveHourRemaining = 8 };
        var cachedLine = MonitorText.CodexCompact(cached);
        check("a cached codex row is marked and turns red",
            cachedLine.Text == "5h 8%  ·  缓存" && cachedLine.Tone == MonitorTone.Danger);
        var warning = MonitorText.CodexCompact(new UsageSnapshot { WeeklyRemaining = 20 });
        check("25 percent or less is the warning colour", warning.Tone == MonitorTone.Warning);

        var disabled = new UsageSnapshot { CodexState = "未启用", DeepSeekState = "未启用", GlmState = "未启用" };
        check("disabled providers render their state muted",
            MonitorText.CodexCompact(disabled).Tone == MonitorTone.Muted
            && MonitorText.DeepSeekCompact(disabled) is { Text: "未启用", Tone: MonitorTone.Muted }
            && MonitorText.GlmCompact(disabled) is { Text: "未启用", Tone: MonitorTone.Muted });
        check("an empty snapshot falls back to the Windows placeholders",
            MonitorText.CodexCompact(new UsageSnapshot()).Text == "不可用"
            && MonitorText.DeepSeekCompact(new UsageSnapshot()).Text == "不可用"
            && MonitorText.GlmCompact(new UsageSnapshot()).Text == "未启用");

        var drained = new UsageSnapshot { DeepSeekBalance = "¥0", DeepSeekState = "余额不足" };
        check("a drained deepseek balance turns amber",
            MonitorText.DeepSeekCompact(drained).Tone == MonitorTone.Warning);
        var smallBalance = new UsageSnapshot { GlmBalance = 3.5, GlmCurrencySymbol = "¥" };
        check("a small glm balance turns amber",
            MonitorText.GlmCompact(smallBalance).Tone == MonitorTone.Warning
            && MonitorText.GlmCompact(smallBalance).Text == "¥3.5");
        var negative = new UsageSnapshot { GlmBalance = 0 };
        check("a zero glm balance turns red", MonitorText.GlmCompact(negative).Tone == MonitorTone.Danger);
        var monthlyOnly = new UsageSnapshot { GlmMonthlyRemaining = 82 };
        check("a monthly only plan shows the MCP label", MonitorText.GlmCompact(monthlyOnly).Text == "MCP 82%");

        check("the footnote reports progress, the update time and the initial state",
            MonitorText.CompactFooter(state, true) == "正在刷新…"
            && MonitorText.CompactFooter(state, false) == "更新 13:00"
            && MonitorText.CompactFooter(new UsageSnapshot(), false) == "等待更新");

        check("the detail codex value and meta match Windows",
            MonitorText.CodexDetailValue(state) == "5h 62%   周 38%"
            && MonitorText.CodexDetailMeta(state) == "计划 plus  ·  5h 重置 18:30  ·  额度更新 9/21 13:00");
        check("the detail deepseek lines match Windows",
            MonitorText.DeepSeekDetailValue(state) == "¥128.4 / $3.5"
            && MonitorText.DeepSeekDetailMeta(state) == "读取 DEEPSEEK_API_KEY");
        check("the detail glm value and meta match Windows",
            MonitorText.GlmDetailValue(state) == "余额 ¥52.35   5h 62%   周 38%   MCP 月 82%"
            && MonitorText.GlmDetailMeta(state)
                == "累计消费 ¥147.65  ·  套餐 pro  ·  5h 1.23M/5M  ·  5h 重置 18:30  ·  额度更新 9/21 13:00");
        check("today's estimate says 今日约 once the whole day is covered",
            MonitorText.TodaySpend(10.25, new DateTime(2026, 9, 21, 0, 0, 0), "¥", new DateTime(2026, 9, 21, 13, 0, 0))
                == "今日约 ¥10.25");
        check("a partial day is labelled 监控后",
            MonitorText.TodaySpend(10.25, new DateTime(2026, 9, 21, 11, 30, 0), "¥", new DateTime(2026, 9, 21, 13, 0, 0))
                == "监控后 ¥10.25");
        check("no estimate yet shows the placeholder",
            MonitorText.TodaySpend(null, null, "¥", new DateTime(2026, 9, 21, 13, 0, 0)) == "今日 --");

        check("the trend title names the provider and the range",
            MonitorText.TrendTitle(false, false, MonitorTrendRange.Day) == "DeepSeek · 今日消费趋势（估算）"
            && MonitorText.TrendTitle(true, false, MonitorTrendRange.Week) == "GLM · 本周消费趋势（估算）"
            && MonitorText.TrendTitle(false, true, MonitorTrendRange.Day) == "DeepSeek · 监控后消费趋势（估算）"
            && MonitorText.TrendTitle(true, true, MonitorTrendRange.Month) == "GLM · 本月监控后消费趋势（估算）");
        check("the axis labels carry the currency and the range format",
            MonitorText.ChartMaximum("¥", 12.5) == "¥12.5" && MonitorText.ChartMinimum("¥") == "¥0"
            && MonitorText.ChartStart(true, new DateTime(2026, 9, 21, 9, 0, 0)) == "09:00"
            && MonitorText.ChartEnd(false, new DateTime(2026, 9, 21, 13, 0, 0)) == "9/21");
        check("the detail footer shows the full update time",
            MonitorText.DetailsUpdated(state) == "更新 13:00:00"
            && MonitorText.DetailsUpdated(new UsageSnapshot()) == "尚未更新");

        check("one provider gets the tall compact row",
            MonitorLayout.CompactRowHeight(1) == 48 && MonitorLayout.CompactFooterHeight(1) == 24);
        check("two providers get the middle row",
            MonitorLayout.CompactRowHeight(2) == 27 && MonitorLayout.CompactFooterHeight(2) == 18);
        check("three providers keep the Windows rows",
            MonitorLayout.CompactRowHeight(3) == 20 && MonitorLayout.CompactFooterHeight(3) == 12);
        check("the detail window shrinks by the Windows amounts, with a 205 floor",
            MonitorLayout.DetailsWindowHeight(true, true, true) == 505
            && MonitorLayout.DetailsWindowHeight(true, true, false) == 435
            && MonitorLayout.DetailsWindowHeight(true, false, false) == 215
            && MonitorLayout.DetailsWindowHeight(false, false, false) == 205);
        check("detail sections collapse together",
            MonitorLayout.DetailsSections(true, false, true).SequenceEqual([true, false, true, true])
            && MonitorLayout.DetailsSections(false, false, false).SequenceEqual([false, false, false, false]));
        check("remaining is clamped to 0..100",
            MonitorLayout.Remaining(-5) == 100 && MonitorLayout.Remaining(140) == 0 && MonitorLayout.Remaining(62.5) == 37.5);
        check("minimum uses 100 when nothing is known",
            MonitorLayout.Minimum(null, null) == 100 && MonitorLayout.Minimum(null, 20) == 20
            && MonitorLayout.Minimum(20, null) == 20 && MonitorLayout.Minimum(20, 30) == 20);
        check("the compact number format never adds separators",
            MonitorLayout.CompactNumber(1234.5) == "1234.5" && MonitorLayout.CompactNumber(0) == "0");
        check("percent rounds like Windows", MonitorLayout.Percent(37.5) == "38%" && MonitorLayout.Percent(62.5) == "62%");
        check("the window sizes are the Windows ones",
            MonitorLayout.CompactWidth == 316 && MonitorLayout.CompactHeight == 92
            && MonitorLayout.DetailsWidth == 380 && MonitorLayout.DetailsHeight == 505
            && MonitorLayout.SettingsWidth == 430 && MonitorLayout.SettingsHeight == 470);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Runs an action and returns the exception it threw, or null.</summary>
    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Runs a test body against a throwaway root with the import override cleared
    /// and the XDG data home redirected there, so neither a leftover environment
    /// variable nor a real Windows folder on this machine can decide the result.
    /// </summary>
    private static void WithTemporaryRoot(Action<string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "LittleTools-monitor-test-" + Guid.NewGuid().ToString("N"));
        var previousData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var previousOverride = Environment.GetEnvironmentVariable(MonitorStore.DirectoryVariable);
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "xdg"));
            Environment.SetEnvironmentVariable(MonitorStore.DirectoryVariable, null);
            body(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previousData);
            Environment.SetEnvironmentVariable(MonitorStore.DirectoryVariable, previousOverride);
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch
            {
                // A leftover temp folder is harmless.
            }
        }
    }
}
