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

    private MonitorDetailsWindow? _details;
    private bool _edgeHideEnabled;
    private int _hiddenEdge;
    private double _revealedLeft;
    private Point _logicalPosition;
    private bool _positioned;
    private bool _movingWindow;
    private bool _permanentlyClosing;
    private bool _refreshing;
    private DateTime _interactionAt = DateTime.MinValue;

    /// <summary>The detail window raised 退出; the suite host turns the module off.</summary>
    internal event Action? ExitRequested;

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
        // wide label column and a right aligned value.
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
                ColumnDefinitions = new ColumnDefinitions("12,79,*"),
                Background = Brushes.Transparent
            };
            row.Children.Add(MonitorTheme.Dot(colors[index]));
            var name = MonitorTheme.Label(labels[index], 10.5, MonitorTheme.RowLabel, bold: true);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var value = MonitorTheme.Label(initial[index], 12.5, MonitorTheme.PrimaryText, bold: true);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 2);
            row.Children.Add(value);
            Grid.SetRow(row, index);
            _compactGrid.Children.Add(row);
            _rows[index] = row;
            _values[index] = value;
        }

        _updatedText = MonitorTheme.Label("等待更新", 9.5, MonitorTheme.FooterText);
        _updatedText.HorizontalAlignment = HorizontalAlignment.Right;
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
            _shell.Background = MonitorTheme.ShellHover;
            _edgeHideTimer.Stop();
            RevealFromEdge();
        };
        _shell.PointerExited += (_, _) =>
        {
            _shell.Background = MonitorTheme.ShellBackground;
            if (_edgeHideEnabled && _hiddenEdge != 0 && !IsPointerOver && _details is not { IsVisible: true })
                _edgeHideTimer.Start();
        };
        _shell.PointerPressed += PrimaryPointerPressed;
        _edgeHideTimer.Tick += (_, _) =>
        {
            _edgeHideTimer.Stop();
            if (_edgeHideEnabled && _hiddenEdge != 0 && !IsPointerOver && _details is not { IsVisible: true })
                HideToEdge();
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
        };
        Deactivated += (_, _) =>
        {
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
            + ",refreshing=" + _refreshing
            + ",size=" + Width.ToString("0.#") + "x" + Height.ToString("0.#");
    }

    internal static string DescribeColor(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToString().ToUpperInvariant() : brush?.GetType().Name ?? "none";

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
            SnapOrHideAtEdge();
            PositionDetails();
            if (!moved) ToggleDetails();
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
        if (_hiddenEdge < 0) ApplyPosition(work.Left - Width + VisibleStrip, _logicalPosition.Y);
        else if (_hiddenEdge > 0) ApplyPosition(work.Right - VisibleStrip, _logicalPosition.Y);
    }
}
