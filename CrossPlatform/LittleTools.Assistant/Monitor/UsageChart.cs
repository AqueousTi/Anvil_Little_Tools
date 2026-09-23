using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LittleTools.Assistant.Monitor;

/// <summary>The realised geometry of the last chart paint, for the render smoke.</summary>
internal readonly record struct MonitorChartRenderInfo(
    int PointCount,
    double Width,
    double Height,
    double PlotWidth,
    double PlotHeight,
    double Maximum,
    double LastX,
    double LastY,
    bool Empty)
{
    public override string ToString() =>
        "points=" + PointCount + ",size=" + Width.ToString("0.#") + "x" + Height.ToString("0.#")
        + ",plot=" + PlotWidth.ToString("0.#") + "x" + PlotHeight.ToString("0.#")
        + ",max=" + Maximum.ToString("0.####")
        + ",last=" + LastX.ToString("0.#") + "," + LastY.ToString("0.#")
        + ",empty=" + Empty;
}

/// <summary>
/// The self drawn spending trend. Windows used a WPF <c>FrameworkElement</c> with
/// <c>StreamGeometry</c> (AIUsageMonitor/Program.cs L2230-L2289): a rounded
/// rectangle background, three horizontal grid lines inset by 7, the series as a
/// 1.8 wide polyline in ARGB(225) of the provider colour, and a 2.2 radius dot on
/// the last point. The Avalonia port redraws the identical primitives and
/// publishes <see cref="LastRender"/> so a run can assert the realised geometry
/// and the drawn colour from the pixels.
/// </summary>
internal sealed class UsageChart : Control
{
    /// <summary>Windows insets the plot area by 7 on every side.</summary>
    public const double Inset = 7;

    private static readonly IPen GridPen = new Pen(new SolidColorBrush(MonitorTheme.ChartGrid), 1);
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(MonitorTheme.ChartBackground);

    private List<UsagePoint> _points = [];
    private DateTime _visibleStart = DateTime.Now;
    private Color _seriesColor = MonitorTheme.DeepSeekColor;

    /// <summary>The last paint, or null before the first one.</summary>
    public MonitorChartRenderInfo? LastRender { get; private set; }

    /// <summary>True when the last paint drew nothing because the series was empty.</summary>
    public bool LastRenderWasEmpty { get; private set; }

    /// <summary>The series colour of the last paint.</summary>
    public Color LastSeriesColor { get; private set; } = MonitorTheme.DeepSeekColor;

    /// <summary>The points of the last paint, in draw order.</summary>
    public IReadOnlyList<UsagePoint> Points => _points;

    /// <summary>Windows UsageChart.SetData (Program.cs L2236-L2242).</summary>
    public void SetData(IEnumerable<UsagePoint>? values, DateTime rangeStart, Color color)
    {
        _points = values?.ToList() ?? [];
        _visibleStart = rangeStart;
        _seriesColor = color;
        LastRender = null;
        LastRenderWasEmpty = false;
        InvalidateVisual();
    }

    /// <summary>Windows UsageChart.OnRender (Program.cs L2244-L2256).</summary>
    public override void Render(DrawingContext context)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(1, Bounds.Height);
        context.DrawRectangle(BackgroundBrush, null, new RoundedRect(new Rect(0, 0, width, height), 8));

        context.DrawLine(GridPen, new Point(Inset, Inset), new Point(width - Inset, Inset));
        context.DrawLine(GridPen, new Point(Inset, height / 2), new Point(width - Inset, height / 2));
        context.DrawLine(GridPen, new Point(Inset, height - Inset), new Point(width - Inset, height - Inset));

        var area = new Rect(Inset, Inset, width - 2 * Inset, height - 2 * Inset);
        DrawSeries(context, area);
        LastSeriesColor = _seriesColor;
    }

    /// <summary>Windows UsageChart.DrawSeries (Program.cs L2258-L2288).</summary>
    private void DrawSeries(DrawingContext context, Rect area)
    {
        if (_points.Count == 0 || area.Width <= 0 || area.Height <= 0)
        {
            LastRenderWasEmpty = true;
            LastRender = new MonitorChartRenderInfo(_points.Count, Bounds.Width, Bounds.Height,
                Math.Max(0, area.Width), Math.Max(0, area.Height), 0, 0, 0, true);
            return;
        }

        var start = _visibleStart;
        var end = DateTime.Now;
        if (end <= start) end = start.AddMinutes(1);
        double maximum = 0;
        foreach (var point in _points) maximum = Math.Max(maximum, point.Value);
        if (maximum <= 0) maximum = 1;

        var last = new Point(area.Left, area.Bottom);
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            for (var index = 0; index < _points.Count; index++)
            {
                var timeRatio = (_points[index].Timestamp - start).TotalSeconds / (end - start).TotalSeconds;
                timeRatio = Math.Max(0, Math.Min(1, timeRatio));
                var valueRatio = Math.Max(0, Math.Min(1, _points[index].Value / maximum));
                var plotted = new Point(area.Left + area.Width * timeRatio, area.Bottom - area.Height * valueRatio);
                if (index == 0) sink.BeginFigure(plotted, false);
                else sink.LineTo(plotted);
                last = plotted;
            }
        }

        var brush = new SolidColorBrush(Color.FromArgb(225, _seriesColor.R, _seriesColor.G, _seriesColor.B));
        context.DrawGeometry(null, new Pen(brush, 1.8), geometry);
        context.DrawEllipse(brush, null, last, 2.2, 2.2);

        LastRenderWasEmpty = false;
        LastRender = new MonitorChartRenderInfo(_points.Count, Bounds.Width, Bounds.Height,
            area.Width, area.Height, maximum, last.X, last.Y, false);
    }
}
