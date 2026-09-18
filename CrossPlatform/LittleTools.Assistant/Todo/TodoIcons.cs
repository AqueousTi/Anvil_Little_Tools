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

    /// <summary>The four corner marks used for the "move to backlog" button.</summary>
    public static Control StackedItems(IBrush? brush = null, double size = 13) => new ShapePath
    {
        Data = Geometry.Parse("M 2,2 L 7,2 M 2,2 L 2,7 M 11,2 L 6,2 M 11,2 L 11,7 M 2,11 L 7,11 M 2,11 L 2,6 M 11,11 L 6,11 M 11,11 L 11,6"),
        Stroke = brush ?? TodoTheme.SecondaryText,
        StrokeThickness = 1.5,
        StrokeLineCap = PenLineCap.Round,
        Fill = null,
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform
    };

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
/// The circular focus indicator. Draws a ring that fills with the remaining time
/// and turns red in the final minute, like the Windows <c>FocusRingIcon</c>.
/// </summary>
internal sealed class FocusRingIcon : Control
{
    public static readonly StyledProperty<bool> ActiveProperty =
        AvaloniaProperty.Register<FocusRingIcon, bool>(nameof(Active));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<FocusRingIcon, double>(nameof(Progress));

    public static readonly StyledProperty<bool> UrgentProperty =
        AvaloniaProperty.Register<FocusRingIcon, bool>(nameof(Urgent));

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
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 2) return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 2;
        var brush = Urgent ? TodoTheme.Danger : Active ? TodoTheme.Accent : TodoTheme.SecondaryText;

        context.DrawEllipse(null, new Pen(brush, 1.6, dashStyle: null), centre, radius, radius);
        if (Active && Progress > 0)
        {
            var sweep = Math.Clamp(Progress, 0, 1) * Math.PI * 2;
            var geometry = new StreamGeometry();
            using (var sink = geometry.Open())
            {
                var start = new Point(centre.X, centre.Y - radius);
                sink.BeginFigure(start, false);
                sink.ArcTo(
                    new Point(centre.X + radius * Math.Sin(sweep), centre.Y - radius * Math.Cos(sweep)),
                    new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
                sink.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(brush, 2.6, lineCap: PenLineCap.Round), geometry);
        }
        // A small dot in the middle mirrors the Windows dial button.
        context.DrawEllipse(brush, null, centre, 1.8, 1.8);
    }
}
