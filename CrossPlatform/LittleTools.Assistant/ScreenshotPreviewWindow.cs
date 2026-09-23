using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LittleTools.Assistant;

/// <summary>
/// Magnified viewer for a finished screenshot translation.
///
/// Windows basis: upstream has no equivalent. The shared assistant renders the
/// annotated image into the conversation with a fixed <c>MaxHeight = 520</c> and
/// <c>Stretch = Uniform</c> (MainWindow.AddScreenshotResult) and offers no click,
/// wheel or zoom interaction anywhere, so a small screenshot stays small and its
/// translation cannot be read. This window is a Linux side addition; the Windows
/// build of the same project inherits it only if this source is merged upstream.
///
/// Window conventions come from the existing suite dialogs
/// (Todo/TodoDialogs.cs TodoDialogWindow, Stock/StockDetailsWindow.cs): borderless,
/// rounded, frosted <see cref="GlassSurface"/> panel, not in the taskbar, closed by
/// Esc, by losing the focus and by its own close button.
/// </summary>
internal sealed class ScreenshotPreviewWindow : Window
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8.0;
    private const double WheelFactor = 1.15;
    /// <summary>Border padding (14 + 14) plus the 1px border on both sides.</summary>
    private const double HorizontalChrome = 30;
    /// <summary>The same horizontal chrome plus the header, toolbar and their spacing.</summary>
    private const double VerticalChrome = 104;
    private const double CloseOnDeactivateGraceMs = 350;

    private readonly Bitmap _bitmap;
    private readonly Image _image;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _zoomLabel;
    private readonly Button _close;
    private readonly double _fitZoom;
    private readonly DateTime _shownAt = DateTime.UtcNow;
    private double _zoom;
    private bool _fitting = true;
    private bool _dragging;
    private Point _dragStart;
    private Vector _dragOffset;

    internal ScreenshotPreviewWindow(string path, Window? owner)
    {
        using (var stream = File.OpenRead(path)) _bitmap = new Bitmap(stream);

        Title = "Little Tools · 译图预览";
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = GlassSurface.TransparencyLevels;
        TransparencyBackgroundFallback = Brushes.Transparent;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;

        var area = AvailableArea(owner);
        var imageWidth = Math.Max(1, _bitmap.Size.Width);
        var imageHeight = Math.Max(1, _bitmap.Size.Height);
        var availableWidth = Math.Max(160, area.Width - HorizontalChrome);
        var availableHeight = Math.Max(160, area.Height - VerticalChrome);
        _fitZoom = Math.Clamp(Math.Min(1, Math.Min(availableWidth / imageWidth, availableHeight / imageHeight)), MinZoom, 1);
        Width = Math.Clamp(imageWidth * _fitZoom + HorizontalChrome, 380, Math.Max(380, area.Width));
        Height = Math.Clamp(imageHeight * _fitZoom + VerticalChrome, 260, Math.Max(260, area.Height));

        _zoomLabel = new TextBlock
        {
            FontSize = 10.5,
            Foreground = Brush.Parse("#B8FFFFFF"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        _close = MakeButton("×", 27);
        _close.FontSize = 12;
        _close.Click += (_, _) => Close();

        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(new TextBlock
        {
            Text = "译图预览",
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_close, 1);
        title.Children.Add(_close);
        title.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(title).Properties.IsLeftButtonPressed) return;
            try { BeginMoveDrag(args); } catch { }
        };

        var fit = MakeButton("适应窗口", 66);
        fit.Click += (_, _) => FitToWindow();
        var actual = MakeButton("100%", 44);
        actual.Click += (_, _) => ZoomToActualSize();
        var zoomIn = MakeButton("＋", 27);
        zoomIn.Click += (_, _) => ZoomStep(inward: false);
        var zoomOut = MakeButton("－", 27);
        zoomOut.Click += (_, _) => ZoomStep(inward: true);
        var hint = new TextBlock
        {
            Text = "滚轮缩放 · 拖动平移 · Esc 关闭",
            FontSize = 9.5,
            Foreground = Brush.Parse("#8CFFFFFF"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(0, 8, 0, 8) };
        foreach (var (control, column) in new (Control, int)[] { (fit, 0), (actual, 1), (zoomOut, 2), (zoomIn, 3), (_zoomLabel, 4), (hint, 5) })
        {
            control.Margin = column == 0 ? new Thickness(0) : new Thickness(6, 0, 0, 0);
            Grid.SetColumn(control, column);
            toolbar.Children.Add(control);
        }

        _image = new Image
        {
            Source = _bitmap,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = ScreenshotPreview.HandCursor
        };
        _scroll = new ScrollViewer
        {
            Content = _image,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 120
        };
        var viewportHost = new Border
        {
            Child = _scroll,
            Background = Brush.Parse("#6611141B"),
            CornerRadius = new CornerRadius(9),
            ClipToBounds = true
        };
        _scroll.PointerWheelChanged += OnPointerWheel;
        _image.PointerPressed += OnPointerPressed;
        _image.PointerMoved += OnPointerMoved;
        _image.PointerReleased += OnPointerReleased;

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Grid.SetRow(title, 0);
        body.Children.Add(title);
        Grid.SetRow(toolbar, 1);
        body.Children.Add(toolbar);
        Grid.SetRow(viewportHost, 2);
        body.Children.Add(viewportHost);

        Content = new Border
        {
            Child = body,
            CornerRadius = new CornerRadius(15),
            Background = GlassSurface.Dialog(),
            BorderBrush = Brush.Parse("#3CFFFFFF"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, OffsetY = 5, Color = Color.FromArgb(71, 0, 0, 0) })
        };

        // The suite closes transient windows as soon as they lose the focus
        // (Todo DateChooserWindow, StockDetailsWindow). The grace period keeps a
        // window manager that maps the window before handing it the focus from
        // closing it immediately.
        Activated += (_, _) => DeactivateLog += $"activated@{(DateTime.UtcNow - _shownAt).TotalMilliseconds:0}ms ";
        Deactivated += (_, _) =>
        {
            DeactivatedEvents++;
            var elapsed = (DateTime.UtcNow - _shownAt).TotalMilliseconds;
            DeactivateLog += $"deactivated@{elapsed:0}ms(visible={IsVisible},active={IsActive}) ";
            if (!IsVisible) return;
            if (elapsed < CloseOnDeactivateGraceMs) return;
            DeactivateCloses++;
            ClosedByFocusLoss = true;
            Close();
            DeactivateLog += "closed ";
        };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            _image.Source = null;
            _bitmap.Dispose();
        };
        ApplyZoom(_fitZoom, fitting: true);
    }

    /// <summary>The current zoom factor, 1.0 meaning one bitmap pixel per DIP.</summary>
    internal double Zoom => _zoom;

    /// <summary>The factor the "适应窗口" button restores; 1.0 when the image already fits.</summary>
    internal double FitZoom => _fitZoom;

    /// <summary>True while the view is showing the fit factor.</summary>
    internal bool IsFitting => _fitting;

    /// <summary>How often the window manager reported a loss of focus (smoke diagnostics).</summary>
    internal int DeactivatedEvents { get; private set; }

    /// <summary>How often that loss of focus actually closed the viewer (smoke diagnostics).</summary>
    internal int DeactivateCloses { get; private set; }

    /// <summary>Timestamped trace of the focus transitions, for the smoke report.</summary>
    internal string DeactivateLog { get; private set; } = string.Empty;

    /// <summary>
    /// True when the viewer closed itself because it lost the focus, so the owner
    /// can tell "the user clicked away" (leave the assistant hidden) from "the user
    /// pressed Escape" (bring the assistant back).
    /// </summary>
    internal bool ClosedByFocusLoss { get; private set; }

    /// <summary>The current scroll offset, i.e. the pan position of a zoomed image.</summary>
    internal Vector PanOffset => _scroll.Offset;

    /// <summary>The scrollable content size, used by the smoke to report what can pan.</summary>
    internal Size ScrollExtent => _scroll.Extent;

    /// <summary>The visible image area, used by the smoke to report what can pan.</summary>
    internal Size ScrollViewport => _scroll.Viewport;

    internal Size PreviewSize => new(_bitmap.Size.Width, _bitmap.Size.Height);

    internal Button CloseButton => _close;

    /// <summary>Restores the initial "show at original size, scale down only if needed" view.</summary>
    internal void FitToWindow()
    {
        ApplyZoom(_fitZoom, fitting: true);
        _scroll.Offset = default;
    }

    internal void ZoomToActualSize()
    {
        ZoomAt(new Point(_scroll.Viewport.Width / 2, _scroll.Viewport.Height / 2), 1 / _zoom);
    }

    /// <summary>One wheel step, the same amount the pointer wheel asks for.</summary>
    internal void ZoomStep(bool inward)
    {
        ZoomAt(new Point(_scroll.Viewport.Width / 2, _scroll.Viewport.Height / 2), inward ? 1 / WheelFactor : WheelFactor);
    }

    /// <summary>Drags the image by a screen distance, used by the smoke and by the pointer.</summary>
    internal void DragBy(double dx, double dy)
    {
        SetOffset(_scroll.Offset.X - dx, _scroll.Offset.Y - dy);
    }

    internal void SaveRender(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        switch (args.Key)
        {
            case Key.Escape:
                args.Handled = true;
                Close();
                break;
            case Key.Add or Key.OemPlus:
                args.Handled = true;
                ZoomStep(inward: false);
                break;
            case Key.Subtract or Key.OemMinus:
                args.Handled = true;
                ZoomStep(inward: true);
                break;
            case Key.D0 or Key.NumPad0:
                args.Handled = true;
                ZoomToActualSize();
                break;
            case Key.F:
                args.Handled = true;
                FitToWindow();
                break;
            case Key.Left:
                args.Handled = true;
                DragBy(-24, 0);
                break;
            case Key.Right:
                args.Handled = true;
                DragBy(24, 0);
                break;
            case Key.Up:
                args.Handled = true;
                DragBy(0, -24);
                break;
            case Key.Down:
                args.Handled = true;
                DragBy(0, 24);
                break;
        }
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs args)
    {
        if (args.Delta.Y == 0) return;
        args.Handled = true;
        var point = args.GetPosition(_scroll);
        ZoomAt(point, args.Delta.Y > 0 ? WheelFactor : 1 / WheelFactor);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(_image).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _dragStart = args.GetPosition(_scroll);
        _dragOffset = _scroll.Offset;
        args.Pointer.Capture(_image);
        _image.Cursor = new Cursor(StandardCursorType.SizeAll);
        args.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs args)
    {
        if (!_dragging) return;
        var point = args.GetPosition(_scroll);
        SetOffset(_dragOffset.X - (point.X - _dragStart.X), _dragOffset.Y - (point.Y - _dragStart.Y));
        args.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (!_dragging) return;
        _dragging = false;
        args.Pointer.Capture(null);
        _image.Cursor = ScreenshotPreview.HandCursor;
        args.Handled = true;
    }

    /// <summary>
    /// Keeps the content point under <paramref name="anchor"/> fixed while the zoom
    /// changes, so the wheel magnifies what the user is pointing at.
    /// </summary>
    private void ZoomAt(Point anchor, double factor)
    {
        var before = _zoom;
        var viewport = _scroll.Viewport;
        var contentX = (anchor.X - Align(viewport.Width, _image.Width) + _scroll.Offset.X) / before;
        var contentY = (anchor.Y - Align(viewport.Height, _image.Height) + _scroll.Offset.Y) / before;
        ApplyZoom(before * factor, fitting: false);
        _scroll.UpdateLayout();
        SetOffset(
            Align(_scroll.Viewport.Width, _image.Width) + contentX * _zoom - anchor.X,
            Align(_scroll.Viewport.Height, _image.Height) + contentY * _zoom - anchor.Y);
    }

    private void ApplyZoom(double zoom, bool fitting)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _fitting = fitting;
        _image.Width = Math.Max(1, _bitmap.Size.Width * _zoom);
        _image.Height = Math.Max(1, _bitmap.Size.Height * _zoom);
        _zoomLabel.Text = (_zoom * 100).ToString("0.#") + "%";
    }

    private void SetOffset(double x, double y)
    {
        var maxX = Math.Max(0, _image.Width - _scroll.Viewport.Width);
        var maxY = Math.Max(0, _image.Height - _scroll.Viewport.Height);
        _scroll.Offset = new Vector(Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY));
    }

    private static double Align(double viewport, double content) => content >= viewport ? 0 : (viewport - content) / 2;

    private Size AvailableArea(Window? owner)
    {
        try
        {
            var screens = owner?.Screens ?? Screens;
            var screen = (owner is not null ? screens.ScreenFromWindow(owner) : null) ?? screens.Primary;
            if (screen is null) return new Size(1000, 700);
            var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
            return new Size(
                Math.Max(360, screen.WorkingArea.Width / scale - 60),
                Math.Max(260, screen.WorkingArea.Height / scale - 60));
        }
        catch
        {
            return new Size(1000, 700);
        }
    }

    private static Button MakeButton(string text, double width) => new()
    {
        Content = text,
        Width = width,
        MinWidth = width,
        Height = 25,
        FontSize = 10,
        VerticalAlignment = VerticalAlignment.Center
    };
}

/// <summary>
/// Wires a rendered translation image to the magnified viewer: hand cursor on
/// hover and one click to open. Kept beside the window so the shared
/// <c>MainWindow.axaml.cs</c> only needs the single call in
/// <c>AddScreenshotResult</c>.
/// </summary>
internal static class ScreenshotPreview
{
    /// <summary>The shared hand cursor, also used by the smoke to assert the wiring.</summary>
    internal static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    internal static Image MakePreviewable(Image image, string path, Action<Control, string> open)
    {
        image.Cursor = HandCursor;
        ToolTip.SetTip(image, "点击放大查看");
        image.Tapped += (_, args) =>
        {
            args.Handled = true;
            open(image, path);
        };
        return image;
    }

    /// <summary>
    /// Opens the viewer for <paramref name="path"/>, or returns null when the file
    /// is gone. The viewer loads its own copy of the bitmap, so the translation
    /// window may release <c>MainWindow._translationBitmaps</c> without leaving the
    /// preview pointing at a disposed image.
    /// </summary>
    internal static ScreenshotPreviewWindow? Show(Control anchor, string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var owner = TopLevel.GetTopLevel(anchor) as Window;
            var preview = new ScreenshotPreviewWindow(path, owner);
            if (owner is not null && owner.IsVisible) preview.Show(owner);
            else preview.Show();
            // The viewer owns the keyboard (Escape/zoom) and closes itself when it
            // loses the focus, so it must really take the focus; a topmost owner
            // keeps it otherwise.
            preview.Activate();
            return preview;
        }
        catch
        {
            return null;
        }
    }
}
