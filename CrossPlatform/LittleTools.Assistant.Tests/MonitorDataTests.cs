using LittleTools.Assistant;
using LittleTools.Assistant.Monitor;
using LittleTools.Assistant.Services;

/// <summary>
/// Tests for the monitor's network paths. Every body comes from the embedded
/// fixtures (Monitor/Fixtures, see the README there for which are real recorded
/// responses and which are hand built), replayed through the normal
/// <see cref="MonitorDataService"/>, so the parse, degrade and error branches run
/// exactly as they do against the live hosts.
///
/// <c>--monitor-live</c> adds a real pass: the Codex app-server is queried for
/// real, and the two HTTP hosts are asked with a deliberately invalid key so
/// reachability and the authentication mapping are measured instead of assumed.
/// A blocked host surfaces as a failure there rather than being papered over.
/// </summary>
internal static class MonitorDataTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        CheckFixtureSet(check);
        await CheckDeepSeekAsync(check);
        await CheckGlmAsync(check);
        await CheckFailuresAsync(check);
        await CheckCodexAsync(check);
    }

    private static void CheckFixtureSet(Action<string, bool> check)
    {
        foreach (var required in new[]
                 {
                     "codex-rate-limits.json", "codex-auth-required.json", "deepseek-balance.json",
                     "deepseek-balance-multi.json", "deepseek-empty-balance.json", "deepseek-auth-failed.txt",
                     "glm-quota-limits.json", "glm-quota-no-plan.json", "glm-quota-no-coding-plan.json",
                     "glm-report-wallet.json", "glm-balance-v4.json", "glm-balance-v4-404.json",
                     "glm-wallet-usd.json", "glm-quota-auth-failed.json", "glm-report-auth-failed.json",
                     "glm-balance-auth-failed.json"
                 })
            check("fixture present: " + required, MonitorFixtures.Exists(required));
        check("the monitor fixture routes are matched",
            MonitorFixtures.FileFor("https://api.deepseek.com/user/balance") == "deepseek-balance.json"
            && MonitorFixtures.FileFor("https://open.bigmodel.cn/api/monitor/usage/quota/limit") == "glm-quota-limits.json"
            && MonitorFixtures.FileFor("https://open.bigmodel.cn/api/biz/account/query-customer-account-report") == "glm-report-wallet.json"
            && MonitorFixtures.FileFor("https://open.bigmodel.cn/api/paas/v4/balance") == "glm-balance-v4.json");
        check("an unknown monitor url is not routed", MonitorFixtures.FileFor("https://example.invalid/x") is null);
        check("the recorded deepseek 401 body is the live text",
            MonitorFixtures.ReadText("deepseek-auth-failed.txt").Trim() == "Authentication Fails (governor)");
        check("the recorded glm auth body is the live text",
            MonitorFixtures.ReadText("glm-quota-auth-failed.json").Contains("身份验证", StringComparison.Ordinal));
    }

    private static async Task CheckDeepSeekAsync(Action<string, bool> check)
    {
        using var service = MonitorFixtures.CreateService();
        var usage = await service.ReadDeepSeekAsync("test-key", CancellationToken.None);
        check("deepseek reads the recorded balance through the service",
            usage.Available && usage.PrimaryBalance == 16.98 && usage.CurrencySymbol == "¥"
            && usage.TotalDisplay == "¥16.98");

        using var drained = MonitorFixtures.CreateService(MonitorFixtureScenario.DeepSeekDrained);
        var low = await drained.ReadDeepSeekAsync("test-key", CancellationToken.None);
        check("a drained deepseek account is not available", !low.Available && low.PrimaryBalance == 0);
    }

    private static async Task CheckGlmAsync(Action<string, bool> check)
    {
        // Both halves answer: the full Coding Plan state.
        using (var service = MonitorFixtures.CreateService())
        {
            var usage = await service.ReadGlmAsync("test-key", CancellationToken.None);
            check("glm reads the quota and the wallet together",
                usage.QuotaRead && usage.WalletRead && usage.Balance == 3.584534445
                && usage.TotalSpend == 16.415465555
                && usage.WalletTotal is null && usage.CurrencySymbol == "¥" && usage.Level == "pro");
            check("glm keeps the quota windows", usage.FiveHour is not null && usage.Weekly is not null && usage.Monthly is not null);
            check("glm did not degrade when the report answered", service.WalletFallbackCount == 0);
        }

        // The report host refuses: the wallet must come from the older endpoint.
        using (var service = MonitorFixtures.CreateService(MonitorFixtureScenario.WalletEndpointOnly))
        {
            var usage = await service.ReadGlmAsync("test-key", CancellationToken.None);
            check("a refused report degrades to the v4 balance",
                usage.WalletRead && usage.Balance == 52.35 && usage.WalletTotal == 60);
            check("the degradation is counted with a reason",
                service.WalletFallbackCount == 1 && service.LastWalletFallbackReason is not null
                && service.LastWalletFallbackReason.Contains("GLM HTTP 500", StringComparison.Ordinal));
            check("the quota still answers after the wallet degraded",
                usage.QuotaRead && usage.FiveHour is not null);
        }

        // An ordinary pay-as-you-go key that answers successfully with no windows.
        using (var service = MonitorFixtures.CreateService(MonitorFixtureScenario.NoCodingPlan))
        {
            var usage = await service.ReadGlmAsync("test-key", CancellationToken.None);
            check("an ordinary key has no plan quota",
                usage.QuotaRead && usage.WalletRead && usage.FiveHour is null && usage.Weekly is null
                && usage.Monthly is null);
        }

        // The real pay-as-you-go answer: the quota endpoint reports an API error
        // ("当前用户不存在coding plan"), the report succeeds, the v4 fallback is gone.
        using (var service = MonitorFixtures.CreateService(MonitorFixtureScenario.NoCodingPlanApiError))
        {
            var usage = await service.ReadGlmAsync("test-key", CancellationToken.None);
            check("the real no-coding-plan answer leaves the quota unread but keeps the wallet",
                !usage.QuotaRead && usage.WalletRead && usage.Balance == 3.584534445
                && usage.TotalSpend == 16.415465555);
        }

        // Only the quota refuses.
        using (var service = MonitorFixtures.CreateService(MonitorFixtureScenario.QuotaRefused))
        {
            var usage = await service.ReadGlmAsync("test-key", CancellationToken.None);
            check("a refused quota keeps the wallet",
                !usage.QuotaRead && usage.WalletRead && usage.Balance == 3.584534445);
        }

        // The report refuses and the real v4 fallback answers 404: no wallet at all.
        using (var service = MonitorFixtures.CreateService(MonitorFixtureScenario.WalletUnavailable))
        {
            var usage = await service.ReadGlmAsync("test-key", CancellationToken.None);
            check("a refused wallet keeps the quota",
                usage.QuotaRead && !usage.WalletRead && usage.FiveHour is not null);
            // The report's refusal is what starts the fallback; the real v4 404 is
            // then pinned by the parser test in MonitorCoreTests.
            check("the refused report is recorded as the fallback reason",
                service.WalletFallbackCount == 1 && service.LastWalletFallbackReason is not null
                && service.LastWalletFallbackReason.Contains("GLM HTTP 500", StringComparison.Ordinal));
        }
    }

    private static async Task CheckFailuresAsync(Action<string, bool> check)
    {
        // A missing key never reaches the network.
        using (var service = MonitorFixtures.CreateService())
        {
            var deep = await CaptureAsync(() => service.ReadDeepSeekAsync(null, CancellationToken.None));
            var glm = await CaptureAsync(() => service.ReadGlmAsync("   ", CancellationToken.None));
            check("deepseek refuses to run without a key",
                deep is InvalidOperationException { Message: "API key or environment variable is not configured" });
            check("glm refuses to run without a key",
                glm is InvalidOperationException { Message: "API key or environment variable is not configured" });
            check("the missing key text maps to 未配置 API Key",
                MonitorLayout.FriendlyError(deep!, "DeepSeek 暂不可用") == "未配置 API Key"
                && MonitorLayout.FriendlyError(glm!, "GLM 暂不可用") == "未配置 API Key");
        }

        // Everything refuses with an authentication failure: the key is wrong.
        using (var service = MonitorFixtures.CreateService(MonitorFixtureScenario.AuthFailure))
        {
            var error = await CaptureAsync(() => service.ReadGlmAsync("bad-key", CancellationToken.None));
            check("a rejected key is reported as an authentication failure",
                error is InvalidOperationException { Message: "API key authentication failed" });
            check("the rejected key text maps to API Key 无效",
                MonitorLayout.FriendlyError(error!, "GLM 暂不可用") == "API Key 无效");
        }

        // Quota refuses with a 500 and the wallet is unreadable as well: no auth error,
        // so the Windows generic message is used.
        using (var service = MonitorFixtures.CreateService(MonitorFixtureScenario.BothRefused))
        {
            var error = await CaptureAsync(() => service.ReadGlmAsync("bad-key", CancellationToken.None));
            check("a partial outage without auth trouble uses the generic message",
                error is InvalidOperationException { Message: "GLM quota and balance unavailable" });
            check("a rejected balance endpoint still counts as auth",
                MonitorDataService.IsAuthenticationFailure(new MonitorDataService.HttpResult
                { StatusCode = 401, Body = MonitorFixtures.ReadText("glm-balance-auth-failed.json") }));
        }

        // The recorded DeepSeek 401: the host is reachable, the key is not accepted.
        var handler = new MonitorFixtureHandler(MonitorFixtureScenario.Success) { RefuseDeepSeek = true };
        using (var service = new MonitorDataService(handler, FixtureCodexAppServer.LoggedIn()))
        {
            var error = await CaptureAsync(() => service.ReadDeepSeekAsync("bad-key", CancellationToken.None));
            check("an HTTP status is surfaced as the Windows message",
                error is InvalidOperationException { Message: "DeepSeek HTTP 401" });
            check("the HTTP message keeps the Windows fallback label",
                MonitorLayout.FriendlyError(error!, "DeepSeek 暂不可用") == "DeepSeek 暂不可用");
        }
    }

    private static async Task CheckCodexAsync(Action<string, bool> check)
    {
        var loggedIn = FixtureCodexAppServer.LoggedIn();
        var usage = await loggedIn.ReadAsync(CancellationToken.None);
        check("the recorded codex reply parses through the fixture server",
            usage.Primary is { UsedPercent: 0, WindowDurationMins: 300 }
            && usage.Secondary is { UsedPercent: 49, WindowDurationMins: 10080 }
            && usage.PlanType == "plus" && usage.Credits == "0");

        var loggedOut = FixtureCodexAppServer.LoggedOut();
        var error = await CaptureAsync(() => loggedOut.ReadAsync(CancellationToken.None));
        check("the recorded logged out reply is an authentication error",
            error is InvalidOperationException
            && error.Message.Contains("authentication required", StringComparison.Ordinal));
        check("the codex error maps to the login hint",
            MonitorLayout.FriendlyError(error!, "Codex 暂不可用 · 显示上次数据") == "需要 Codex 登录");
    }

    /// <summary>
    /// The opt-in live pass (<c>--monitor-live</c>). It never falls back to the
    /// fixtures: a host that cannot be reached fails the run, and the measured
    /// status is written next to the test output so a blocked network is visible.
    /// </summary>
    public static async Task RunLiveAsync(Action<string, bool> check)
    {
        Console.WriteLine("LIVE monitor: the *-live pass. A configured key reads real data; without one the probe"
            + " uses an invalid key so host reachability and the auth mapping are still measured.");
        var store = new SettingsStore();
        using var service = new MonitorDataService();

        // Codex: the real app-server, when one is available.
        var availability = service.Codex.Availability;
        Console.WriteLine($"LIVE codex: availability={availability} ({service.Codex.Describe()})");
        if (availability != CodexAvailability.None)
        {
            var codexError = await CaptureAsync(() => service.Codex.ReadAsync(CancellationToken.None));
            if (codexError is null)
            {
                var usage = await service.Codex.ReadAsync(CancellationToken.None);
                Console.WriteLine("LIVE codex: primary=" + Describe(usage.Primary) + " secondary=" + Describe(usage.Secondary)
                    + " plan=" + (usage.PlanType ?? "none") + " credits=" + (usage.Credits ?? "none"));
                check("live codex returns a rate window", usage.Primary is not null || usage.Secondary is not null);
            }
            else
            {
                // The app-server needs a writable CODEX_HOME. A sandbox that denies
                // writes under ~/.codex makes it time out; that is reported as a
                // failure instead of being hidden, with the hint that identifies it.
                Console.WriteLine("LIVE codex: " + codexError.GetType().Name + ": " + codexError.Message
                    + " (CODEX_HOME=" + (Environment.GetEnvironmentVariable("CODEX_HOME") ?? "~/.codex")
                    + "; a read-only CODEX_HOME makes the app-server time out)");
                check("live codex reads the rate limits", false);
            }
        }
        else
        {
            Console.WriteLine("LIVE codex: skipped, no codex app-server on this machine.");
        }

        // DeepSeek: an invalid key must reach the host and come back as a 401.
        var deepKey = store.ResolveKey(ProviderKind.DeepSeek);
        var deepError = await CaptureAsync(() => service.ReadDeepSeekAsync(
            string.IsNullOrWhiteSpace(deepKey) ? "sk-invalid-monitor-probe" : deepKey, CancellationToken.None));
        if (deepError is null)
        {
            var liveDeep = await service.ReadDeepSeekAsync(deepKey!, CancellationToken.None);
            Console.WriteLine("LIVE deepseek: balance=" + (liveDeep.TotalDisplay ?? "none")
                + " breakdown=" + (liveDeep.Breakdown ?? "none")
                + " available=" + liveDeep.Available + " sample=" + liveDeep.PrimaryBalance);
            check("live deepseek reads a balance", !string.IsNullOrEmpty(liveDeep.TotalDisplay));
        }
        else
        {
            Console.WriteLine("LIVE deepseek: " + deepError.GetType().Name + ": " + deepError.Message
                + " -> " + MonitorLayout.FriendlyError(deepError, "DeepSeek 暂不可用"));
            check("live deepseek reached the host",
                deepError is InvalidOperationException { Message: "DeepSeek HTTP 401" }
                || deepError.Message.Contains("HTTP", StringComparison.Ordinal));
        }

        // GLM: same idea, with the Chinese authentication body the host really sends.
        var glmKey = store.ResolveKey(ProviderKind.Glm);
        var glmError = await CaptureAsync(() => service.ReadGlmAsync(
            string.IsNullOrWhiteSpace(glmKey) ? "invalid-monitor-probe" : glmKey, CancellationToken.None));
        if (glmError is null)
        {
            var liveGlm = await service.ReadGlmAsync(glmKey!, CancellationToken.None);
            Console.WriteLine("LIVE glm: quotaRead=" + liveGlm.QuotaRead + " walletRead=" + liveGlm.WalletRead
                + " balance=" + liveGlm.Balance + " totalSpend=" + liveGlm.TotalSpend
                + " level=" + (liveGlm.Level ?? "none") + " breakdown=" + (liveGlm.Breakdown ?? "none")
                + " walletFallbacks=" + service.WalletFallbackCount
                + " last=" + (service.LastWalletFallbackReason ?? "none"));
            check("live glm reads the wallet or the quota",
                liveGlm.WalletRead || liveGlm.QuotaRead);
        }
        else
        {
            Console.WriteLine("LIVE glm: " + glmError.GetType().Name + ": " + glmError.Message
                + " -> " + MonitorLayout.FriendlyError(glmError, "GLM 暂不可用")
                + " walletFallbacks=" + service.WalletFallbackCount
                + " last=" + (service.LastWalletFallbackReason ?? "none"));
            check("live glm reached the hosts",
                glmError is InvalidOperationException { Message: "API key authentication failed" }
                || glmError.Message.Contains("GLM HTTP", StringComparison.Ordinal));
        }
    }

    private static string Describe(RateWindow? window) =>
        window is null ? "none" : window.UsedPercent + "%/" + window.WindowDurationMins + "min";

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
