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

    /// <summary>Windows StockWindow.cs L131: date labels for daily bars, time labels for intraday.</summary>
    public static string TimeLabel(DateTime time) =>
        time.TimeOfDay == TimeSpan.Zero ? "MM-dd" : "HH:mm";

    /// <summary>Windows StockWindow.cs L118: a bar whose close is not below its open is red.</summary>
    public static bool IsUp(Candle candle) => candle.Close >= candle.Open;

    public static (double Min, double Max) Range(IReadOnlyList<Candle> candles)
    {
        var min = candles.Min(item => item.Low);
        var max = candles.Max(item => item.High);
        if (max <= min) max = min + 1;
        return (min, max);
    }

    /// <summary>Windows StockWindow.cs L128: first, middle and last bar, de-duplicated.</summary>
    public static IReadOnlyList<int> LabelIndices(int count) =>
        new[] { 0, count / 2, count - 1 }.Distinct().ToArray();

    /// <summary>Windows StockWindow.cs L134-L135: the label is clamped inside the plot.</summary>
    public static double LabelLeft(double left, double step, int index, double labelWidth, double plotWidth) =>
        Math.Max(left, Math.Min(left + plotWidth - labelWidth, left + (index + 0.5) * step - labelWidth / 2));
}

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
