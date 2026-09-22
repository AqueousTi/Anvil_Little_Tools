using System.Globalization;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Pure chart geometry, ported from the Windows <c>CandleChart.OnRender</c>
/// (StockMonitor/StockWindow.cs L62-L143). The renderer only calls these, so the
/// realised chart can be asserted without a display.
/// </summary>
internal static class StockChartMath
{
    public const double Left = 12;
    public const double Right = 43;
    public const double Top = 12;
    public const double Bottom = 22;
    public const int GridLines = 4;

    /// <summary>Windows CandleChart.Y (StockWindow.cs L140-L143).</summary>
    public static double Y(double value, double min, double max, double top, double height) =>
        top + (max - value) / (max - min) * height;

    public static double PlotWidth(double width) => Math.Max(1, width - Left - Right);

    public static double PlotHeight(double height) => Math.Max(1, height - Top - Bottom);

    /// <summary>Windows StockWindow.cs L96: a long series or a flat intraday line.</summary>
    public static bool IsLineMode(IReadOnlyList<Candle> candles) =>
        candles.Count > 260 || candles.All(item => Math.Abs(item.Open - item.Close) < 0.0000001);

    /// <summary>Windows StockWindow.cs L113: body width clamped to 1..8.</summary>
    public static double BodyWidth(double step) => Math.Max(1, Math.Min(8, step * 0.58));

    /// <summary>Windows StockWindow.cs L87: the y of grid line 0..3.</summary>
    public static double GridY(double top, double plotHeight, int line) => top + plotHeight * line / 3.0;

    /// <summary>Windows StockWindow.cs L89: the value shown next to grid line 0..3.</summary>
    public static double GridValue(double max, double min, int line) => max - (max - min) * line / 3.0;

    /// <summary>Windows StockWindow.cs L90: three decimals for prices below ten.</summary>
    public static string PriceLabel(double value) =>
        value.ToString(value < 10 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The x axis label of one candle.
    ///
    /// **Deliberate deviation from Windows** (<c>StockMonitor/StockWindow.cs</c>
    /// L128-L135 prints "MM-dd" / "HH:mm" for the first, middle and last bar
    /// whatever the span): a one-year daily axis crosses a year, so "MM-dd" made it
    /// look like the dates ran backwards, and a middle tick that landed on an
    /// arbitrary bar did not read as a coordinate axis. Here:
    ///   * an intraday series keeps <c>HH:mm</c>;
    ///   * weekly and monthly series carry <c>yyyy-MM</c>;
    ///   * a daily series that crosses a year carries it (<c>yy-MM-dd</c>), a
    ///     within-one-year daily series keeps <c>MM-dd</c>.
    /// </summary>
    public static string AxisLabel(DateTime time, string period, bool withYear)
    {
        if (time.TimeOfDay != TimeSpan.Zero) return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (period is StockPeriods.Weekly or StockPeriods.Monthly)
            return time.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return time.ToString(withYear ? "yy-MM-dd" : "MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>True when the plotted span runs over a year boundary.</summary>
    public static bool CrossesYear(IReadOnlyList<Candle> candles) =>
        candles.Count > 1 && candles[0].Time.Year != candles[^1].Time.Year;

    /// <summary>
    /// Windows StockWindow.cs L128: first, middle and last bar, de-duplicated. The
    /// middle bar snaps to a natural month boundary (see
    /// <see cref="MiddleLabelIndex"/>), which is the deliberate axis change.
    /// </summary>
    public static int[] AxisLabelIndices(IReadOnlyList<Candle> candles, string period)
    {
        if (candles.Count == 0) return [];
        if (candles.Count == 1) return [0];
        return new[] { 0, MiddleLabelIndex(candles, period), candles.Count - 1 }.Distinct().ToArray();
    }

    /// <summary>
    /// The middle tick: the first trading day of a month closest to the midpoint,
    /// so the axis reads like a real date axis instead of "exactly bar N". Falls
    /// back to the exact midpoint when no month boundary is close enough (a short
    /// series may contain none) and for intraday series, where a month boundary is
    /// meaningless.
    /// </summary>
    public static int MiddleLabelIndex(IReadOnlyList<Candle> candles, string period)
    {
        var middle = candles.Count / 2;
        if (candles.Count < 5 || period == StockPeriods.Minute) return middle;
        // A boundary further than a quarter of the series away would not look like
        // the axis midpoint any more.
        var limit = Math.Max(1, candles.Count / 4);
        var best = middle;
        var bestDistance = int.MaxValue;
        for (var index = 1; index < candles.Count - 1; index++)
        {
            var previous = candles[index - 1].Time;
            var current = candles[index].Time;
            if (current.Year == previous.Year && current.Month == previous.Month) continue;
            var distance = Math.Abs(index - middle);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        return bestDistance <= limit ? best : middle;
    }

    /// <summary>The realised x axis of a series: which bar and the text it draws.</summary>
    public static ChartAxisLabel[] AxisLabels(IReadOnlyList<Candle> candles, string period)
    {
        var withYear = CrossesYear(candles) && period is not (StockPeriods.Weekly or StockPeriods.Monthly);
        return AxisLabelIndices(candles, period)
            .Select(index => new ChartAxisLabel(index, AxisLabel(candles[index].Time, period, withYear)))
            .ToArray();
    }

    /// <summary>Windows StockWindow.cs L118: a bar whose close is not below its open is red.</summary>
    public static bool IsUp(Candle candle) => candle.Close >= candle.Open;

    public static (double Min, double Max) Range(IReadOnlyList<Candle> candles)
    {
        var min = candles.Min(item => item.Low);
        var max = candles.Max(item => item.High);
        if (max <= min) max = min + 1;
        return (min, max);
    }

    /// <summary>Windows StockWindow.cs L134-L135: the label is clamped inside the plot.</summary>
    public static double LabelLeft(double left, double step, int index, double labelWidth, double plotWidth) =>
        Math.Max(left, Math.Min(left + plotWidth - labelWidth, left + (index + 0.5) * step - labelWidth / 2));
}

/// <summary>One realised x axis tick: which bar it points at and the text drawn.</summary>
internal readonly record struct ChartAxisLabel(int Index, string Text);

/// <summary>The realised chart geometry of the last render, for the render smoke.</summary>
internal sealed record StockChartRenderInfo(
    int CandleCount, bool LineMode, double Width, double Height, double Step, double BodyWidth,
    double Min, double Max, double PlotWidth, double PlotHeight)
{
    public override string ToString() =>
        "count=" + CandleCount.ToString(CultureInfo.InvariantCulture)
        + ",lineMode=" + LineMode
        + ",width=" + Width.ToString(CultureInfo.InvariantCulture)
        + ",height=" + Height.ToString(CultureInfo.InvariantCulture)
        + ",step=" + Step.ToString("0.####", CultureInfo.InvariantCulture)
        + ",bodyWidth=" + BodyWidth.ToString("0.####", CultureInfo.InvariantCulture)
        + ",min=" + Min.ToString("0.####", CultureInfo.InvariantCulture)
        + ",max=" + Max.ToString("0.####", CultureInfo.InvariantCulture)
        + ",plotWidth=" + PlotWidth.ToString("0.####", CultureInfo.InvariantCulture)
        + ",plotHeight=" + PlotHeight.ToString("0.####", CultureInfo.InvariantCulture);
}
