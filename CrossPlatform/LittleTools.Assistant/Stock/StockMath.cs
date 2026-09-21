using System.Globalization;
using System.Text.RegularExpressions;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Pure rules ported one to one from the Windows module (StockMonitor/StockData.cs).
/// Keeping them free of HTTP and UI state is what lets the port be asserted in the
/// headless test run.
/// </summary>
internal static partial class StockMath
{
    [GeneratedRegex("^[0-9]{6}$")]
    private static partial Regex CodePattern();

    /// <summary>Windows StockData.cs L129-L132.</summary>
    public static bool IsValidCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && CodePattern().IsMatch(code.Trim());

    /// <summary>Windows StockData.cs L134-L137: a 5/6/9 prefix is a Shanghai listing.</summary>
    public static string Symbol(string code) =>
        (code.StartsWith('5') || code.StartsWith('6') || code.StartsWith('9') ? "sh" : "sz") + code;

    /// <summary>Windows StockData.cs L139-L142.</summary>
    public static string SecId(string code) =>
        (code.StartsWith('5') || code.StartsWith('6') || code.StartsWith('9') ? "1." : "0.") + code;

    /// <summary>Windows StockData.cs L203: the eastmoney klt code for a period.</summary>
    public static int Klt(string period) =>
        period == StockPeriods.Weekly ? 102 : period == StockPeriods.Monthly ? 103 : 101;

    /// <summary>Windows StockData.cs L204-L209.</summary>
    public static int KlineLimit(string period, int rangeYears)
    {
        int limit;
        if (rangeYears == 0) limit = period == StockPeriods.Daily ? 35 : 24;
        else if (period == StockPeriods.Monthly) limit = rangeYears * 12 + 2;
        else if (period == StockPeriods.Weekly) limit = rangeYears * 53 + 3;
        else limit = rangeYears * 260 + 5;
        return Math.Max(20, Math.Min(1500, limit));
    }

    /// <summary>Windows StockData.cs L244-L249: the span is measured back from the last candle.</summary>
    public static List<Candle> FilterRange(List<Candle> source, int years)
    {
        if (years <= 0 || source.Count == 0) return source;
        var cutoff = source[^1].Time.AddYears(-years);
        return source.Where(item => item.Time >= cutoff).ToList();
    }

    /// <summary>Windows StockData.cs L374-L378.</summary>
    public static DateTime ParseQuoteTime(string? value, DateTime fallback) =>
        DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result
            : fallback;

    /// <summary>Windows StockData.cs L380-L389.</summary>
    public static double Number(string? value) => TryNumber(value, out var result) ? result : 0;

    public static bool TryNumber(string? value, out double result) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result);

    public static bool TryObjectNumber(object? value, out double result)
    {
        if (value is null)
        {
            result = 0;
            return false;
        }
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any,
            CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Windows StockData.cs L355-L372. The percentile is the share of the span's
    /// samples at or below the latest value.
    /// </summary>
    public static ValuationInfo BuildValuation(List<(DateTime Date, double Value)> source, int years, string sourceName)
    {
        source.Sort((a, b) => a.Date.CompareTo(b.Date));
        if (source.Count == 0) return new ValuationInfo { Years = years, Source = sourceName };
        var latest = source[^1];
        var cutoff = latest.Date.AddYears(-Math.Max(1, years));
        var values = source.Where(item => item.Date >= cutoff).Select(item => item.Value).ToList();
        var atOrBelow = values.Count(item => item <= latest.Value);
        return new ValuationInfo
        {
            CurrentPe = latest.Value,
            Percentile = values.Count == 0 ? null : atOrBelow * 100.0 / values.Count,
            Years = years,
            DataDate = latest.Date,
            Source = sourceName,
            SampleCount = values.Count
        };
    }
}

/// <summary>Which historical valuation source Windows picks (StockData.cs L251-L288).</summary>
internal enum StockValuationSource
{
    /// <summary>510300 uses the official CSI index PE.</summary>
    Csi300,
    /// <summary>513500 (and any S&amp;P 500 tracker by name) uses the monthly S&amp;P PE table.</summary>
    Sp500,
    /// <summary>An ordinary stock with eastmoney PE-TTM history.</summary>
    StockHistory,
    /// <summary>An ETF whose tracked index is unknown.</summary>
    Placeholder
}

/// <summary>Text and colour rules shared by both windows, from StockWindow.cs.</summary>
internal static class StockFormat
{
    public enum ChangeKind
    {
        Up,
        Down,
        Flat
    }

    /// <summary>Windows StockWindow.cs L499: three decimals below ten yuan.</summary>
    public static string Price(double price) =>
        price.ToString(price < 10 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>Windows StockWindow.cs L524.</summary>
    public static string Iopv(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    /// <summary>Windows ApplyPremiumStyle, StockWindow.cs L883.</summary>
    public static string Premium(double? value) =>
        value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%" : "不适用";

    /// <summary>Windows StockWindow.cs L512.</summary>
    public static string Change(double changePercent) =>
        (changePercent >= 0 ? "+" : "") + changePercent.ToString("0.00", CultureInfo.InvariantCulture) + "%";

    /// <summary>Windows StockWindow.cs L894.</summary>
    public static string Pe(double? value) =>
        value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) + "×" : "--";

    /// <summary>Windows StockWindow.cs L964: the fund manager prefix is dropped in tabs.</summary>
    public static string ShortName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Replace("华泰柏瑞", "").Replace("博时", "");

    /// <summary>Windows StockWindow.cs L505.</summary>
    public static string CompactUpdated(StockQuote? quote) =>
        quote is null ? "单击查看明细" : "更新 " + quote.UpdatedAt.ToString("HH:mm") + "  ·  单击查看明细";

    /// <summary>Windows StockWindow.cs L472.</summary>
    public static string Status(StockQuote? quote) =>
        (quote is null ? string.Empty : "更新于 " + quote.UpdatedAt.ToString("MM-dd HH:mm")) + "  ·  Ctrl + Alt + Q";

    /// <summary>Windows StockWindow.cs L547-L548.</summary>
    public static string TabCaption(string code, StockQuote? quote) =>
        code + (quote is null ? string.Empty : "  " + ShortName(quote.Name));

    public static double TabWidth(string caption) => Math.Max(100, Math.Min(185, 48 + caption.Length * 8));

    /// <summary>Windows ChangeBrush, StockWindow.cs L963: red up, green down, white flat.</summary>
    public static ChangeKind ChangeOf(double changePercent) =>
        changePercent > 0 ? ChangeKind.Up : changePercent < 0 ? ChangeKind.Down : ChangeKind.Flat;

    /// <summary>Windows ApplyPremiumStyle, StockWindow.cs L884: outlined while below 2%.</summary>
    public static bool PremiumOutlineEnabled(double? premium) => premium.HasValue && premium.Value < 2;

    /// <summary>Windows RenderValuation, StockWindow.cs L533.</summary>
    public static string Percentile(ValuationInfo? value) =>
        value is not null && value.Percentile.HasValue
            ? value.Years + "年 · " + value.Percentile.Value.ToString("0", CultureInfo.InvariantCulture) + "%"
            : "样本不足";

    /// <summary>Windows RenderValuation, StockWindow.cs L534.</summary>
    public static string ValuationSource(ValuationInfo? value) =>
        value is null
            ? "估值数据暂不可用"
            : value.Source + (value.DataDate.HasValue
                ? " · " + value.DataDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : string.Empty);

    /// <summary>Windows RenderQuote, StockWindow.cs L523: ordinary stocks show their type, not IOPV.</summary>
    public static string InstrumentLabel(StockQuote? quote) =>
        quote is not null && !quote.IsEtf ? "证券类型" : "IOPV";

    public static string InstrumentValue(StockQuote? quote)
    {
        if (quote is not null && !quote.IsEtf) return "普通股票";
        return quote is not null && quote.Iopv.HasValue ? Iopv(quote.Iopv.Value) : "--";
    }

    /// <summary>Windows ApplySecurityMetric, StockWindow.cs L891-L899.</summary>
    public static string SecurityMetricLabel(StockQuote? quote) =>
        quote is not null && !quote.IsEtf ? "滚动 PE" : "参考溢价";

    /// <summary>Windows UpdateActionButtons, StockWindow.cs L599.</summary>
    public static string AlertCaption(bool supportsPremium, bool alertEnabled) =>
        !supportsPremium ? "溢价提醒不适用" : alertEnabled ? "提醒 < 2%  ✓" : "提醒 < 2%";
}

/// <summary>
/// Stateful half of the Windows <c>CheckAlert</c> (StockWindow.cs L480-L493): the
/// alert fires when the premium falls below the threshold and only re-arms once it
/// has recovered above it.
/// </summary>
internal sealed class StockAlertTracker
{
    private readonly Dictionary<string, bool> _lastBelow = new(StringComparer.Ordinal);

    public StockAlert? Check(StockWatchEntry entry, StockQuote quote)
    {
        if (!entry.AlertEnabled || !quote.IsEtf || !quote.PremiumPercent.HasValue) return null;
        var below = quote.PremiumPercent.Value < entry.PremiumThreshold;
        var known = _lastBelow.TryGetValue(entry.Code ?? string.Empty, out var previous);
        _lastBelow[entry.Code ?? string.Empty] = below;
        if (!below || (known && previous)) return null;
        return new StockAlert("Little Tools · 溢价提醒",
            quote.Code + " " + quote.Name + " 当前参考溢价 "
            + quote.PremiumPercent.Value.ToString("0.00", CultureInfo.InvariantCulture)
            + "%（阈值 " + entry.PremiumThreshold.ToString("0.##", CultureInfo.InvariantCulture) + "%）");
    }
}

internal sealed record StockAlert(string Title, string Message);
