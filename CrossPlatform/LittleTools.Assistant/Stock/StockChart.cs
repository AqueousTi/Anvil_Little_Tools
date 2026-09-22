using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// The self drawn K line / intraday chart. WPF used a FrameworkElement with
/// StreamGeometry and FormattedText; the Avalonia port redraws the identical
/// primitives through <see cref="DrawingContext"/> and shares every coordinate
/// with <see cref="StockChartMath"/>.
/// </summary>
internal sealed class CandleChart : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(StockPalette.ChartBackground);
    private static readonly IPen BackgroundPen = new Pen(new SolidColorBrush(StockPalette.ChartBorder), 1);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(StockPalette.ChartGrid), 1);
    private static readonly IBrush LabelBrush = new SolidColorBrush(StockPalette.ChartLabel);
    private static readonly IBrush EmptyBrush = new SolidColorBrush(StockPalette.EmptyText);
    private static readonly IBrush UpBrush = new SolidColorBrush(StockPalette.UpCandle);
    private static readonly IBrush DownBrush = new SolidColorBrush(StockPalette.DownCandle);
    private static readonly IPen LinePen = new Pen(new SolidColorBrush(StockPalette.LineStroke), 1.5);

    private List<Candle> _candles = [];

    public IReadOnlyList<Candle> Candles => _candles;

    /// <summary>The geometry of the last render; null before the first paint.</summary>
    public StockChartRenderInfo? LastRender { get; private set; }

    /// <summary>
    /// The x axis labels of the last render, in draw order, and the bar each one
    /// points at. Exposed for the render smoke so the axis can be asserted to carry
    /// formatted dates on a natural month boundary (the first port printed the
    /// "MM-dd" format string itself; Windows printed a bare date on an arbitrary
    /// middle bar).
    /// </summary>
    public IReadOnlyList<string> LastLabels { get; private set; } = [];

    /// <summary>The bar index behind each entry of <see cref="LastLabels"/>.</summary>
    public IReadOnlyList<int> LastLabelIndices { get; private set; } = [];

    private string _period = StockPeriods.Daily;

    /// <summary>True when the last render drew the "暂无走势数据" placeholder.</summary>
    public bool LastRenderWasEmpty { get; private set; }

    /// <summary>
    /// Windows SetData: null clears the series and repaints. The period is needed
    /// to pick the axis format (daily vs weekly/monthly vs intraday).
    /// </summary>
    public void SetData(IEnumerable<Candle>? value, string period)
    {
        _candles = value?.ToList() ?? [];
        _period = StockPeriods.IsKnown(period) ? period : StockPeriods.Daily;
        LastRender = null;
        LastLabels = [];
        LastLabelIndices = [];
        LastRenderWasEmpty = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 20 || height < 20) return;

        // Windows: DrawRoundedRectangle(ARGB(55, 8, 9, 12), ARGB(30) border, 0.5 inset, radius 10).
        context.DrawRectangle(BackgroundBrush, BackgroundPen,
            new RoundedRect(new Rect(0.5, 0.5, width - 1, height - 1), 10));

        if (_candles.Count < 2)
        {
            var empty = Text("暂无走势数据", 11, EmptyBrush);
            context.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
            LastLabels = [];
            LastLabelIndices = [];
            LastRenderWasEmpty = true;
            LastRender = new StockChartRenderInfo(_candles.Count, false, width, height, 0, 0, 0, 0,
                StockChartMath.PlotWidth(width), StockChartMath.PlotHeight(height));
            return;
        }

        var plotWidth = StockChartMath.PlotWidth(width);
        var plotHeight = StockChartMath.PlotHeight(height);
        var (min, max) = StockChartMath.Range(_candles);

        for (var line = 0; line < StockChartMath.GridLines; line++)
        {
            var y = StockChartMath.GridY(StockChartMath.Top, plotHeight, line);
            context.DrawLine(GridPen, new Point(StockChartMath.Left, y), new Point(StockChartMath.Left + plotWidth, y));
            var label = Text(StockChartMath.PriceLabel(StockChartMath.GridValue(max, min, line)), 9,
                LabelBrush, CultureInfo.InvariantCulture);
            context.DrawText(label, new Point(width - StockChartMath.Right + 5, y - label.Height / 2));
        }

        var step = plotWidth / _candles.Count;
        var lineMode = StockChartMath.IsLineMode(_candles);
        var bodyWidth = StockChartMath.BodyWidth(step);
        if (lineMode)
        {
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                for (var index = 0; index < _candles.Count; index++)
                {
                    var point = new Point(StockChartMath.Left + (index + 0.5) * step,
                        StockChartMath.Y(_candles[index].Close, min, max, StockChartMath.Top, plotHeight));
                    if (index == 0) sink.BeginFigure(point, false);
                    else sink.LineTo(point);
                }
            }
            context.DrawGeometry(null, LinePen, geometry);
        }
        else
        {
            foreach (var (item, index) in _candles.Select((item, index) => (item, index)))
            {
                var x = StockChartMath.Left + (index + 0.5) * step;
                var brush = StockChartMath.IsUp(item) ? UpBrush : DownBrush;
                var pen = new Pen(brush, 1);
                context.DrawLine(pen,
                    new Point(x, StockChartMath.Y(item.High, min, max, StockChartMath.Top, plotHeight)),
                    new Point(x, StockChartMath.Y(item.Low, min, max, StockChartMath.Top, plotHeight)));
                var y1 = StockChartMath.Y(item.Open, min, max, StockChartMath.Top, plotHeight);
                var y2 = StockChartMath.Y(item.Close, min, max, StockChartMath.Top, plotHeight);
                context.DrawRectangle(brush, null,
                    new Rect(x - bodyWidth / 2, Math.Min(y1, y2), bodyWidth, Math.Max(1, Math.Abs(y2 - y1))));
            }
        }

        var labels = new List<string>();
        var labelIndices = new List<int>();
        foreach (var (index, text) in StockChartMath.AxisLabels(_candles, _period))
        {
            labels.Add(text);
            labelIndices.Add(index);
            var label = Text(text, 9, LabelBrush, CultureInfo.InvariantCulture);
            var x = StockChartMath.LabelLeft(StockChartMath.Left, step, index, label.Width, plotWidth);
            context.DrawText(label, new Point(x, height - StockChartMath.Bottom + 4));
        }

        LastLabels = labels;
        LastLabelIndices = labelIndices;
        LastRenderWasEmpty = false;
        LastRender = new StockChartRenderInfo(_candles.Count, lineMode, width, height, step, bodyWidth,
            min, max, plotWidth, plotHeight);
    }

    private static FormattedText Text(string value, double size, IBrush brush, CultureInfo? culture = null) => new(
        value,
        culture ?? System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
        FlowDirection.LeftToRight,
        new Typeface(StockTheme.UiFont),
        size,
        brush);
}
