using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using System.Runtime.InteropServices;

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
        // Showing on another monitor can change the window's actual DPI.
        Position = _screen.Bounds.Position;
        Width = _screen.Bounds.Width / RenderScaling;
        Height = _screen.Bounds.Height / RenderScaling;
        if (OperatingSystem.IsWindows())
        {
            var bounds = _screen.Bounds;
            if (!SetWindowPos(TryGetPlatformHandle()!.Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010))
            {
                var error = Marshal.GetLastWin32Error();
                Close();
                throw new System.ComponentModel.Win32Exception(error, "无法定位截图框选窗口。");
            }
        }
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
        // Use the actual canvas client origin and current window DPI, rather
        // than assuming the overlay's origin/scale match the requested screen.
        var rectangle = ClipSelection(
            _canvas.PointToScreen(new Point(left, top)),
            _canvas.PointToScreen(new Point(left + width, top + height)), _screen.Bounds);
        if (rectangle.Width < 1 || rectangle.Height < 1) return;
        Complete(rectangle);
        Close();
    }

    internal static PixelRect ClipSelection(PixelPoint first, PixelPoint second, PixelRect bounds)
    {
        var left = Math.Clamp(Math.Min(first.X, second.X), bounds.X, bounds.Right);
        var top = Math.Clamp(Math.Min(first.Y, second.Y), bounds.Y, bounds.Bottom);
        var right = Math.Clamp(Math.Max(first.X, second.X), bounds.X, bounds.Right);
        var bottom = Math.Clamp(Math.Max(first.Y, second.Y), bounds.Y, bounds.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    internal async Task VerifyScreenCaptureAsync()
    {
        _canvas.Children.Clear();
        Background = new SolidColorBrush(Color.FromRgb(37, 113, 179));
        _ = SelectAsync();
        try
        {
            await Task.Delay(250);
            UpdateLayout();
            var origin = _canvas.PointToScreen(new Point(0, 0));
            var corner = _canvas.PointToScreen(new Point(_canvas.Bounds.Width, _canvas.Bounds.Height));
            if (Math.Abs(origin.X - _screen.Bounds.X) > 2 || Math.Abs(origin.Y - _screen.Bounds.Y) > 2
                || Math.Abs(corner.X - _screen.Bounds.Right) > 2 || Math.Abs(corner.Y - _screen.Bounds.Bottom) > 2)
                throw new InvalidOperationException($"Screen coordinate mismatch: {_screen.Bounds}; actual {origin} to {corner}, DPI {RenderScaling}.");
            if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            {
                var center = _canvas.PointToScreen(new Point(_canvas.Bounds.Width / 2, _canvas.Bounds.Height / 2));
                using var bytes = new MemoryStream(WindowsScreenshotService.CapturePixels(new PixelRect(center.X, center.Y, 8, 8)));
                using var bitmap = new System.Drawing.Bitmap(bytes);
                var pixel = bitmap.GetPixel(4, 4);
                if (Math.Abs(pixel.R - 37) > 5 || Math.Abs(pixel.G - 113) > 5 || Math.Abs(pixel.B - 179) > 5)
                    throw new InvalidOperationException($"Screen capture selected wrong pixels on {_screen.Bounds}.");
            }
        }
        finally { Close(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

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
