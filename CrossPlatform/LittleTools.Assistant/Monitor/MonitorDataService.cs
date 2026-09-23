using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// Reads the DeepSeek balance and the GLM wallet/quota, ported from
/// <c>DeepSeekProvider</c> (AIUsageMonitor/Program.cs L767-L840) and
/// <c>GlmProvider</c> (L865-L1148). Every URL, header variant, status rule and
/// field name is the Windows one:
///   * DeepSeek <c>GET https://api.deepseek.com/user/balance</c> with a Bearer key;
///   * GLM quota <c>GET .../api/monitor/usage/quota/limit</c>, tried with the bare
///     key first and retried with <c>Bearer</c> on an authentication failure (L909-L920);
///   * GLM wallet <c>.../api/biz/account/query-customer-account-report</c>, falling
///     back to <c>.../api/paas/v4/balance</c> when the report host refuses (L922-L934).
///
/// The HTTP handler and the Codex app-server are injectable, so the recorded
/// responses replay the real parsing path offline (see <see cref="MonitorFixtures"/>).
///
/// Network reality measured on this machine before the port was finished (see the
/// porting notes): both hosts are reachable through the session's HTTP proxy
/// (<c>api.deepseek.com</c> answers 401 without a key, <c>open.bigmodel.cn</c>
/// answers 200 with the Chinese "no Authorization header" body). Unlike the stock
/// module there is therefore no second source to degrade to; the only deliberate
/// change is that the GLM wallet degradation is counted so a run can prove it
/// happened instead of assuming it.
/// </summary>
internal sealed class MonitorDataService : IDisposable
{
    private const string QuotaEndpoint = "https://open.bigmodel.cn/api/monitor/usage/quota/limit";
    private const string ReportEndpoint = "https://open.bigmodel.cn/api/biz/account/query-customer-account-report";
    private const string BalanceEndpoint = "https://open.bigmodel.cn/api/paas/v4/balance";
    private const string DeepSeekEndpoint = "https://api.deepseek.com/user/balance";

    /// <summary>Windows HttpClient timeout, kept (Program.cs L769, L870).</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private readonly HttpClient _client;
    private int _walletFallbackCount;
    private string? _lastWalletFallbackReason;

    public MonitorDataService(HttpMessageHandler? handler = null, ICodexAppServer? codex = null)
    {
        Codex = codex ?? new SystemCodexAppServer();
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = RequestTimeout;
    }

    /// <summary>The Codex seam, used by the controller for its availability state.</summary>
    public ICodexAppServer Codex { get; }

    /// <summary>
    /// How often the GLM wallet had to fall back from the account report to the
    /// older balance endpoint. Exposed so a live run shows the degradation instead
    /// of silently reporting a wallet that came from somewhere else.
    /// </summary>
    public int WalletFallbackCount => Volatile.Read(ref _walletFallbackCount);

    /// <summary>Why the wallet fell back, for diagnostics.</summary>
    public string? LastWalletFallbackReason => Volatile.Read(ref _lastWalletFallbackReason);

    // -------------------------------------------------------------- DeepSeek

    /// <summary>Windows DeepSeekProvider.ReadAsync (Program.cs L771-L789).</summary>
    public async Task<DeepSeekUsage> ReadDeepSeekAsync(string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API key or environment variable is not configured");

        using var request = new HttpRequestMessage(HttpMethod.Get, DeepSeekEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("DeepSeek HTTP " + (int)response.StatusCode);
        return ParseDeepSeek(body);
    }

    /// <summary>Windows DeepSeekProvider.Parse (Program.cs L791-L830).</summary>
    internal static DeepSeekUsage ParseDeepSeek(string body)
    {
        var root = MonitorPayload.ParseObject(body)
            ?? throw new InvalidOperationException("DeepSeek returned invalid data");
        var usage = new DeepSeekUsage
        {
            Available = MonitorPayload.Bool(root, "is_available", false)
        };
        if (MonitorPayload.Array(root, "balance_infos") is not { } infos || infos.Count == 0)
            throw new InvalidOperationException("DeepSeek returned no balance");

        var totals = new List<string>();
        var details = new List<string>();
        foreach (var info in infos)
        {
            var currency = MonitorPayload.String(info, "currency") ?? string.Empty;
            var symbol = CurrencySymbol(currency);
            var total = MonitorPayload.String(info, "total_balance") ?? "0";
            var topped = MonitorPayload.String(info, "topped_up_balance") ?? "0";
            var granted = MonitorPayload.String(info, "granted_balance") ?? "0";
            totals.Add(symbol + TrimMoney(total));
            details.Add(string.Format(CultureInfo.InvariantCulture, "{0}充值 {1} · 赠送 {2}",
                symbol, TrimMoney(topped), TrimMoney(granted)));
            if (!usage.PrimaryBalance.HasValue
                && double.TryParse(total, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                usage.PrimaryBalance = parsed;
                usage.CurrencySymbol = symbol;
            }
        }
        usage.TotalDisplay = string.Join(" / ", totals);
        usage.Breakdown = string.Join("   ", details);
        return usage;
    }

    /// <summary>Windows DeepSeekProvider.TrimMoney (Program.cs L832-L839).</summary>
    internal static string TrimMoney(string value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("0.##", CultureInfo.InvariantCulture)
            : value;

    // ------------------------------------------------------------------- GLM

    /// <summary>Windows GlmProvider.ReadAsync (Program.cs L872-L907).</summary>
    public async Task<GlmUsage> ReadGlmAsync(string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API key or environment variable is not configured");

        var key = apiKey.Trim();
        var quotaTask = ReadQuotaAsync(key, cancellationToken);
        var walletTask = ReadWalletAsync(key, cancellationToken);
        GlmUsage? usage = null;
        GlmWallet? wallet = null;
        Exception? quotaError = null;
        Exception? walletError = null;
        try
        {
            usage = await quotaTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            quotaError = exception;
        }
        try
        {
            wallet = await walletTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            walletError = exception;
        }

        usage ??= new GlmUsage();
        if (wallet is not null)
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

    /// <summary>Windows GlmProvider.ReadQuotaAsync (Program.cs L909-L920).</summary>
    private async Task<GlmUsage> ReadQuotaAsync(string key, CancellationToken cancellationToken)
    {
        var result = await SendAsync(QuotaEndpoint, key, bearer: false, cancellationToken).ConfigureAwait(false);
        if (IsAuthenticationFailure(result))
            result = await SendAsync(QuotaEndpoint, key, bearer: true, cancellationToken).ConfigureAwait(false);

        if (IsAuthenticationFailure(result))
            throw new InvalidOperationException("API key authentication failed");
        if (result.StatusCode < 200 || result.StatusCode >= 300)
            throw new InvalidOperationException("GLM HTTP " + result.StatusCode);
        return ParseGlmQuota(result.Body);
    }

    /// <summary>Windows GlmProvider.ReadWalletAsync (Program.cs L922-L934).</summary>
    private async Task<GlmWallet> ReadWalletAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var report = await SendAsync(ReportEndpoint, key, bearer: false, cancellationToken).ConfigureAwait(false);
            return ParseReportWallet(report);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _walletFallbackCount);
            Volatile.Write(ref _lastWalletFallbackReason, exception.GetType().Name + ": " + exception.Message);
        }

        var balance = await SendAsync(BalanceEndpoint, key, bearer: true, cancellationToken).ConfigureAwait(false);
        return ParseV4Wallet(balance);
    }

    /// <summary>Windows GlmProvider.SendAsync (Program.cs L936-L951).</summary>
    private async Task<HttpResult> SendAsync(string endpoint, string key, bool bearer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("Authorization", bearer ? "Bearer " + key : key);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return new HttpResult
        {
            StatusCode = (int)response.StatusCode,
            Body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    /// <summary>Windows GlmProvider.ParseReportWallet (Program.cs L953-L966).</summary>
    internal static GlmWallet ParseReportWallet(HttpResult result)
    {
        var root = RequireSuccess(result);
        var data = MonitorPayload.Object(root, "data")
            ?? throw new InvalidOperationException("GLM balance report returned no data");
        var available = MonitorPayload.OptionalDouble(data, "availableBalance")
            ?? MonitorPayload.OptionalDouble(data, "balance")
            ?? throw new InvalidOperationException("GLM balance report returned no balance");
        return new GlmWallet
        {
            Balance = available,
            TotalSpend = MonitorPayload.OptionalDouble(data, "totalSpendAmount"),
            CurrencySymbol = CurrencySymbol(MonitorPayload.String(data, "currency"))
        };
    }

    /// <summary>Windows GlmProvider.ParseV4Wallet (Program.cs L968-L982).</summary>
    internal static GlmWallet ParseV4Wallet(HttpResult result)
    {
        var root = RequireSuccess(result);
        var data = MonitorPayload.Object(root, "data")
            ?? throw new InvalidOperationException("GLM balance returned no data");
        var available = MonitorPayload.OptionalDouble(data, "available_balance");
        var total = MonitorPayload.OptionalDouble(data, "total_balance") ?? available;
        if (!available.HasValue && !total.HasValue)
            throw new InvalidOperationException("GLM balance returned no balance");
        return new GlmWallet
        {
            Balance = available ?? total,
            WalletTotal = total,
            CurrencySymbol = CurrencySymbol(MonitorPayload.String(data, "currency"))
        };
    }

    /// <summary>Windows GlmProvider.RequireSuccess (Program.cs L984-L1002).</summary>
    private static JsonElement RequireSuccess(HttpResult result)
    {
        if (IsAuthenticationFailure(result)) throw new InvalidOperationException("API key authentication failed");
        if (result.StatusCode < 200 || result.StatusCode >= 300)
            throw new InvalidOperationException("GLM HTTP " + result.StatusCode);
        var root = MonitorPayload.ParseObject(result.Body)
            ?? throw new InvalidOperationException("GLM returned invalid data");
        var code = MonitorPayload.String(root, "code");
        if (!string.IsNullOrEmpty(code)
            && int.TryParse(code, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedCode)
            && parsedCode != 0 && parsedCode != 200)
            throw new InvalidOperationException(MonitorPayload.String(root, "msg")
                ?? MonitorPayload.String(root, "message") ?? "GLM API error");
        if (MonitorPayload.String(root, "success") is { } successText
            && bool.TryParse(successText, out var success) && !success)
            throw new InvalidOperationException(MonitorPayload.String(root, "msg")
                ?? MonitorPayload.String(root, "message") ?? "GLM API error");
        return root;
    }

    /// <summary>Windows GlmProvider.IsAuthenticationFailure (Program.cs L1026-L1050).</summary>
    internal static bool IsAuthenticationFailure(HttpResult result)
    {
        if (result.StatusCode == 401 || result.StatusCode == 403) return true;
        try
        {
            var root = MonitorPayload.ParseObject(result.Body);
            if (root is null) return false;
            var code = MonitorPayload.String(root, "code");
            if (!string.IsNullOrEmpty(code)
                && int.TryParse(code, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedCode)
                && (parsedCode == 401 || parsedCode == 403))
                return true;
            var message = (MonitorPayload.String(root, "msg") ?? MonitorPayload.String(root, "message") ?? "")
                .ToLowerInvariant();
            if (MonitorPayload.Object(root, "error") is { } error)
                message += " " + (MonitorPayload.String(error, "message") ?? "").ToLowerInvariant();
            return message.Contains("unauthorized", StringComparison.Ordinal)
                || message.Contains("api key", StringComparison.Ordinal)
                || message.Contains("authorization", StringComparison.Ordinal)
                || message.Contains("token expired", StringComparison.Ordinal)
                || message.Contains("token incorrect", StringComparison.Ordinal)
                || message.Contains("鉴权", StringComparison.Ordinal)
                || message.Contains("认证", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAuthenticationException(Exception? exception) =>
        exception?.Message?.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Windows GlmProvider.Parse (Program.cs L1052-L1103).</summary>
    internal static GlmUsage ParseGlmQuota(string body)
    {
        var root = MonitorPayload.ParseObject(body)
            ?? throw new InvalidOperationException("GLM returned invalid data");

        var code = MonitorPayload.String(root, "code");
        if (!string.IsNullOrEmpty(code)
            && int.TryParse(code, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedCode)
            && parsedCode != 0 && parsedCode != 200)
        {
            var message = MonitorPayload.String(root, "msg") ?? MonitorPayload.String(root, "message") ?? "GLM API error";
            if (parsedCode == 401 || parsedCode == 403)
                throw new InvalidOperationException("API key authentication failed");
            throw new InvalidOperationException(message);
        }

        var data = MonitorPayload.Object(root, "data") ?? root;
        var usage = new GlmUsage
        {
            QuotaRead = true,
            Level = MonitorPayload.String(data, "level")
        };
        var details = new List<string>();
        foreach (var limit in MonitorPayload.Array(data, "limits") ?? [])
        {
            var type = MonitorPayload.String(limit, "type") ?? string.Empty;
            var unit = MonitorPayload.Int(limit, "unit", 0);
            var number = MonitorPayload.Int(limit, "number", 0);
            var window = ParseGlmWindow(limit, unit, number);
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
        usage.Breakdown = string.Join("  ·  ", details);
        return usage;
    }

    /// <summary>Windows GlmProvider.ParseWindow (Program.cs L1105-L1120).</summary>
    internal static RateWindow ParseGlmWindow(JsonElement limit, int unit, int number)
    {
        var window = new RateWindow
        {
            UsedPercent = Math.Max(0, Math.Min(100, MonitorPayload.Double(limit, "percentage", 0))),
            WindowDurationMins = unit == 3 ? Math.Max(1, number) * 60
                : unit == 6 ? Math.Max(1, number) * 7 * 24 * 60
                : 30 * 24 * 60
        };
        var timestamp = MonitorPayload.Long(limit, "nextResetTime", 0);
        if (timestamp > 0)
            window.ResetsAt = JavaScriptSerializerDateConverter.FromUnixMilliseconds(
                timestamp > 100000000000L ? timestamp : timestamp * 1000);
        return window;
    }

    /// <summary>Windows GlmProvider.FormatQuota (Program.cs L1122-L1130).</summary>
    internal static string FormatQuota(string label, JsonElement limit)
    {
        var current = MonitorPayload.OptionalDouble(limit, "currentValue");
        var total = MonitorPayload.OptionalDouble(limit, "usage");
        if (current.HasValue && total.HasValue)
            return label + " " + FormatNumber(current.Value) + "/" + FormatNumber(total.Value);
        return label + " 已用 "
            + MonitorPayload.Double(limit, "percentage", 0).ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>Windows GlmProvider.FormatNumber (Program.cs L1132-L1141).</summary>
    internal static string FormatNumber(double number)
    {
        if (Math.Abs(number) >= 1000000000) return (number / 1000000000d).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        if (Math.Abs(number) >= 1000000) return (number / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        if (Math.Abs(number) >= 1000) return (number / 1000d).ToString("0.##", CultureInfo.InvariantCulture) + "K";
        return number.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Windows maps a currency code to a symbol for DeepSeek
    /// (Program.cs L811: CNY ¥, USD $, otherwise "CODE ") and defaults GLM to ¥
    /// (L1013-L1018).
    /// </summary>
    internal static string CurrencySymbol(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return "¥";
        return currency.Trim().ToUpperInvariant() switch
        {
            "CNY" => "¥",
            "USD" => "$",
            _ => currency.Trim() + " "
        };
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Windows GlmProvider's private HttpResult (Program.cs L1143-L1147).</summary>
    internal sealed class HttpResult
    {
        public int StatusCode { get; init; }
        public string Body { get; init; } = string.Empty;
    }
}
