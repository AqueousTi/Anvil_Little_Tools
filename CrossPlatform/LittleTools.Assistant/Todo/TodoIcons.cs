using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Small vector icons for the todo module. The Windows module drew these with
/// StreamGeometry; the same shapes are rebuilt here with Avalonia paths and a
/// custom rendered focus ring.
/// </summary>
internal static class TodoIcons
{
    public static Control Chevron(bool pointingLeft, IBrush? brush = null, double size = 12)
    {
        var path = new ShapePath
        {
            Data = Geometry.Parse(pointingLeft ? "M 9,1 L 3,7 L 9,13" : "M 3,1 L 9,7 L 3,13"),
            Stroke = brush ?? TodoTheme.SecondaryText,
            StrokeThickness = 1.8,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Fill = null,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        return path;
    }

    public static Control Minimize(IBrush? brush = null, double size = 12) => new ShapePath
    {
        // A horizontal line has no height, so uniform stretching would collapse it.
        Data = Geometry.Parse("M 0,0 L 12,0"),
        Stroke = brush ?? TodoTheme.SecondaryText,
        StrokeThickness = 1.8,
        StrokeLineCap = PenLineCap.Round,
        Fill = null,
        Width = size,
        Height = 2,
        Stretch = Stretch.None,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    public static Control Plus(IBrush? brush = null, double size = 14) => new ShapePath
    {
        Data = Geometry.Parse("M 7,1 L 7,13 M 1,7 L 13,7"),
        Stroke = brush ?? TodoTheme.PrimaryText,
        StrokeThickness = 1.8,
        StrokeLineCap = PenLineCap.Round,
        Fill = null,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform
    };

    public static Control Check(IBrush? brush = null, double size = 13) => new ShapePath
    {
        Data = Geometry.Parse("M 1,7 L 5,11 L 12,2"),
        Stroke = brush ?? TodoTheme.PrimaryText,
        StrokeThickness = 1.9,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Fill = null,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform
    };

    public static Control Cross(IBrush? brush = null, double size = 11) => new ShapePath
    {
        Data = Geometry.Parse("M 1,1 L 10,10 M 10,1 L 1,10"),
        Stroke = brush ?? TodoTheme.SecondaryText,
        StrokeThickness = 1.6,
        StrokeLineCap = PenLineCap.Round,
        Fill = null,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform
    };

    public static Control Trash(IBrush? brush = null, double size = 14) => new ShapePath
    {
        Data = Geometry.Parse("M 1,3 L 13,3 M 5,3 L 5,1 L 9,1 L 9,3 M 3,3 L 3.8,13 L 10.2,13 L 11,3 M 6,5.5 L 6,11 M 8,5.5 L 8,11"),
        Stroke = brush ?? TodoTheme.Danger,
        StrokeThickness = 1.4,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Fill = null,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform
    };

    /// <summary>
    /// The "move to backlog" / backlog tab icon: three stacked layers, ported from
    /// the Windows StackedItemsIcon.
    /// </summary>
    public static Control StackedItems(IBrush? brush = null, double size = 18) =>
        new StackedItemsIcon { Width = size, Height = size };

    /// <summary>Drag handle: two columns of dots.</summary>
    public static Control DragHandle(IBrush? brush = null, double size = 14)
    {
        var canvas = new Canvas { Width = size, Height = size };
        foreach (var (x, y) in new[] { (4.0, 3.0), (4.0, 7.0), (4.0, 11.0), (9.0, 3.0), (9.0, 7.0), (9.0, 11.0) })
        {
            var dot = new Ellipse
            {
                Width = 2.2,
                Height = 2.2,
                Fill = brush ?? TodoTheme.SecondaryText
            };
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, y);
            canvas.Children.Add(dot);
        }
        return canvas;
    }
}

/// <summary>
/// Three stacked layers, ported from the Windows StackedItemsIcon: the back layer
/// is faintest and the front layer solid.
/// </summary>
internal sealed class StackedItemsIcon : Control
{
    public static readonly StyledProperty<bool> InvertedProperty =
        AvaloniaProperty.Register<StackedItemsIcon, bool>(nameof(Inverted));

    static StackedItemsIcon()
    {
        AffectsRender<StackedItemsIcon>(InvertedProperty);
    }

    public bool Inverted
    {
        get => GetValue(InvertedProperty);
        set => SetValue(InvertedProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Math.Max(12, Bounds.Width);
        var centre = width / 2;
        var top = Math.Max(2, (Bounds.Height - 16) / 2);
        IBrush primary = Inverted ? new SolidColorBrush(Color.FromRgb(28, 31, 37)) : Brushes.White;
        IBrush secondary = Inverted
            ? new SolidColorBrush(Color.FromArgb(155, 28, 31, 37))
            : new SolidColorBrush(Color.FromArgb(150, 235, 238, 244));
        IBrush tertiary = Inverted
            ? new SolidColorBrush(Color.FromArgb(95, 28, 31, 37))
            : new SolidColorBrush(Color.FromArgb(85, 235, 238, 244));

        DrawLayer(context, centre, top + 8, width - 3, tertiary);
        DrawLayer(context, centre, top + 4, width - 3, secondary);
        DrawLayer(context, centre, top, width - 3, primary);
    }

    private static void DrawLayer(DrawingContext context, double centre, double y, double width, IBrush brush)
    {
        var half = width / 2;
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Point(centre, y), false);
            sink.LineTo(new Point(centre + half, y + 3.5));
            sink.LineTo(new Point(centre, y + 7));
            sink.LineTo(new Point(centre - half, y + 3.5));
            sink.EndFigure(true);
        }
        context.DrawGeometry(null, new Pen(brush, 1.45, lineJoin: PenLineJoin.Round), geometry);
    }
}

/// <summary>
/// The focus entry icon, ported from the Windows FocusRingIcon: a light ring with
/// an arc on top. Idle shows three quarters in red, a running countdown turns the
/// arc blue, and the last minute turns it red again.
/// </summary>
internal sealed class FocusRingIcon : Control
{
    public static readonly StyledProperty<bool> ActiveProperty =
        AvaloniaProperty.Register<FocusRingIcon, bool>(nameof(Active));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<FocusRingIcon, double>(nameof(Progress));

    public static readonly StyledProperty<bool> UrgentProperty =
        AvaloniaProperty.Register<FocusRingIcon, bool>(nameof(Urgent));

    private static readonly IBrush White = new SolidColorBrush(Color.FromArgb(238, 248, 248, 246));
    private static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(231, 70, 63));
    private static readonly IBrush Blue = new SolidColorBrush(Color.FromRgb(76, 145, 255));

    static FocusRingIcon()
    {
        AffectsRender<FocusRingIcon>(ActiveProperty, ProgressProperty, UrgentProperty);
    }

    public bool Active
    {
        get => GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool Urgent
    {
        get => GetValue(UrgentProperty);
        set => SetValue(UrgentProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Max(8, Math.Min(Bounds.Width, Bounds.Height));
        if (size <= 2) return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var thickness = Math.Max(2.4, size * 0.16);
        var radius = size / 2 - thickness / 2 - 1;

        context.DrawEllipse(null, new Pen(White, thickness), centre, radius, radius);
        var shown = Active ? Math.Max(0.015, Math.Min(1, Progress)) : 0.75;
        var pen = new Pen(Active && !Urgent ? Blue : Red, thickness, lineCap: PenLineCap.Round);
        if (shown >= 0.999)
            context.DrawEllipse(null, pen, centre, radius, radius);
        else
            DrawArc(context, centre, radius, -90, shown * 360, pen);
    }

    internal static void DrawArc(DrawingContext context, Point centre, double radius,
        double startDegrees, double sweepDegrees, Pen pen)
    {
        if (sweepDegrees <= 0.01) return;
        var startRadians = startDegrees * Math.PI / 180.0;
        var endRadians = (startDegrees + sweepDegrees) * Math.PI / 180.0;
        var start = new Point(centre.X + Math.Cos(startRadians) * radius, centre.Y + Math.Sin(startRadians) * radius);
        var end = new Point(centre.X + Math.Cos(endRadians) * radius, centre.Y + Math.Sin(endRadians) * radius);
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(start, false);
            sink.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise);
            sink.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
}
