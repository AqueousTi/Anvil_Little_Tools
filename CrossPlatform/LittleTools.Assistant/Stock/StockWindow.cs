using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// The stock monitor capsule. Windows kept the 316x92 frame and the whole detail
/// view in one <c>StockWindow</c> (StockMonitor/StockWindow.cs L146-L982); the
/// Avalonia port keeps the same frame, drag, edge hide and refresh behaviour and
/// moves the detail view into <see cref="StockDetailsWindow"/>, which is a second
/// top level window there as well.
/// </summary>
internal sealed class StockWindow : Window
{
    private readonly StockStore _store;
    private readonly StockDataService _service;
    private readonly StockSettings _settings;
    private readonly bool _ownsService;
    private readonly Dictionary<string, StockQuote> _quotes = new(StringComparer.Ordinal);
    private readonly StockAlertTracker _alerts = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly DispatcherTimer _edgeHideTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };

    private readonly Border _shell;
    private readonly TextBlock _compactSymbol;
    private readonly TextBlock _compactName;
    private readonly TextBlock _compactPrice;
    private readonly TextBlock _compactMetricLabel;
    private readonly OutlinedValueText _compactPremium;
    private readonly TextBlock _compactUpdated;

    private StockDetailsWindow? _details;
    private bool _edgeHideEnabled;
    private int _hiddenEdge;
    private double _revealedLeft;
    private Point _logicalPosition;
    private bool _positioned;
    private bool _movingWindow;
    private bool _refreshing;
    private int _requestVersion;
    private bool _permanentlyClosing;
    private DateTime _interactionAt = DateTime.MinValue;

    /// <summary>Raised for a premium alert; the module turns it into a notification.</summary>
    internal event Action<string, string>? AlertRaised;

    public StockWindow(StockStore store, StockSettings settings, StockDataService? service = null,
        bool edgeHideEnabled = false)
    {
        _store = store;
        _settings = settings;
        _ownsService = service is null;
        // Without an injected service the factory decides between the live sources
        // and the recorded replay (LITTLETOOLS_STOCK_FIXTURES).
        _service = service ?? StockDataServiceFactory.Create();
        _edgeHideEnabled = edgeHideEnabled;

        Title = "Little Tools · 股票观察";
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = settings.Topmost;
        ShowInTaskbar = false;
        CanResize = false;
        Width = StockTheme.CompactWidth;
        Height = StockTheme.CompactHeight;
        // A saved position is adopted when it is still on a screen, otherwise the
        // window starts centred, exactly like the Windows CenterScreen fallback.
        WindowStartupLocation = WindowStartupLocation.Manual;

        _compactSymbol = StockTheme.Label(settings.SelectedCode, 10.5,
            new SolidColorBrush(Color.FromArgb(175, 255, 255, 255)), bold: true);
        _compactName = StockTheme.Label("加载中", 10.5, new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)));
        _compactName.Margin = new Thickness(7, 0, 0, 0);
        _compactPrice = StockTheme.Label("--", 13, Brushes.White, bold: true);
        _compactPrice.HorizontalAlignment = HorizontalAlignment.Right;
        _compactMetricLabel = StockTheme.Label("参考溢价", 10, new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)));
        _compactPremium = StockTheme.PremiumValue(12.5);
        _compactPremium.HorizontalAlignment = HorizontalAlignment.Right;
        _compactUpdated = StockTheme.Label("单击查看明细", 9.5, new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));
        _compactUpdated.HorizontalAlignment = HorizontalAlignment.Right;

        _shell = new Border
        {
            CornerRadius = new CornerRadius(StockTheme.ShellCornerRadius),
            Background = StockTheme.ShellBackground,
            BorderBrush = StockTheme.ShellBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 9),
            BoxShadow = StockTheme.ShellShadow,
            Child = BuildCompact()
        };
        Content = _shell;

        _shell.PointerEntered += (_, _) =>
        {
            _shell.Background = StockTheme.ShellHover;
            _edgeHideTimer.Stop();
            RevealFromEdge();
        };
        _shell.PointerExited += (_, _) =>
        {
            _shell.Background = StockTheme.ShellBackground;
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
        _refreshTimer.Tick += async (_, _) => await RefreshAllMonitoredAsync(false);

        // X11 completes a move asynchronously, so the logical position is tracked
        // here and the platform value is adopted from the notification.
        PositionChanged += (_, args) =>
        {
            var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
            var logical = new Point(args.Point.X / scaling, args.Point.Y / scaling);
            if (Math.Abs(logical.X - _logicalPosition.X) < 1 && Math.Abs(logical.Y - _logicalPosition.Y) < 1) return;
            _logicalPosition = logical;
            if (_hiddenEdge == 0)
            {
                _settings.Left = logical.X;
                _settings.Top = logical.Y;
                SaveSettings();
            }
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
            _refreshTimer.Stop();
            _edgeHideTimer.Stop();
            CloseDetails();
            if (_ownsService) _service.Dispose();
        };

        RenderQuote(QuoteFor(_settings.SelectedCode));
        _refreshTimer.Start();
    }

    internal StockSettings Settings => _settings;

    internal StockDataService Service => _service;

    internal CandleChart? ChartForSmoke => _details?.ChartForSmoke;

    internal StockDetailsWindow? DetailsForSmoke => _details;

    internal bool IsDetailsOpen => _details is { IsVisible: true };

    internal Point LogicalPosition => _logicalPosition;

    internal int HiddenEdge => _hiddenEdge;

    /// <summary>
    /// A one line snapshot of the capsule for the render smoke, so the A-share
    /// colour rule and the premium outline state can be asserted from the run.
    /// </summary>
    internal string DescribeCompactForSmoke() =>
        "code=" + _compactSymbol.Text
        + ",name=" + _compactName.Text
        + ",price=" + _compactPrice.Text
        + ",changeColor=" + DescribeColor(_compactPrice.Foreground)
        + ",metric=" + _compactMetricLabel.Text
        + ",premium=" + _compactPremium.Text
        + ",outline=" + _compactPremium.OutlineEnabled
        + ",updated=" + _compactUpdated.Text;

    internal static string DescribeColor(IBrush? brush) =>
        brush is ISolidColorBrush solid ? solid.Color.ToString().ToUpperInvariant() : brush?.GetType().Name ?? "none";

    // ---------------------------------------------------------------- layout

    /// <summary>Windows BuildCompact (StockWindow.cs L264-L292): rows 27 / 27 / 14.</summary>
    private Grid BuildCompact()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("27,27,14"),
            Background = Brushes.Transparent
        };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(_compactSymbol);
        identity.Children.Add(_compactName);
        Grid.SetColumn(identity, 0);
        top.Children.Add(identity);
        Grid.SetColumn(_compactPrice, 1);
        top.Children.Add(_compactPrice);
        Grid.SetRow(top, 0);
        grid.Children.Add(top);

        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(_compactMetricLabel, 0);
        middle.Children.Add(_compactMetricLabel);
        Grid.SetColumn(_compactPremium, 1);
        middle.Children.Add(_compactPremium);
        Grid.SetRow(middle, 1);
        grid.Children.Add(middle);

        Grid.SetRow(_compactUpdated, 2);
        grid.Children.Add(_compactUpdated);
        return grid;
    }

    // -------------------------------------------------------------- behaviour

    /// <summary>
    /// Windows PrimaryMouseLeftButtonDown (StockWindow.cs L632-L647): a press
    /// starts a window move and a press without movement toggles the detail view.
    /// </summary>
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

    internal void ToggleDetails()
    {
        if (_details is { IsVisible: true })
        {
            CloseDetails();
            return;
        }

        var details = new StockDetailsWindow(this);
        _details = details;
        _edgeHideTimer.Stop();
        RevealFromEdge();
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
        details.Show(this);
        details.RenderQuote(QuoteFor(_settings.SelectedCode));
        details.UpdateActionButtons();
        Dispatcher.UIThread.Post(async () => await RefreshSelectedAsync(true));
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

    /// <summary>Windows PositionDetails (StockWindow.cs L730-L738).</summary>
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

    /// <summary>Windows ToggleVisibility (StockWindow.cs L777-L781).</summary>
    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            CloseDetails();
            Hide();
            return;
        }
        Show();
        Activate();
        Topmost = _settings.Topmost;
    }

    internal void ShowWindow()
    {
        if (!IsVisible) Show();
        if (!_positioned) PositionDefault();
        Activate();
        Topmost = _settings.Topmost;
    }

    internal void ClosePermanently()
    {
        _permanentlyClosing = true;
        CloseDetails();
        try { Close(); } catch { }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_positioned) PositionDefault();
        RenderQuote(QuoteFor(_settings.SelectedCode));
        _ = RefreshAllMonitoredAsync(true);
    }

    // ----------------------------------------------------------- data binding

    internal StockQuote? QuoteFor(string code) =>
        _quotes.TryGetValue(code, out var quote) ? quote : null;

    internal void SaveSettings()
    {
        try { _store.Save(_settings); } catch { }
    }

    internal void SetStatus(string value) => _details?.SetStatus(value);

    internal void ApplySecurityMetric(TextBlock label, OutlinedValueText target, StockQuote? quote)
    {
        if (quote is not null && !quote.IsEtf)
        {
            label.Text = "滚动 PE";
            target.Text = StockFormat.Pe(quote.Pe);
            target.OutlineEnabled = false;
            target.Foreground = quote.Pe.HasValue ? Brushes.White : StockTheme.MutedText;
            target.InvalidateVisual();
            return;
        }

        label.Text = "参考溢价";
        var premium = quote?.PremiumPercent;
        target.Text = StockFormat.Premium(premium);
        target.OutlineEnabled = StockFormat.PremiumOutlineEnabled(premium);
        target.Foreground = target.OutlineEnabled ? Brushes.White : StockTheme.MutedText;
        target.InvalidateVisual();
    }

    /// <summary>Windows RenderQuote (StockWindow.cs L495-L527).</summary>
    internal void RenderQuote(StockQuote? quote)
    {
        var code = quote?.Code ?? _settings.SelectedCode;
        var name = quote?.Name ?? "加载中";
        var price = quote is null ? "--" : StockFormat.Price(quote.Price);
        _compactSymbol.Text = code;
        _compactName.Text = StockFormat.ShortName(name);
        _compactPrice.Text = price;
        _compactPrice.Foreground = StockTheme.ChangeBrush(StockFormat.ChangeOf(quote?.ChangePercent ?? 0));
        ApplySecurityMetric(_compactMetricLabel, _compactPremium, quote);
        _compactUpdated.Text = StockFormat.CompactUpdated(quote);
        _details?.RenderQuote(quote);
    }

    /// <summary>Windows RenderValuation (StockWindow.cs L529-L535).</summary>
    internal void RenderValuation(ValuationInfo? value) => _details?.RenderValuation(value);

    /// <summary>Windows RenderTabs (StockWindow.cs L537-L561).</summary>
    internal void RenderTabs() => _details?.RenderTabs();

    internal void UpdateActionButtons() => _details?.UpdateActionButtons();

    // ---------------------------------------------------------------- refresh

    /// <summary>Windows RefreshAllMonitoredAsync (StockWindow.cs L416-L441).</summary>
    internal async Task RefreshAllMonitoredAsync(bool includeChart)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            foreach (var entry in _settings.Watched.ToArray())
            {
                try
                {
                    var quote = await _service.GetQuoteAsync(entry.Code!);
                    _quotes[entry.Code!] = quote;
                    CheckAlert(entry, quote);
                }
                catch
                {
                    // A single failing code must not stop the refresh.
                }
            }
            if (!_quotes.ContainsKey(_settings.SelectedCode))
            {
                try { _quotes[_settings.SelectedCode] = await _service.GetQuoteAsync(_settings.SelectedCode); }
                catch (Exception exception) { SetStatus(exception.Message); }
            }
            RenderTabs();
            RenderQuote(QuoteFor(_settings.SelectedCode));
            if (includeChart && _details is { IsVisible: true }) await RefreshDetailsAsync();
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Windows RefreshSelectedAsync (StockWindow.cs L443-L456).</summary>
    internal async Task RefreshSelectedAsync(bool details)
    {
        var version = ++_requestVersion;
        SetStatus("正在查询…");
        try
        {
            var quote = await _service.GetQuoteAsync(_settings.SelectedCode);
            if (version != _requestVersion) return;
            _quotes[_settings.SelectedCode] = quote;
            RenderTabs();
            RenderQuote(quote);
            if (details && _details is { IsVisible: true }) await RefreshDetailsAsync();
        }
        catch (Exception exception)
        {
            if (version == _requestVersion) SetStatus(exception.Message);
        }
    }

    /// <summary>Windows RefreshDetailsAsync (StockWindow.cs L458-L478).</summary>
    internal async Task RefreshDetailsAsync()
    {
        var code = _settings.SelectedCode;
        var version = ++_requestVersion;
        var quote = QuoteFor(code);
        try
        {
            var candlesTask = _service.GetCandlesAsync(code, _settings.KlinePeriod, _settings.RangeYears);
            var valuationTask = _service.GetValuationAsync(code, quote?.IsEtf ?? false, quote?.Pe,
                Math.Max(1, _settings.RangeYears), quote?.Name);
            await Task.WhenAll(candlesTask, valuationTask);
            if (version != _requestVersion || _settings.SelectedCode != code) return;
            _details?.SetChartData(candlesTask.Result);
            RenderValuation(valuationTask.Result);
            SetStatus(StockFormat.Status(quote));
        }
        catch (Exception exception)
        {
            if (version != _requestVersion) return;
            _details?.SetChartData(null);
            RenderValuation(null);
            SetStatus("部分数据暂不可用 · " + exception.Message);
        }
    }

    /// <summary>Windows QueryAsync (StockWindow.cs L407-L414).</summary>
    internal async Task QueryAsync(string code)
    {
        code = (code ?? string.Empty).Trim();
        if (!StockDataService.IsValidCode(code))
        {
            SetStatus("请输入六位沪深代码");
            return;
        }
        _settings.SelectedCode = code;
        SaveSettings();
        RenderTabs();
        await RefreshSelectedAsync(true);
    }

    /// <summary>Windows ToggleMonitor (StockWindow.cs L573-L579).</summary>
    internal void ToggleMonitor()
    {
        var existing = FindWatch(_settings.SelectedCode);
        if (existing is null)
            _settings.Watched.Add(new StockWatchEntry
            {
                Code = _settings.SelectedCode, AlertEnabled = true, PremiumThreshold = 2.0
            });
        else if (_settings.Watched.Count > 1) _settings.Watched.Remove(existing);
        SaveSettings();
        RenderTabs();
        UpdateActionButtons();
    }

    /// <summary>Windows ConfigureAlert (StockWindow.cs L581-L588).</summary>
    internal void ConfigureAlert()
    {
        var entry = FindWatch(_settings.SelectedCode);
        if (entry is null)
        {
            entry = new StockWatchEntry { Code = _settings.SelectedCode };
            _settings.Watched.Add(entry);
        }
        entry.AlertEnabled = !entry.AlertEnabled;
        entry.PremiumThreshold = 2.0;
        SaveSettings();
        RenderTabs();
        UpdateActionButtons();
    }

    internal StockWatchEntry? FindWatch(string code) =>
        _settings.Watched.FirstOrDefault(item => item.Code == code);

    /// <summary>Windows CheckAlert (StockWindow.cs L480-L493).</summary>
    internal void CheckAlert(StockWatchEntry entry, StockQuote quote)
    {
        var alert = _alerts.Check(entry, quote);
        if (alert is not null) AlertRaised?.Invoke(alert.Title, alert.Message);
    }

    internal void SelectCode(string code)
    {
        _settings.SelectedCode = code;
        SaveSettings();
        RenderTabs();
        RenderQuote(QuoteFor(code));
    }

    internal void SetChoice(bool period, string value)
    {
        if (period) _settings.KlinePeriod = value;
        else if (int.TryParse(value, out var years)) _settings.RangeYears = years;
        SaveSettings();
        _details?.UpdateChoiceButtons();
    }

    internal void SetTopmost(bool topmost)
    {
        _settings.Topmost = topmost;
        Topmost = topmost;
        if (_details is { } details) details.Topmost = topmost;
        SaveSettings();
        UpdateActionButtons();
    }

    public void SetEdgeHideEnabled(bool enabled)
    {
        _edgeHideEnabled = enabled;
        _edgeHideTimer.Stop();
        if (enabled) return;
        RevealFromEdge();
        _hiddenEdge = 0;
    }

    // ------------------------------------------------------------- positioning

    private void ApplyPosition(double x, double y)
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

    /// <summary>
    /// Windows falls back to CenterScreen when no saved position is on a screen;
    /// an off screen saved position is treated as absent here for the same reason.
    /// </summary>
    private void PositionDefault()
    {
        var work = WorkingArea();
        var left = _settings.Left;
        var top = _settings.Top;
        var onScreen = !double.IsNaN(left) && !double.IsNaN(top)
            && left + Width > work.Left && left < work.Right
            && top + Height > work.Top && top < work.Bottom;
        ApplyPosition(
            onScreen ? left : Math.Max(work.Left, work.Left + (work.Width - Width) / 2),
            onScreen ? top : Math.Max(work.Top, work.Top + (work.Height - Height) / 2));
        _positioned = true;
    }

    internal void EnsurePositioned()
    {
        if (!_positioned) PositionDefault();
    }

    /// <summary>Windows SnapOrHideAtEdge (StockWindow.cs L783-L807).</summary>
    internal void SnapOrHideAtEdge()
    {
        const double snapDistance = 28;
        var work = WorkingArea();
        var x = _logicalPosition.X;
        var y = Math.Max(work.Top, Math.Min(_logicalPosition.Y, work.Bottom - Height));

        if (!_edgeHideEnabled)
        {
            _hiddenEdge = 0;
            ApplyPosition(Math.Max(work.Left, Math.Min(x, work.Right - Width)), y);
            return;
        }

        if (x <= work.Left + snapDistance)
        {
            _hiddenEdge = -1;
            _revealedLeft = work.Left;
            if (IsDetailsOpen) ApplyPosition(_revealedLeft, y);
            else HideToEdge();
        }
        else if (x + Width >= work.Right - snapDistance)
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

    private void RevealFromEdge()
    {
        if (_hiddenEdge != 0) ApplyPosition(_revealedLeft, _logicalPosition.Y);
    }

    /// <summary>Windows HideToEdge (StockWindow.cs L810-L815): a nine pixel strip stays visible.</summary>
    private void HideToEdge()
    {
        if (!_edgeHideEnabled || IsDetailsOpen) return;
        var work = WorkingArea();
        if (_hiddenEdge < 0) ApplyPosition(work.Left - Width + 9, _logicalPosition.Y);
        else if (_hiddenEdge > 0) ApplyPosition(work.Right - 9, _logicalPosition.Y);
    }
}
