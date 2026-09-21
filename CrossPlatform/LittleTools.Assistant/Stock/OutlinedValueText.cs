using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// The premium value label. Windows drew it as <c>FormattedText</c> geometry with
/// an optional coloured outline (StockMonitor/StockWindow.cs L19-L55); the same
/// geometry call is used here, so a value below the alert threshold keeps its
/// white core and gradient edge.
/// </summary>
internal sealed class OutlinedValueText : ContentControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<OutlinedValueText, string?>(nameof(Text), "--");

    public static readonly StyledProperty<bool> OutlineEnabledProperty =
        AvaloniaProperty.Register<OutlinedValueText, bool>(nameof(OutlineEnabled));

    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<OutlinedValueText, IBrush?>(nameof(OutlineBrush));

    public static readonly StyledProperty<double> OutlineThicknessProperty =
        AvaloniaProperty.Register<OutlinedValueText, double>(nameof(OutlineThickness));

    static OutlinedValueText()
    {
        AffectsMeasure<OutlinedValueText>(TextProperty, FontSizeProperty, FontFamilyProperty, FontWeightProperty,
            HorizontalContentAlignmentProperty);
        AffectsRender<OutlinedValueText>(TextProperty, OutlineEnabledProperty, OutlineBrushProperty,
            OutlineThicknessProperty, ForegroundProperty, HorizontalContentAlignmentProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool OutlineEnabled
    {
        get => GetValue(OutlineEnabledProperty);
        set => SetValue(OutlineEnabledProperty, value);
    }

    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public double OutlineThickness
    {
        get => GetValue(OutlineThicknessProperty);
        set => SetValue(OutlineThicknessProperty, value);
    }

    /// <summary>Windows MeasureOverride: text plus a 5x4 margin.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var text = CreateText();
        return new Size(text.Width + 5, text.Height + 4);
    }

    public override void Render(DrawingContext context)
    {
        var text = CreateText();
        var x = HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Right
            ? Math.Max(2, Bounds.Width - text.Width - 2)
            : HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Center
                ? Math.Max(2, (Bounds.Width - text.Width) / 2)
                : 2;
        var y = Math.Max(1, (Bounds.Height - text.Height) / 2);
        var geometry = text.BuildGeometry(new Point(x, y));
        if (geometry is null) return;
        Pen? outline = null;
        if (OutlineEnabled && OutlineBrush is not null)
            outline = new Pen(OutlineBrush, Math.Max(0.5, OutlineThickness)) { LineJoin = PenLineJoin.Round };
        context.DrawGeometry(Foreground ?? Brushes.White, outline, geometry);
    }

    private FormattedText CreateText() => new(
        Text ?? string.Empty,
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
        FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyle, FontWeight),
        FontSize,
        Foreground ?? Brushes.White);
}
