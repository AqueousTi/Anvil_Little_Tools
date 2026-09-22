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

    // The last successfully fetched series per (code, period, span). The detail
    // window is closed whenever it loses focus, and a reopened detail window is a
    // new instance, so the cache has to live on the owner. It is what makes a return
    // to an already seen stock or period paint immediately instead of showing an
    // empty chart while the hosts are asked again.
    private readonly Dictionary<string, List<Candle>> _chartCache = new(StringComparer.Ordinal);
    private readonly List<string> _chartCacheOrder = [];
    private const int ChartCacheLimit = 16;

    /// <summary>How long the valuation host may take before the card gives up.</summary>
    private static readonly TimeSpan ValuationBudget = TimeSpan.FromSeconds(8);
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
        TransparencyLevelHint = GlassSurface.TransparencyLevels;
        Background = Brushes.Transparent;
        // The capsule is always on top, like the todo capsule (TodoWindow.cs L104),
        // instead of following StockSettings.Topmost the way Windows does
        // (StockData.cs L27 defaults it to false). Intentional deviation, see the
        // porting notes; the detail window still follows the setting.
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = StockTheme.CompactWidth;
        Height = StockTheme.CompactHeight;
        // A saved position is adopted when it is still on a screen, otherwise the
        // window starts centred, exactly like the Windows CenterScreen fallback.
        WindowStartupLocation = WindowStartupLocation.Manual;

        _compactSymbol = StockTheme.Label(settings.SelectedCode, 10.5,
            new SolidColorBrush(Color.FromArgb(196, 255, 255, 255)), bold: true);
        _compactName = StockTheme.Label("加载中", 10.5, new SolidColorBrush(Color.FromArgb(182, 255, 255, 255)));
        _compactName.Margin = new Thickness(7, 0, 0, 0);
        _compactPrice = StockTheme.Label("--", 13, Brushes.White, bold: true);
        _compactPrice.HorizontalAlignment = HorizontalAlignment.Right;
        _compactMetricLabel = StockTheme.Label("参考溢价", 10, new SolidColorBrush(Color.FromArgb(182, 255, 255, 255)));
        _compactPremium = StockTheme.PremiumValue(12.5);
        _compactPremium.HorizontalAlignment = HorizontalAlignment.Right;
        _compactUpdated = StockTheme.Label("单击查看明细", 9.5, new SolidColorBrush(Color.FromArgb(158, 255, 255, 255)));
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
        ReapplyWindowFlags(details, details.Topmost);
        details.RenderQuote(QuoteFor(_settings.SelectedCode));
        // A reopened detail window is a new instance: paint the selection's cached
        // series straight away instead of leaving it blank until the refresh lands.
        RenderCachedChart();
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
        Topmost = true;
        ReapplyWindowFlags(this, true);
    }

    internal void ShowWindow()
    {
        if (!IsVisible) Show();
        if (!_positioned) PositionDefault();
        Activate();
        Topmost = true;
        ReapplyWindowFlags(this, true);
    }

    /// <summary>
    /// Re-applies the always-on-top and skip-taskbar hints after a window has been
    /// shown again.
    ///
    /// Two X11 details only combine on a hide/show cycle: mutter deletes
    /// <c>_NET_WM_STATE</c> when a window is unmapped, and Avalonia pushes both
    /// <c>Window.Topmost</c> (WindowBase.CreatePlatformImplBinding) and
    /// <c>Window.ShowInTaskbar</c> (Window.CreatePlatformImplBinding) to the
    /// platform only when the property <i>changes</i>. A re-mapped capsule was
    /// therefore managed without <c>_NET_WM_STATE_SKIP_TASKBAR</c> (so the WM
    /// listed it in the taskbar) and without <c>_NET_WM_STATE_ABOVE</c> (so it
    /// stopped floating). The capsule is hidden by the Ctrl+Alt+Q toggle and by
    /// Close, so this runs after every show. Assigning the value the property
    /// already has would be a no-op, hence the round trip through false.
    /// </summary>
    internal static void ReapplyWindowFlags(Window window, bool topmost)
    {
        window.Topmost = false;
        if (topmost) window.Topmost = true;
        if (window.ShowInTaskbar) return;
        window.ShowInTaskbar = true;
        window.ShowInTaskbar = false;
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
        var token = CaptureToken();
        SetStatus("正在查询…");
        try
        {
            var quote = await _service.GetQuoteAsync(token.Code);
            // The answer may only touch the screen while it still describes the
            // selection the user is looking at: a quote fetched for the previous
            // code must never be rendered next to the new one.
            if (!IsCurrent(token)) return;
            _quotes[token.Code] = quote;
            RenderTabs();
            RenderQuote(quote);
            if (details && _details is { IsVisible: true }) await RefreshDetailsAsync();
        }
        catch (Exception exception)
        {
            if (IsCurrent(token)) SetStatus(exception.Message);
        }
    }

    /// <summary>
    /// Windows RefreshDetailsAsync (StockWindow.cs L458-L478), with two deliberate
    /// changes:
    ///
    ///   * the answer is validated against the selection before it is applied, so a
    ///     late response can neither redraw a chart for a code the user left nor be
    ///     dropped so completely that the chart stays empty;
    ///   * the candles and the valuation are applied independently. Windows awaited
    ///     both with <c>Task.WhenAll</c> and cleared the chart when either failed, so
    ///     a slow or broken valuation host took a perfectly good chart off the
    ///     screen;
    ///   * a successful series is cached per (code, period, span) and a failed
    ///     refresh keeps the last good series for the live selection instead of
    ///     blanking it. Intentional deviation from Windows (which clears and shows
    ///     the placeholder), requested by the user: the empty window while a stock is
    ///     re-queried was reported as a defect.
    /// </summary>
    internal async Task RefreshDetailsAsync()
    {
        var token = CaptureToken();
        var quote = QuoteFor(token.Code);
        var candlesTask = _service.GetCandlesAsync(token.Code, token.Period, token.Years);
        // The valuation comes from a different host and can be slow or unreachable
        // (multpl.com resolves slowly from some networks). It is bounded so it can
        // never keep the window reporting progress once the chart is decided.
        using var valuationBudget = new CancellationTokenSource(ValuationBudget);
        var valuationTask = _service.GetValuationAsync(token.Code, quote?.IsEtf ?? false, quote?.Pe,
            Math.Max(1, token.Years), quote?.Name, valuationBudget.Token);

        // The chart is applied as soon as the candles are in so a slower valuation
        // cannot hold it back.
        var candles = await CaptureAsync(candlesTask);
        if (candles.Value is not null)
        {
            // The cache is keyed by what the request was made for, never by the live
            // selection, so a late answer cannot seed another stock's entry.
            RememberChart(ChartKey(token.Code, token.Period, token.Years), candles.Value);
            if (IsCurrent(token)) _details?.SetChartData(candles.Value, token.Period);
        }
        // A failure keeps the last good series of the live selection instead of
        // blanking it (Windows cleared here; the user asked for the opposite - see
        // the porting notes). Only a selection that never produced data falls back to
        // the placeholder, and it must: at that point the chart may still hold the
        // *previous* stock's picture, which must never be presented as this one's.

        if (IsCurrent(token))
        {
            if (candles.Value is null)
            {
                // No fresh series: keep this selection's last good one if it has any,
                // otherwise show the placeholder - never the previous stock's chart.
                // The cached series belongs to the live selection, so its period is
                // the one the axis has to be formatted for.
                if (TryGetCachedChart(out var cached) && cached is not null) _details?.SetChartData(cached, token.Period);
                else _details?.SetChartData(null, token.Period);
            }
            // Report as soon as the chart is decided: the window must not keep
            // saying "正在查询…" while a slow valuation host is still answering.
            SetStatus(candles.Value is null
                ? "部分数据暂不可用 · " + candles.Error!.Message
                : StockFormat.Status(quote));
        }

        // Always awaited, even when the selection moved on, so a late failure is
        // observed instead of surfacing as an unobserved task exception.
        var valuation = await CaptureAsync(valuationTask);
        if (!IsCurrent(token)) return;
        RenderValuation(valuation.Value);
        if (candles.Value is not null && valuation.Value is null)
            SetStatus("部分数据暂不可用 · " + valuation.Error!.Message);
    }

    /// <summary>A chart cache key: the query, not the moment.</summary>
    private static string ChartKey(string code, string period, int years) => code + "|" + period + "|"
        + years.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Stores a series and keeps the cache small (oldest entry evicted).</summary>
    private void RememberChart(string key, List<Candle> candles)
    {
        _chartCache[key] = candles;
        _chartCacheOrder.Remove(key);
        _chartCacheOrder.Add(key);
        while (_chartCacheOrder.Count > ChartCacheLimit)
        {
            _chartCache.Remove(_chartCacheOrder[0]);
            _chartCacheOrder.RemoveAt(0);
        }
    }

    /// <summary>
    /// Paints the cached series of the live selection. Synchronous and network free,
    /// so returning to a stock, a period or a span shows its chart at once; the
    /// refresh that follows replaces it with fresh data.
    ///
    /// A selection that was never fetched keeps whatever is on screen instead of
    /// being blanked: the user reported the empty window between the click and the
    /// answer as the defect. That is safe because the identity check never lets the
    /// previous stock's data be *applied* for the new selection - the old picture is
    /// only still visible while the request is in flight, and a failure for a
    /// selection with no cached series does clear it (see RefreshDetailsAsync).
    /// </summary>
    private void RenderCachedChart()
    {
        if (_details is not { } details) return;
        if (!_chartCache.TryGetValue(CurrentChartKey(), out var candles)) return;
        details.SetChartData(candles, Settings.KlinePeriod);
    }

    private string CurrentChartKey() => ChartKey(_settings.SelectedCode ?? string.Empty,
        _settings.KlinePeriod ?? StockPeriods.Daily, _settings.RangeYears);

    private bool TryGetCachedChart(out List<Candle>? candles) =>
        _chartCache.TryGetValue(CurrentChartKey(), out candles);

    /// <summary>Number of cached series, for the render smoke.</summary>
    internal int CachedChartCount => _chartCache.Count;

    /// <summary>The identity of a refresh: what the request is being made for.</summary>
    private StockRefreshToken CaptureToken() => new(
        _settings.SelectedCode ?? string.Empty,
        _settings.KlinePeriod ?? StockPeriods.Daily,
        _settings.RangeYears,
        ++_requestVersion);

    /// <summary>True while the token still describes the live selection.</summary>
    private bool IsCurrent(in StockRefreshToken token) => token.Matches(
        _settings.SelectedCode ?? string.Empty,
        _settings.KlinePeriod ?? StockPeriods.Daily,
        _settings.RangeYears);

    /// <summary>Runs a request without throwing, so both halves can be applied.</summary>
    private static async Task<(T? Value, Exception? Error)> CaptureAsync<T>(Task<T> task) where T : class
    {
        try
        {
            return (await task, null);
        }
        catch (Exception exception)
        {
            return (null, exception);
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
        // Instant feedback: the cached series of the new selection if it has one.
        RenderCachedChart();
    }

    internal void SetChoice(bool period, string value)
    {
        if (period) _settings.KlinePeriod = value;
        else if (int.TryParse(value, out var years)) _settings.RangeYears = years;
        SaveSettings();
        _details?.UpdateChoiceButtons();
        // A period or span that was looked at before paints from its cache at once.
        RenderCachedChart();
    }

    internal void SetTopmost(bool topmost)
    {
        _settings.Topmost = topmost;
        // The Linux capsule stays on top regardless of the switch; the pin button
        // only drives the detail window now (see the porting notes).
        Topmost = true;
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
