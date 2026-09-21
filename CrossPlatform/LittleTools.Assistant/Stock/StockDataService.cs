using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Market data access, ported from the Windows <c>StockDataService</c>
/// (StockMonitor/StockData.cs L116-L426). Every URL, field index and source label
/// is the Windows one:
///   * quotes   - Tencent qt.gtimg.cn, GB18030 encoded, fields 1/3/4/30/32/61/77/78
///   * PE       - eastmoney push2 /api/qt/stock/get f162
///   * K line   - eastmoney push2his kline (klt 101/102/103)
///   * minute   - eastmoney push2 trends2
///   * PE-TTM   - eastmoney datacenter-web RPT_VALUEANALYSIS_DET
///   * 510300   - csindex.com.cn official index performance
///   * 513500   - multpl.com monthly S&amp;P 500 PE table
/// The HTTP handler is injectable so the recorded sample responses can replay the
/// exact parsing path without a network.
/// </summary>
internal sealed class StockDataService : IDisposable
{
    private static readonly TimeSpan ValuationCacheLifetime = TimeSpan.FromHours(6);

    private readonly HttpClient _client;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, (DateTime At, ValuationInfo Value)> _valuationCache = new(StringComparer.Ordinal);

    static StockDataService() =>
        // GB18030 is not in the default .NET provider set on Linux; the
        // CodePages provider ships in the shared framework.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public StockDataService(HttpMessageHandler? handler = null, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.Now);
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = TimeSpan.FromSeconds(15);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LittleTools/1.0");
    }

    /// <summary>Windows StockData.cs L129-L132.</summary>
    public static bool IsValidCode(string? code) => StockMath.IsValidCode(code);

    // ------------------------------------------------------------------ quote

    /// <summary>Windows StockData.cs L144-L183.</summary>
    public async Task<StockQuote> GetQuoteAsync(string code, CancellationToken cancellationToken = default)
    {
        code = (code ?? string.Empty).Trim();
        if (!IsValidCode(code)) throw new InvalidOperationException("请输入六位沪深证券代码");
        var bytes = await _client.GetByteArrayAsync(QuoteUrl(code), cancellationToken).ConfigureAwait(false);
        var quote = ParseQuote(Encoding.GetEncoding("GB18030").GetString(bytes), code, _now());
        if (!quote.IsEtf)
        {
            try { quote.Pe = await GetStockPeAsync(code, cancellationToken).ConfigureAwait(false); }
            catch
            {
                // A missing PE never fails the quote, exactly like Windows.
            }
        }
        return quote;
    }

    internal static string QuoteUrl(string code) => "https://qt.gtimg.cn/q=" + StockMath.Symbol(code);

    /// <summary>Windows StockData.cs L150-L182, decoding already done by the caller.</summary>
    internal static StockQuote ParseQuote(string text, string code, DateTime now)
    {
        var first = text.IndexOf('"');
        var last = text.LastIndexOf('"');
        if (first < 0 || last <= first) throw new InvalidOperationException("没有查询到该证券代码");
        var fields = text.Substring(first + 1, last - first - 1).Split('~');
        if (fields.Length < 35 || fields[2] != code) throw new InvalidOperationException("行情数据格式异常");

        var quote = new StockQuote
        {
            Code = code,
            Name = fields[1],
            Price = StockMath.Number(fields[3]),
            PreviousClose = StockMath.Number(fields[4]),
            ChangePercent = StockMath.Number(fields[32]),
            IsEtf = fields.Length > 61 && IsExchangeTradedFundType(fields[61]),
            UpdatedAt = StockMath.ParseQuoteTime(fields.Length > 30 ? fields[30] : null, now),
            DataSource = "腾讯行情"
        };
        if (quote.Price <= 0) throw new InvalidOperationException("该证券当前没有有效行情");
        if (quote.IsEtf && fields.Length > 78)
        {
            if (StockMath.TryNumber(fields[77], out var premium)) quote.PremiumPercent = premium;
            if (StockMath.TryNumber(fields[78], out var iopv) && iopv > 0) quote.Iopv = iopv;
            if (!quote.PremiumPercent.HasValue && quote.Iopv.HasValue)
                quote.PremiumPercent = (quote.Price / quote.Iopv.Value - 1.0) * 100.0;
        }
        return quote;
    }

    /// <summary>Windows StockData.cs L185-L189.</summary>
    private static bool IsExchangeTradedFundType(string value) =>
        string.Equals(value, "ETF", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "LOF", StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows StockData.cs L191-L198.</summary>
    public async Task<double?> GetStockPeAsync(string code, CancellationToken cancellationToken = default)
    {
        var text = await _client.GetStringAsync(StockPeUrl(code), cancellationToken).ConfigureAwait(false);
        return ParseStockPe(text);
    }

    internal static string StockPeUrl(string code) =>
        "https://push2.eastmoney.com/api/qt/stock/get?fltt=2&secid=" + StockMath.SecId(code) + "&fields=f162";

    internal static double? ParseStockPe(string text)
    {
        var root = ParseJsonObject(text);
        var data = Child(root, "data");
        if (data is not { } value) return null;
        return TryObjectNumber(Value(value, "f162"), out var pe) && pe > 0 ? pe : null;
    }

    // ----------------------------------------------------------------- K line

    /// <summary>Windows StockData.cs L200-L224.</summary>
    public async Task<List<Candle>> GetCandlesAsync(string code, string period, int rangeYears,
        CancellationToken cancellationToken = default)
    {
        if (period == StockPeriods.Minute) return await GetMinuteAsync(code, cancellationToken).ConfigureAwait(false);
        var limit = StockMath.KlineLimit(period, rangeYears);
        var text = await _client.GetStringAsync(KlineUrl(code, period, limit), cancellationToken).ConfigureAwait(false);
        return StockMath.FilterRange(ParseKlines(text), rangeYears);
    }

    internal static string KlineUrl(string code, string period, int limit) =>
        "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=" + StockMath.SecId(code)
        + "&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56&klt=" + StockMath.Klt(period)
        + "&fqt=1&end=20500101&lmt=" + limit.ToString(CultureInfo.InvariantCulture);

    /// <summary>Windows StockData.cs L213-L223.</summary>
    internal static List<Candle> ParseKlines(string text)
    {
        var root = ParseJsonObject(text);
        var data = Child(root, "data");
        var result = new List<Candle>();
        if (data is not { } value) return result;
        foreach (var raw in ArrayValue(value, "klines"))
        {
            var parts = (raw.GetString() ?? string.Empty).Split(',');
            if (parts.Length < 5
                || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            result.Add(new Candle
            {
                Time = date, Open = StockMath.Number(parts[1]), Close = StockMath.Number(parts[2]),
                High = StockMath.Number(parts[3]), Low = StockMath.Number(parts[4])
            });
        }
        return result;
    }

    /// <summary>Windows StockData.cs L226-L242.</summary>
    public async Task<List<Candle>> GetMinuteAsync(string code, CancellationToken cancellationToken = default)
    {
        var text = await _client.GetStringAsync(MinuteUrl(code), cancellationToken).ConfigureAwait(false);
        return ParseTrends(text);
    }

    internal static string MinuteUrl(string code) =>
        "https://push2.eastmoney.com/api/qt/stock/trends2/get?secid=" + StockMath.SecId(code)
        + "&fields1=f1,f2,f3,f4,f5,f6,f7,f8&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&iscca=0&ndays=1";

    /// <summary>Windows StockData.cs L233-L240: the minute series repeats the price as open and close.</summary>
    internal static List<Candle> ParseTrends(string text)
    {
        var root = ParseJsonObject(text);
        var data = Child(root, "data");
        var result = new List<Candle>();
        if (data is not { } value) return result;
        foreach (var raw in ArrayValue(value, "trends"))
        {
            var parts = (raw.GetString() ?? string.Empty).Split(',');
            if (parts.Length < 5
                || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
            var price = StockMath.Number(parts[1]);
            result.Add(new Candle
            {
                Time = time, Open = price, Close = price,
                High = StockMath.Number(parts[3]), Low = StockMath.Number(parts[4])
            });
        }
        return result;
    }

    // -------------------------------------------------------------- valuation

    /// <summary>Windows StockData.cs L251-L288, including the six hour cache.</summary>
    public async Task<ValuationInfo> GetValuationAsync(string code, bool isEtf, double? stockPe, int years,
        string? securityName = null, CancellationToken cancellationToken = default)
    {
        var key = code + ":" + years.ToString(CultureInfo.InvariantCulture);
        if (_valuationCache.TryGetValue(key, out var cached)
            && _now() - cached.At < ValuationCacheLifetime) return cached.Value;

        ValuationInfo value;
        switch (ResolveValuationSource(code, isEtf, securityName))
        {
            case StockValuationSource.Csi300:
                value = await GetCsi300ValuationAsync(years, cancellationToken).ConfigureAwait(false);
                break;
            case StockValuationSource.Sp500:
                value = await GetSp500ValuationAsync(years, cancellationToken).ConfigureAwait(false);
                break;
            case StockValuationSource.StockHistory:
                try
                {
                    value = await GetStockHistoricalValuationAsync(code, years, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    value = Placeholder(stockPe, years, "东方财富动态PE");
                }
                break;
            default:
                value = Placeholder(stockPe, years, isEtf ? "暂未匹配跟踪指数" : "东方财富动态PE");
                break;
        }

        _valuationCache[key] = (_now(), value);
        return value;
    }

    /// <summary>Windows StockData.cs L258-L260.</summary>
    internal static StockValuationSource ResolveValuationSource(string code, bool isEtf, string? securityName)
    {
        if (code == "510300") return StockValuationSource.Csi300;
        if (code == "513500"
            || (isEtf && !string.IsNullOrWhiteSpace(securityName)
                && securityName.IndexOf("标普500", StringComparison.OrdinalIgnoreCase) >= 0))
            return StockValuationSource.Sp500;
        return isEtf ? StockValuationSource.Placeholder : StockValuationSource.StockHistory;
    }

    private static ValuationInfo Placeholder(double? stockPe, int years, string source) => new()
    {
        CurrentPe = stockPe,
        Percentile = null,
        Years = years,
        DataDate = stockPe.HasValue ? DateTime.Today : null,
        Source = source,
        SampleCount = 0
    };

    /// <summary>Windows StockData.cs L290-L310.</summary>
    private async Task<ValuationInfo> GetStockHistoricalValuationAsync(string code, int years,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Max(280, Math.Min(1500, Math.Max(1, years) * 260 + 30));
        var text = await _client.GetStringAsync(StockValuationUrl(code, pageSize), cancellationToken).ConfigureAwait(false);
        return StockMath.BuildValuation(ParseStockValuation(text), years, "东方财富 · PE-TTM");
    }

    internal static string StockValuationUrl(string code, int pageSize) =>
        "https://datacenter-web.eastmoney.com/api/data/v1/get?reportName=RPT_VALUEANALYSIS_DET"
        + "&columns=SECURITY_CODE%2CTRADE_DATE%2CPE_TTM"
        + "&filter=(SECURITY_CODE%3D%22" + code + "%22)"
        + "&pageNumber=1&pageSize=" + pageSize.ToString(CultureInfo.InvariantCulture)
        + "&sortTypes=-1&sortColumns=TRADE_DATE&source=WEB&client=WEB";

    /// <summary>Windows StockData.cs L300-L308.</summary>
    internal static List<(DateTime Date, double Value)> ParseStockValuation(string text)
    {
        var points = new List<(DateTime, double)>();
        var root = ParseJsonObject(text);
        var result = Child(root, "result");
        if (result is not { } container) return points;
        foreach (var row in ArrayValue(container, "data"))
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!DateTime.TryParse(Convert.ToString(Value(row, "TRADE_DATE"), CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (TryObjectNumber(Value(row, "PE_TTM"), out var pe) && pe > 0) points.Add((date, pe));
        }
        return points;
    }

    /// <summary>Windows StockData.cs L312-L333.</summary>
    private async Task<ValuationInfo> GetCsi300ValuationAsync(int years, CancellationToken cancellationToken)
    {
        var end = _now().Date;
        var start = end.AddYears(-Math.Max(1, years)).AddDays(-7);
        using var request = new HttpRequestMessage(HttpMethod.Get, Csi300Url(start, end));
        // The index site rejects a request without a referrer, like Windows.
        request.Headers.Referrer = new Uri("https://www.csindex.com.cn/");
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return StockMath.BuildValuation(ParseCsi300(text), years, "中证指数官方");
    }

    internal static string Csi300Url(DateTime start, DateTime end) =>
        "https://www.csindex.com.cn/csindex-home/perf/index-perf?indexCode=000300&startDate="
        + start.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "&endDate="
        + end.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Windows StockData.cs L323-L331: the index PE arrives in the "peg" field.</summary>
    internal static List<(DateTime Date, double Value)> ParseCsi300(string text)
    {
        var points = new List<(DateTime, double)>();
        var root = ParseJsonObject(text);
        foreach (var row in ArrayValue(root, "data"))
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!DateTime.TryParseExact(Convert.ToString(Value(row, "tradeDate"), CultureInfo.InvariantCulture),
                    "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (TryObjectNumber(Value(row, "peg"), out var pe) && pe > 0) points.Add((date, pe));
        }
        return points;
    }

    /// <summary>Windows StockData.cs L335-L353.</summary>
    private async Task<ValuationInfo> GetSp500ValuationAsync(int years, CancellationToken cancellationToken)
    {
        var html = await _client.GetStringAsync(Sp500Url, cancellationToken).ConfigureAwait(false);
        return StockMath.BuildValuation(ParseSp500(html), years, "Multpl · 月度PE");
    }

    internal const string Sp500Url = "https://www.multpl.com/s-p-500-pe-ratio/table/by-month";

    private static readonly Regex Sp500RowPattern = new(
        "<tr[^>]*>\\s*<td>(?<date>[^<]+)</td>\\s*<td>(?<value>.*?)</td>\\s*</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Windows StockData.cs L339-L351.</summary>
    internal static List<(DateTime Date, double Value)> ParseSp500(string html)
    {
        var points = new List<(DateTime, double)>();
        foreach (Match row in Sp500RowPattern.Matches(html))
        {
            var dateText = WebUtility.HtmlDecode(Regex.Replace(row.Groups["date"].Value, "<.*?>", "")).Trim();
            var valueText = WebUtility.HtmlDecode(Regex.Replace(row.Groups["value"].Value, "<.*?>", ""))
                .Replace("†", "").Trim();
            if (DateTime.TryParse(dateText, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var date)
                && double.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out var pe) && pe > 0)
                points.Add((date, pe));
        }
        return points;
    }

    // ------------------------------------------------------------ json helpers

    /// <summary>Windows StockData.cs L397-L402.</summary>
    private static JsonElement ParseJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("数据源返回了无效JSON");
            return root;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("数据源返回了无效JSON");
        }
    }

    private static JsonElement? Child(JsonElement parent, string key) =>
        Value(parent, key) is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } value ? value : null;

    private static JsonElement? Value(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var value) ? value : null;

    private static IEnumerable<JsonElement> ArrayValue(JsonElement parent, string key) =>
        Value(parent, key) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    /// <summary>Windows StockData.cs L391-L395: numbers may arrive as strings.</summary>
    private static bool TryObjectNumber(JsonElement? value, out double result)
    {
        result = 0;
        if (value is not { } element) return false;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out result),
            JsonValueKind.String => StockMath.TryNumber(element.GetString(), out result),
            _ => false
        };
    }

    public void Dispose() => _client.Dispose();
}
