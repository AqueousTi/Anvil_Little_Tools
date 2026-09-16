using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

namespace LittleTools.Assistant.Platform;

internal sealed class SelectionWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection;
    private readonly Screen _screen;
    private readonly TaskCompletionSource<PixelRect?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Point? _start;
    private bool _completed;

    public SelectionWindow(Screen screen)
    {
        _screen = screen;
        _canvas.Background = Brushes.Transparent;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;
        Background = new SolidColorBrush(Color.FromArgb(105, 0, 0, 0));
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        _selection = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(92, 211, 255)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(38, 92, 211, 255)),
            IsVisible = false
        };
        _canvas.Children.Add(_selection);
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 15, 18, 24)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 7),
            Child = new TextBlock { Text = "拖动选择截图区域 · Esc 取消", Foreground = Brushes.White }
        };
        Canvas.SetLeft(hint, 24);
        Canvas.SetTop(hint, 24);
        _canvas.Children.Add(hint);
        Content = _canvas;

        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        KeyDown += OnKeyDown;
        Closed += (_, _) => Complete(null);
    }

    public Task<PixelRect?> SelectAsync()
    {
        Show();
        Activate();
        return _completion.Task;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        _start = args.GetPosition(_canvas);
        args.Pointer.Capture(this);
        _selection.IsVisible = true;
        UpdateSelection(_start.Value, _start.Value);
    }

    private void OnMoved(object? sender, PointerEventArgs args)
    {
        if (_start is null) return;
        UpdateSelection(_start.Value, args.GetPosition(_canvas));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (_start is null) return;
        var end = args.GetPosition(_canvas);
        args.Pointer.Capture(null);
        var left = Math.Min(_start.Value.X, end.X);
        var top = Math.Min(_start.Value.Y, end.Y);
        var width = Math.Abs(_start.Value.X - end.X);
        var height = Math.Abs(_start.Value.Y - end.Y);
        _start = null;
        if (width < 8 || height < 8)
        {
            _selection.IsVisible = false;
            return;
        }
        var scale = _screen.Scaling;
        var rectangle = new PixelRect(
            _screen.Bounds.X + (int)Math.Round(left * scale),
            _screen.Bounds.Y + (int)Math.Round(top * scale),
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
        Complete(rectangle);
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        Complete(null);
        Close();
    }

    private void UpdateSelection(Point first, Point second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        Canvas.SetLeft(_selection, left);
        Canvas.SetTop(_selection, top);
        _selection.Width = Math.Abs(first.X - second.X);
        _selection.Height = Math.Abs(first.Y - second.Y);
    }

    private void Complete(PixelRect? result)
    {
        if (_completed) return;
        _completed = true;
        _completion.TrySetResult(result);
    }
}
