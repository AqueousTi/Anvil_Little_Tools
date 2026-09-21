namespace LittleTools.Assistant.Stock;

/// <summary>
/// Stock monitor data model. Member names and the JSON shape deliberately match
/// the Windows <c>StockMonitor</c> module (public PascalCase fields written by
/// JavaScriptSerializer) so both platforms read and write the same
/// <c>settings.json</c>. Ported from StockMonitor/StockData.cs L16-L67.
/// </summary>
internal sealed class StockWatchEntry
{
    public string? Code { get; set; }
    public bool AlertEnabled { get; set; } = true;
    public double PremiumThreshold { get; set; } = 2.0;
}

internal sealed class StockSettings
{
    public List<StockWatchEntry> Watched { get; set; } = [];
    public string SelectedCode { get; set; } = "513500";
    public bool Topmost { get; set; }
    public bool Compact { get; set; }
    public string KlinePeriod { get; set; } = StockPeriods.Daily;

    /// <summary>0 means the one month span, otherwise whole years (1, 3 or 5).</summary>
    public int RangeYears { get; set; } = 1;

    /// <summary>NaN until the window has been positioned, exactly like Windows.</summary>
    public double Left { get; set; } = double.NaN;

    public double Top { get; set; } = double.NaN;
}

/// <summary>Windows StockData.cs L35-L48.</summary>
internal sealed class StockQuote
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Price { get; set; }
    public double PreviousClose { get; set; }
    public double ChangePercent { get; set; }
    public bool IsEtf { get; set; }
    public double? Iopv { get; set; }
    public double? PremiumPercent { get; set; }
    public double? Pe { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string DataSource { get; set; } = string.Empty;
}

/// <summary>Windows StockData.cs L50-L57.</summary>
internal sealed class Candle
{
    public DateTime Time { get; set; }
    public double Open { get; set; }
    public double Close { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
}

/// <summary>Windows StockData.cs L59-L67.</summary>
internal sealed class ValuationInfo
{
    public double? CurrentPe { get; set; }
    public double? Percentile { get; set; }
    public int Years { get; set; }
    public DateTime? DataDate { get; set; }
    public string Source { get; set; } = string.Empty;
    public int SampleCount { get; set; }
}

/// <summary>The Windows KlinePeriod values (StockWindow.cs L362).</summary>
internal static class StockPeriods
{
    public const string Minute = "Minute";
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";

    public static bool IsKnown(string? value) =>
        value is Minute or Daily or Weekly or Monthly;
}
