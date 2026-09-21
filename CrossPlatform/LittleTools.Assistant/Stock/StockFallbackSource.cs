using System.Globalization;
using System.Text.Json;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Reads the K line and the intraday series from Tencent, the host that already
/// serves the quote line, for the networks where eastmoney's push2/push2his
/// hosts refuse the request. This source does <b>not</b> exist in the Windows
/// module and is an intentional addition: see LINUX_PORTING.md.
///
/// eastmoney is always asked first (see <see cref="StockDataService.GetCandlesAsync"/>);
/// this source is only reached when that request fails at the transport level,
/// and it never invents a series: an unparsable or missing response throws, so
/// the detail window still ends up on the Windows "暂无走势数据" path.
///
/// The field order of the kline rows is the one eastmoney uses
/// (date, open, close, high, low, volume) and the prices are 前复权, matching
/// the <c>fqt=1</c> the Windows request asks for.
/// </summary>
internal static class StockFallbackSource
{
    private const string KlineEndpoint = "https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param=";
    private const string MinuteEndpoint = "https://web.ifzq.gtimg.cn/appstock/app/minute/query?code=";

    /// <summary>The Tencent series name for a period: day / week / month.</summary>
    public static string PeriodName(string period) => period switch
    {
        StockPeriods.Weekly => "week",
        StockPeriods.Monthly => "month",
        _ => "day"
    };

    public static string KlineUrl(string code, string period, int limit) =>
        KlineEndpoint + StockMath.Symbol(code) + "," + PeriodName(period) + ",,,"
        + limit.ToString(CultureInfo.InvariantCulture) + ",qfq";

    public static string MinuteUrl(string code) => MinuteEndpoint + StockMath.Symbol(code);

    /// <summary>Parses the "qfqday" / "qfqweek" / "qfqmonth" table into candles.</summary>
    public static List<Candle> ParseKlines(string text, string code, string period)
    {
        var result = new List<Candle>();
        var root = StockDataService.ParseJsonObject(text);
        if (StockDataService.Child(root, "data") is not { } data) return result;
        if (StockDataService.Child(data, StockMath.Symbol(code)) is not { } series) return result;

        // A fund that was never adjusted answers under the plain period name.
        var rows = StockDataService.ArrayValue(series, "qfq" + PeriodName(period)).ToArray();
        if (rows.Length == 0) rows = StockDataService.ArrayValue(series, PeriodName(period)).ToArray();

        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var parts = row.EnumerateArray().Select(Cell).ToArray();
            if (parts.Length < 5
                || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            result.Add(new Candle
            {
                Time = date, Open = StockMath.Number(parts[1]), Close = StockMath.Number(parts[2]),
                High = StockMath.Number(parts[3]), Low = StockMath.Number(parts[4])
            });
        }
        return result;
    }

    /// <summary>
    /// Parses the intraday series ("0930 4.586 23421 10740871.00") into the same
    /// price-only candles the eastmoney trends parser builds, so the chart keeps
    /// drawing the intraday line.
    /// </summary>
    public static List<Candle> ParseMinutes(string text, string code)
    {
        var result = new List<Candle>();
        var root = StockDataService.ParseJsonObject(text);
        if (StockDataService.Child(root, "data") is not { } data) return result;
        if (StockDataService.Child(data, StockMath.Symbol(code)) is not { } series) return result;
        if (StockDataService.Child(series, "data") is not { } day) return result;

        var dayText = StockDataService.Value(day, "date") is { ValueKind: JsonValueKind.String } date
            ? date.GetString()
            : null;
        foreach (var row in StockDataService.ArrayValue(day, "data"))
        {
            if (row.ValueKind != JsonValueKind.String) continue;
            var parts = (row.GetString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].Length != 4 || dayText is null) continue;
            if (!DateTime.TryParseExact(dayText + " " + parts[0], "yyyyMMdd HHmm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time)) continue;
            var price = StockMath.Number(parts[1]);
            if (price <= 0) continue;
            result.Add(new Candle { Time = time, Open = price, Close = price, High = price, Low = price });
        }
        return result;
    }

    private static string Cell(JsonElement element) => element.ValueKind == JsonValueKind.String
        ? element.GetString() ?? string.Empty
        : element.ToString();
}
