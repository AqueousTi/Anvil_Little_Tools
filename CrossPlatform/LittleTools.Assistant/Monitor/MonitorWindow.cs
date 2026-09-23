using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// The 316x92 always-on-top AI usage HUD, ported from the Windows
/// <c>MonitorWindow</c> (AIUsageMonitor/Program.cs L1434-L1849): three provider
/// rows with a coloured dot, a right aligned value and a footer with the last
/// update time, a click that expands the detail window, direct dragging, and the
/// 28 pixel edge snap that leaves a 9 pixel strip.
///
/// Not ported: the Windows <c>WS_EX_TRANSPARENT</c> click-through (Program.cs
/// L1830-L1837). Avalonia has no input-shape API, and the suite tray has no per
/// module submenu, so a click-through HUD could not be turned off again from the
/// tray; see the porting notes.
/// </summary>
internal sealed class MonitorWindow : Window
{
    private const double SnapDistance = 28;
    private const double VisibleStrip = 9;

    private readonly MonitorModule _module;
    private readonly Border _shell;
    private readonly Grid _compactGrid;
    private readonly Grid[] _rows;
    private readonly TextBlock[] _values;
    private readonly TextBlock _updatedText;
    private readonly DispatcherTimer _edgeHideTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };

    /// <summary>
    /// Fires once a user drag has stopped moving the window. X11 runs an interactive
    /// move in the window manager, so <c>BeginMoveDrag</c> returns immediately and the
    /// final position arrives later through PositionChanged; snapping right after the
    /// call therefore snapped to an intermediate position and left the HUD un-snapped
    /// when the user released near the edge. Waiting for the movement to settle is what
    /// makes the Windows SnapOrHideAtEdge behaviour observable (verified with a real
    /// XTest drag: the HUD collapses to the nine pixel strip at the screen edge).
    ///
    /// Only an armed drag (a press on the HUD) starts it, so the initial top right
    /// placement - which sits 18 pixels from the edge, inside the 28 pixel snap
    /// distance - is not snapped on load, exactly like Windows, which snaps only after
    /// a drag.
    /// </summary>
    private readonly DispatcherTimer _moveSettleTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private MonitorDetailsWindow? _details;
    private bool _edgeHideEnabled;
    private int _hiddenEdge;
    private double _revealedLeft;
    private Point _logicalPosition;
    private bool _positioned;
    private bool _movingWindow;
    private bool _dragArmed;
    private bool _permanentlyClosing;
    private bool _refreshing;
    private DateTime _interactionAt = DateTime.MinValue;

    /// <summary>The detail window raised 退出; the suite host turns the module off.</summary>
    internal event Action? ExitRequested;

    /// <summary>
    /// Windows closes the detail window as soon as the HUD loses focus and the
    /// pointer is away from both windows (Program.cs L1725-L1733). Kept for
    /// production; the render smoke turns it off, because a shared X display can
    /// take the focus at any moment and an auto-closed window cannot be rendered.
    /// </summary>
    internal bool AutoCloseDetailsOnDeactivate { get; set; } = true;

    public MonitorWindow(MonitorModule module, bool edgeHideEnabled)
    {
        _module = module;
        _edgeHideEnabled = edgeHideEnabled;

        Title = "Little Tools · AI 余量监控";
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = GlassSurface.TransparencyLevels;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = MonitorLayout.CompactWidth;
        Height = MonitorLayout.CompactHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Windows MonitorWindow.CreateRow (Program.cs L1551-L1591): a 7x7 dot, a 79
        // wide label column and a right aligned value. The label column is widened to
        // MonitorLayout.CompactLabelColumn because the Linux font stack draws
        // "DEEPSEEK" wider than Segoe UI Semibold does (see MonitorLayout).
        _compactGrid = new Grid();
        for (var index = 0; index < 3; index++)
            _compactGrid.RowDefinitions.Add(new RowDefinition(new GridLength(MonitorLayout.BaseRowHeight)));
        _compactGrid.RowDefinitions.Add(new RowDefinition(new GridLength(MonitorLayout.BaseFooterHeight)));

        _rows = new Grid[3];
        _values = new TextBlock[3];
        var colors = new[] { MonitorTheme.CodexColor, MonitorTheme.DeepSeekColor, MonitorTheme.GlmColor };
        var labels = new[] { MonitorText.CodexLabel, MonitorText.DeepSeekLabel, MonitorText.GlmLabel };
        var initial = new[] { "正在连接", "正在连接", "未启用" };
        for (var index = 0; index < 3; index++)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(
                    "12," + MonitorLayout.CompactLabelColumn.ToString("0",
                        System.Globalization.CultureInfo.InvariantCulture) + ",*"),
                Background = Brushes.Transparent
            };
            row.Children.Add(MonitorTheme.Dot(colors[index]));
            var name = MonitorTheme.Label(labels[index], 10.5, MonitorTheme.RowLabel, bold: true);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var value = MonitorTheme.Label(initial[index], 12.5, MonitorTheme.PrimaryText, bold: true);
            // The value must fill its cell and align its text to the right rather than
            // being arranged at its own desired width: Avalonia reports a stale, far
            // too small desired width for a right aligned TextBlock here, which placed
            // the box at the right edge and cut the glyphs off mid-character. Stretch
            // plus TextAlignment.Right is layout independent, and a value that still
            // does not fit (the GLM row is wider than the HUD in any font) is
            // ellipsised instead of being cut, which is what the Windows HUD does with
            // its 79+195 pixel columns as well.
            value.HorizontalAlignment = HorizontalAlignment.Stretch;
            value.TextAlignment = TextAlignment.Right;
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            value.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            Grid.SetRow(row, index);
            _compactGrid.Children.Add(row);
            _rows[index] = row;
            _values[index] = value;
        }

        _updatedText = MonitorTheme.Label("等待更新", 9.5, MonitorTheme.FooterText);
        // Same layout rule as the value cells: fill the row and align the text right
        // instead of being arranged at a desired width Avalonia reports too small.
        _updatedText.HorizontalAlignment = HorizontalAlignment.Stretch;
        _updatedText.TextAlignment = TextAlignment.Right;
        _updatedText.TextTrimming = TextTrimming.CharacterEllipsis;
        _updatedText.TextWrapping = TextWrapping.NoWrap;
        Grid.SetRow(_updatedText, 3);
        _compactGrid.Children.Add(_updatedText);

        _shell = new Border
        {
            CornerRadius = new CornerRadius(MonitorLayout.CompactCornerRadius),
            Background = MonitorTheme.ShellBackground,
            BorderBrush = MonitorTheme.ShellBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 9),
            BoxShadow = MonitorTheme.ShellShadow,
            Child = _compactGrid
        };
        Content = _shell;

        _shell.PointerEntered += (_, _) =>
        {
            Debug("pointer entered");
            _shell.Background = MonitorTheme.ShellHover;
            _edgeHideTimer.Stop();
            RevealFromEdge();
        };
        _shell.PointerExited += (_, _) =>
        {
            Debug("pointer exited hidden=" + _hiddenEdge);
            _shell.Background = MonitorTheme.ShellBackground;
            // IsPointerOver is still stale (true) while PointerExited is delivered, so
            // it must not gate the timer here: doing so meant a HUD that had been
            // revealed by hovering never collapsed again after the pointer left. The
            // tick re-checks it 550 ms later, which is the value that matters.
            if (_edgeHideEnabled && _hiddenEdge != 0 && _details is not { IsVisible: true })
                _edgeHideTimer.Start();
        };
        _shell.PointerPressed += PrimaryPointerPressed;
        _edgeHideTimer.Tick += (_, _) =>
        {
            Debug("edgeHide tick pointerOver=" + IsPointerOver + " hidden=" + _hiddenEdge
                + " details=" + IsDetailsOpen);
            _edgeHideTimer.Stop();
            if (_edgeHideEnabled && _hiddenEdge != 0 && !IsPointerOver && _details is not { IsVisible: true })
                HideToEdge();
        };
        _moveSettleTimer.Tick += (_, _) =>
        {
            _moveSettleTimer.Stop();
            _dragArmed = false;
            SnapOrHideAtEdge();
        };

        // X11 completes a move asynchronously; the logical position is tracked from
        // the platform notification, like the stock capsule.
        PositionChanged += (_, args) =>
        {
            var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
            var logical = new Point(args.Point.X / scaling, args.Point.Y / scaling);
            if (Math.Abs(logical.X - _logicalPosition.X) < 1 && Math.Abs(logical.Y - _logicalPosition.Y) < 1) return;
            _logicalPosition = logical;
            PositionDetails();
            if (!_dragArmed) return;
            _moveSettleTimer.Stop();
            _moveSettleTimer.Start();
        };
        Deactivated += (_, _) =>
        {
            if (!AutoCloseDetailsOnDeactivate) return;
            if (_details is not { IsVisible: true } details) return;
            if (_movingWindow || (DateTime.UtcNow - _interactionAt).TotalMilliseconds < 600) return;
            if (IsPointerOver || details.IsPointerOver) return;
            CloseDetails();
        };
        Closing += (_, args) =>
        {
            if (_permanentlyClosing) return;
            args.Cancel = true;
            CloseDetails();
            Hide();
        };
        Closed += (_, _) =>
        {
            _edgeHideTimer.Stop();
            _moveSettleTimer.Stop();
            CloseDetails();
        };

        UpdateView(module.State);
    }

    internal MonitorDetailsWindow? DetailsForSmoke => _details;

    internal bool IsDetailsOpen => _details is { IsVisible: true };

    internal Point LogicalPosition => _logicalPosition;

    internal int HiddenEdge => _hiddenEdge;

    internal double ShellCornerRadius => _shell.CornerRadius.TopLeft;

    /// <summary>
    /// A one line snapshot of the HUD for the render smoke, so the row texts, the
    /// tone brushes, the collapsed rows and the footer can be asserted from the run
    /// rather than only from the composed strings in the unit tests.
    /// </summary>
    internal string DescribeCompactForSmoke()
    {
        var parts = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            parts.Add(_values[index].Text + "/" + DescribeColor(_values[index].Foreground)
                + "/h=" + _compactGrid.RowDefinitions[index].Height.Value.ToString("0.#",
                    System.Globalization.CultureInfo.InvariantCulture)
                + "/vis=" + _rows[index].IsVisible);
        }
        return "codex=[" + parts[0] + "],deepseek=[" + parts[1] + "],glm=[" + parts[2] + "]"
            + ",footer=[" + _updatedText.Text + "],footerH="
            + _compactGrid.RowDefinitions[3].Height.Value.ToString("0.#",
                System.Globalization.CultureInfo.InvariantCulture)
            + ",glmTip=" + (ToolTip.GetTip(_values[2]) as string ?? "none")
            + ",refreshing=" + _refreshing
            + ",size=" + Width.ToString("0.#") + "x" + Height.ToString("0.#");
    }

    /// <summary>
    /// The ARGB hex of a brush. <c>Color.ToString()</c> cannot be used: it returns a
    /// colour <i>name</i> for the handful of known colours ("White"), which would
    /// make the smoke expectations depend on which colours happen to be named.
    /// </summary>
    /// <summary>Temporary/one env var gated diagnostic; prints to the app log.</summary>
    private static void Debug(string message)
    {
        if (Environment.GetEnvironmentVariable("LITTLETOOLS_MONITOR_DEBUG") is not null)
            Console.WriteLine("[monitor] " + message);
    }

    internal static string DescribeColor(IBrush? brush) =>
        brush is ISolidColorBrush solid
            ? "#" + solid.Color.A.ToString("X2") + solid.Color.R.ToString("X2")
                + solid.Color.G.ToString("X2") + solid.Color.B.ToString("X2")
            : brush?.GetType().Name ?? "none";

    // -------------------------------------------------------------- rendering

    /// <summary>Windows MonitorWindow.UpdateView (Program.cs L1593-L1653).</summary>
    internal void UpdateView(UsageSnapshot state)
    {
        SetCompactProviderVisibility(state);

        var codex = MonitorText.CodexCompact(state);
        _values[0].Text = codex.Text;
        _values[0].Foreground = MonitorTheme.ToneBrush(codex.Tone);

        var deepSeek = MonitorText.DeepSeekCompact(state);
        _values[1].Text = deepSeek.Text;
        _values[1].Foreground = MonitorTheme.ToneBrush(deepSeek.Tone);

        var glm = MonitorText.GlmCompact(state);
        _values[2].Text = glm.Text;
        _values[2].Foreground = MonitorTheme.ToneBrush(glm.Tone);

        UpdateFooter();
        // The GLM row is wider than the HUD in any font, so it is ellipsised; the
        // tooltip keeps the full line reachable.
        for (var index = 0; index < 3; index++) ToolTip.SetTip(_values[index], _values[index].Text);
        ToolTip.SetTip(_updatedText, _updatedText.Text);
        // A right aligned TextBlock is arranged at its measured width, and the first
        // measurement can be taken before the font fallback for the mixed
        // Latin/CJK stack has resolved. Without this the value keeps the stale
        // width and the glyphs are cut off at the window edge.
        for (var index = 0; index < 3; index++) _values[index].InvalidateMeasure();
        _updatedText.InvalidateMeasure();
        _details?.UpdateView(state);
    }

    /// <summary>Windows SetCompactProviderVisibility (Program.cs L1655-L1668).</summary>
    private void SetCompactProviderVisibility(UsageSnapshot state)
    {
        var enabled = new[] { state.CodexEnabled, state.DeepSeekEnabled, state.GlmEnabled };
        var count = enabled.Count(value => value);
        var rowHeight = MonitorLayout.CompactRowHeight(count);
        for (var index = 0; index < 3; index++)
        {
            _compactGrid.RowDefinitions[index].Height = new GridLength(enabled[index] ? rowHeight : 0);
            _rows[index].IsVisible = enabled[index];
        }
        _compactGrid.RowDefinitions[3].Height = new GridLength(MonitorLayout.CompactFooterHeight(count));
    }

    /// <summary>Windows SetRefreshing/UpdateFooter (Program.cs L1670-L1682).</summary>
    internal void SetRefreshing(bool value)
    {
        _refreshing = value;
        UpdateFooter();
    }

    private void UpdateFooter() => _updatedText.Text = MonitorText.CompactFooter(_module.State, _refreshing);

    // ------------------------------------------------------------- behaviour

    /// <summary>Windows PrimaryMouseLeftButtonDown (Program.cs L1536-L1548).</summary>
    private void PrimaryPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(_shell).Properties.IsLeftButtonPressed) return;
        _interactionAt = DateTime.UtcNow;
        RevealFromEdge();
        var pressPosition = _logicalPosition;
        _dragArmed = true;
        _movingWindow = true;
        try
        {
            BeginMoveDrag(args);
        }
        catch
        {
            // A window manager may refuse the move; treating it as a click is fine.
        }
        finally
        {
            _movingWindow = false;
        }
        DispatcherTimer.RunOnce(() =>
        {
            var moved = Math.Abs(_logicalPosition.X - pressPosition.X) > 1
                || Math.Abs(_logicalPosition.Y - pressPosition.Y) > 1;
            PositionDetails();
            if (moved) return;
            // A press without movement is a click: the drag is over and the detail view
            // toggles, exactly like the Windows mouse handler.
            _dragArmed = false;
            ToggleDetails();
        }, TimeSpan.FromMilliseconds(180));
        args.Handled = true;
    }

    /// <summary>Windows ToggleDetails (Program.cs L1710-L1755).</summary>
    internal void ToggleDetails()
    {
        if (_details is { IsVisible: true })
        {
            CloseDetails();
            return;
        }

        var details = new MonitorDetailsWindow(this, _module);
        _details = details;
        _edgeHideTimer.Stop();
        RevealFromEdge();
        details.RefreshRequested += async () => await _module.RefreshNowAsync();
        details.SettingsRequested += async () => await _module.OpenProviderSettingsAsync(this);
        details.ExitRequested += () => ExitRequested?.Invoke();
        details.Deactivated += (_, _) =>
        {
            if (!ReferenceEquals(_details, details) || !details.IsVisible) return;
            if (_movingWindow || details.IsMoving || (DateTime.UtcNow - _interactionAt).TotalMilliseconds < 600) return;
            if (IsPointerOver || details.IsPointerOver) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_details, details) && !IsPointerOver && !details.IsPointerOver) CloseDetails();
            }, DispatcherPriority.Background);
        };
        details.Closed += (_, _) =>
        {
            if (ReferenceEquals(_details, details)) _details = null;
        };
        PositionDetails();
        details.UpdateView(_module.State);
        details.Show(this);
        Stock.StockWindow.ReapplyWindowFlags(details, true);
    }

    internal void CloseDetails()
    {
        var current = _details;
        _details = null;
        if (current is not null)
        {
            try { current.Close(); } catch { }
        }
        if (_edgeHideEnabled && _hiddenEdge != 0 && !IsPointerOver) _edgeHideTimer.Start();
    }

    /// <summary>Windows PositionDetails (Program.cs L1757-L1765).</summary>
    private void PositionDetails()
    {
        if (_details is not { } details) return;
        var work = WorkingArea();
        var left = _logicalPosition.X + Width - details.Width;
        var top = _logicalPosition.Y + Height + 8;
        details.ApplyPosition(
            Math.Max(work.Left, Math.Min(work.Right - details.Width, left)),
            Math.Max(work.Top, Math.Min(work.Bottom - details.Height, top)));
    }

    /// <summary>Windows PositionAtTopRight (Program.cs L1767-L1772).</summary>
    private void PositionDefault()
    {
        var work = WorkingArea();
        ApplyPosition(Math.Max(work.Left, work.Right - Width - 18), work.Top + 18);
        _positioned = true;
    }

    internal void ShowWindow()
    {
        if (!IsVisible) Show();
        if (!_positioned) PositionDefault();
        Activate();
        Topmost = true;
        Stock.StockWindow.ReapplyWindowFlags(this, true);
    }

    /// <summary>Windows ToggleWindow (Program.cs L304-L313).</summary>
    internal void ToggleVisibility()
    {
        if (IsVisible)
        {
            CloseDetails();
            Hide();
            return;
        }
        ShowWindow();
    }

    internal void ClosePermanently()
    {
        _permanentlyClosing = true;
        CloseDetails();
        try { Close(); } catch { }
    }

    internal void EnsurePositioned()
    {
        if (!_positioned) PositionDefault();
    }

    public void SetEdgeHideEnabled(bool enabled)
    {
        _edgeHideEnabled = enabled;
        _edgeHideTimer.Stop();
        if (enabled) return;
        RevealFromEdge();
        _hiddenEdge = 0;
    }

    // ------------------------------------------------------------ positioning

    internal void ApplyPosition(double x, double y)
    {
        _logicalPosition = new Point(x, y);
        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        try
        {
            Position = new PixelPoint((int)Math.Round(x * scaling), (int)Math.Round(y * scaling));
        }
        catch
        {
            // Wayland ignores client positioning; the compositor decides instead.
        }
    }

    internal Rect WorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return new Rect(0, 0, 1920, 1080);
        var area = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        return new Rect(area.X / scaling, area.Y / scaling, area.Width / scaling, area.Height / scaling);
    }

    /// <summary>Windows SnapOrHideAtEdge (Program.cs L1774-L1804).</summary>
    internal void SnapOrHideAtEdge()
    {
        var work = WorkingArea();
        Debug("snap x=" + _logicalPosition.X + " work=" + work + " scaling=" + RenderScaling
            + " edgeHide=" + _edgeHideEnabled + " hidden=" + _hiddenEdge + " details=" + IsDetailsOpen);
        var x = _logicalPosition.X;
        var y = Math.Max(work.Top, Math.Min(_logicalPosition.Y, work.Bottom - Height));

        if (!_edgeHideEnabled)
        {
            _hiddenEdge = 0;
            ApplyPosition(Math.Max(work.Left, Math.Min(x, work.Right - Width)), y);
            return;
        }

        if (x <= work.Left + SnapDistance)
        {
            _hiddenEdge = -1;
            _revealedLeft = work.Left;
            if (IsDetailsOpen) ApplyPosition(_revealedLeft, y);
            else HideToEdge();
        }
        else if (x + Width >= work.Right - SnapDistance)
        {
            _hiddenEdge = 1;
            _revealedLeft = work.Right - Width;
            if (IsDetailsOpen) ApplyPosition(_revealedLeft, y);
            else HideToEdge();
        }
        else
        {
            _hiddenEdge = 0;
            ApplyPosition(Math.Max(work.Left, Math.Min(x, work.Right - Width)), y);
        }
        PositionDetails();
    }

    /// <summary>Windows RevealFromEdge (Program.cs L1806-L1810).</summary>
    internal void RevealFromEdge()
    {
        if (_hiddenEdge != 0) ApplyPosition(_revealedLeft, _logicalPosition.Y);
    }

    /// <summary>Windows HideToEdge (Program.cs L1812-L1821): a nine pixel strip stays visible.</summary>
    private void HideToEdge()
    {
        if (!_edgeHideEnabled || IsDetailsOpen) return;
        var work = WorkingArea();
        Debug("hideToEdge hidden=" + _hiddenEdge + " work=" + work);
        if (_hiddenEdge < 0) ApplyPosition(work.Left - Width + VisibleStrip, _logicalPosition.Y);
        else if (_hiddenEdge > 0) ApplyPosition(work.Right - VisibleStrip, _logicalPosition.Y);
    }
}
