using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// The stock detail window: search, watch tabs, quote card, period and span
/// choices, the self drawn chart, the valuation card and the action footer. The
/// Windows module built this as a second <c>Window</c> from
/// StockWindow.BuildDetailsContent (StockMonitor/StockWindow.cs L294-L405) and
/// kept its control references on the capsule; the Avalonia port keeps the same
/// window split and moves the references here.
/// </summary>
internal sealed class StockDetailsWindow : Window
{
    private const double TabScrollStep = 170;

    private readonly StockWindow _owner;
    private readonly Grid _root;
    private readonly TextBox _searchBox;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _tabScroll;
    private readonly Button _tabLeft;
    private readonly Button _tabRight;
    private readonly TextBlock _symbol;
    private readonly TextBlock _name;
    private readonly TextBlock _price;
    private readonly TextBlock _change;
    private readonly TextBlock _metricLabel;
    private readonly OutlinedValueText _premium;
    private readonly TextBlock _instrumentLabel;
    private readonly TextBlock _iopv;
    private readonly TextBlock _pe;
    private readonly TextBlock _percentile;
    private readonly TextBlock _valuationSource;
    private readonly TextBlock _status;
    private readonly Button _monitorButton;
    private readonly Button _alertButton;
    private readonly Button _pinButton;
    private readonly CandleChart _chart = new() { Margin = new Thickness(0, 2, 0, 7) };
    private readonly WrapPanel _periodButtons = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _rangeButtons = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
    };

    private Point _logicalPosition;
    private bool _positioned;

    public StockDetailsWindow(StockWindow owner)
    {
        _owner = owner;
        var settings = owner.Settings;

        Title = "Little Tools · 股票观察明细";
        Width = StockTheme.DetailsWidth;
        Height = StockTheme.DetailsHeight;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = GlassSurface.TransparencyLevels;
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = settings.Topmost;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Row heights from Windows BuildDetailsContent (StockWindow.cs L297-L305).
        _root = new Grid { Margin = new Thickness(16, 11, 16, 13) };
        foreach (var height in new[] { 36d, 43d, 43d, 104d, 34d, 224d, 100d, 43d })
            _root.RowDefinitions.Add(new RowDefinition(new GridLength(height)));

        // Row 0: the title bar also drags the capsule, like MoveFromDetails.
        var title = new Grid { Background = Brushes.Transparent };
        title.Children.Add(StockTheme.Label("股票观察", 14, Brushes.White, bold: true));
        title.PointerPressed += MoveFromDetails;
        Grid.SetRow(title, 0);
        _root.Children.Add(title);

        // Row 1: search.
        var search = new Grid { Margin = new Thickness(0, 3, 0, 5), ColumnDefinitions = new ColumnDefinitions("*,76") };
        _searchBox = StockTheme.InputBox("输入沪深代码");
        _searchBox.Text = settings.SelectedCode;
        _searchBox.KeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            await _owner.QueryAsync(_searchBox.Text ?? string.Empty);
        };
        Grid.SetColumn(_searchBox, 0);
        search.Children.Add(_searchBox);
        var queryButton = StockTheme.SmallButton("查询", 68);
        queryButton.Margin = new Thickness(8, 0, 0, 0);
        queryButton.Click += async (_, _) => await _owner.QueryAsync(_searchBox.Text ?? string.Empty);
        Grid.SetColumn(queryButton, 1);
        search.Children.Add(queryButton);
        Grid.SetRow(search, 1);
        _root.Children.Add(search);

        // Row 2: the watch tab strip with its scroll arrows.
        var tabStrip = new Grid { ColumnDefinitions = new ColumnDefinitions("29,*,29") };
        _tabLeft = StockTheme.SmallButton("‹", 25);
        _tabLeft.Margin = new Thickness(0, 0, 4, 0);
        _tabLeft.Click += (_, _) => ScrollTabs(-TabScrollStep);
        Grid.SetColumn(_tabLeft, 0);
        tabStrip.Children.Add(_tabLeft);
        _tabScroll = new ScrollViewer
        {
            Content = _tabs,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _tabScroll.PointerWheelChanged += (_, args) =>
        {
            if (ScrollableTabs <= 0) return;
            ScrollTabs(-args.Delta.Y * 0.45);
            args.Handled = true;
        };
        _tabScroll.PropertyChanged += (_, args) =>
        {
            if (args.Property == ScrollViewer.OffsetProperty || args.Property == ScrollViewer.ExtentProperty
                || args.Property == ScrollViewer.ViewportProperty) UpdateTabNavigation();
        };
        Grid.SetColumn(_tabScroll, 1);
        tabStrip.Children.Add(_tabScroll);
        _tabRight = StockTheme.SmallButton("›", 25);
        _tabRight.Margin = new Thickness(4, 0, 0, 0);
        _tabRight.Click += (_, _) => ScrollTabs(TabScrollStep);
        Grid.SetColumn(_tabRight, 2);
        tabStrip.Children.Add(_tabRight);
        Grid.SetRow(tabStrip, 2);
        _root.Children.Add(tabStrip);

        // Row 3: the quote card.
        var quoteCard = StockTheme.Card(new Thickness(0, 3, 0, 6));
        var quoteGrid = new Grid
        {
            Margin = new Thickness(13, 9, 13, 8),
            ColumnDefinitions = new ColumnDefinitions("1.45*,*,*")
        };
        var main = new StackPanel();
        var identity = new StackPanel { Orientation = Orientation.Horizontal };
        _symbol = StockTheme.Label(settings.SelectedCode, 10.5, StockTheme.SecondaryText, bold: true);
        _name = StockTheme.Label("加载中", 10.5, StockTheme.SecondaryText);
        _name.Margin = new Thickness(7, 0, 0, 0);
        identity.Children.Add(_symbol);
        identity.Children.Add(_name);
        main.Children.Add(identity);
        var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _price = StockTheme.Label("--", 28, Brushes.White, bold: true);
        _change = StockTheme.Label("--", 11, StockTheme.SecondaryText, bold: true);
        _change.Margin = new Thickness(8, 12, 0, 0);
        priceRow.Children.Add(_price);
        priceRow.Children.Add(_change);
        main.Children.Add(priceRow);
        Grid.SetColumn(main, 0);
        quoteGrid.Children.Add(main);
        var premium = StockTheme.PremiumMetric("参考溢价", out _premium);
        _metricLabel = (TextBlock)premium.Children[0];
        Grid.SetColumn(premium, 1);
        quoteGrid.Children.Add(premium);
        var iopv = StockTheme.Metric("IOPV", out _iopv);
        _instrumentLabel = (TextBlock)iopv.Children[0];
        Grid.SetColumn(iopv, 2);
        quoteGrid.Children.Add(iopv);
        quoteCard.Child = quoteGrid;
        Grid.SetRow(quoteCard, 3);
        _root.Children.Add(quoteCard);

        // Row 4: the period and span choices.
        AddChoice(_periodButtons, "分时", StockPeriods.Minute, true);
        AddChoice(_periodButtons, "日K", StockPeriods.Daily, true);
        AddChoice(_periodButtons, "周K", StockPeriods.Weekly, true);
        AddChoice(_periodButtons, "月K", StockPeriods.Monthly, true);
        AddChoice(_rangeButtons, "1月", "0", false);
        AddChoice(_rangeButtons, "1年", "1", false);
        AddChoice(_rangeButtons, "3年", "3", false);
        AddChoice(_rangeButtons, "5年", "5", false);
        var choices = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(_periodButtons, 0);
        choices.Children.Add(_periodButtons);
        Grid.SetColumn(_rangeButtons, 1);
        choices.Children.Add(_rangeButtons);
        Grid.SetRow(choices, 4);
        _root.Children.Add(choices);

        // Row 5: the chart.
        Grid.SetRow(_chart, 5);
        _root.Children.Add(_chart);

        // Row 6: the valuation card.
        var valuation = StockTheme.Card(new Thickness(0, 0, 0, 7));
        var valuationGrid = new Grid
        {
            Margin = new Thickness(13, 9, 10, 8),
            ColumnDefinitions = new ColumnDefinitions("105,*,105")
        };
        var pe = StockTheme.Metric("滚动 PE", out _pe);
        Grid.SetColumn(pe, 0);
        valuationGrid.Children.Add(pe);
        var percentile = new StackPanel();
        percentile.Children.Add(StockTheme.Label("历史分位", 9.5, StockTheme.SecondaryText));
        _percentile = StockTheme.Label("--", 19, Brushes.White, bold: true);
        _percentile.Margin = new Thickness(0, 5, 0, 0);
        percentile.Children.Add(_percentile);
        _valuationSource = StockTheme.Label("等待估值数据", 8.5, StockTheme.SecondaryText);
        _valuationSource.Margin = new Thickness(0, 3, 0, 0);
        percentile.Children.Add(_valuationSource);
        Grid.SetColumn(percentile, 1);
        valuationGrid.Children.Add(percentile);
        _monitorButton = StockTheme.SmallButton("加入监控", 94);
        _monitorButton.VerticalAlignment = VerticalAlignment.Center;
        _monitorButton.Click += (_, _) => _owner.ToggleMonitor();
        Grid.SetColumn(_monitorButton, 2);
        valuationGrid.Children.Add(_monitorButton);
        valuation.Child = valuationGrid;
        Grid.SetRow(valuation, 6);
        _root.Children.Add(valuation);

        // Row 7: the footer.
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("190,72,*") };
        _alertButton = StockTheme.SmallButton("提醒 ≤ 2%", 180);
        _alertButton.HorizontalAlignment = HorizontalAlignment.Left;
        _alertButton.Click += (_, _) => _owner.ConfigureAlert();
        Grid.SetColumn(_alertButton, 0);
        footer.Children.Add(_alertButton);
        _pinButton = StockTheme.SmallButton(settings.Topmost ? "置顶 ✓" : "置顶", 65);
        _pinButton.Click += (_, _) => _owner.SetTopmost(!_owner.Settings.Topmost);
        Grid.SetColumn(_pinButton, 1);
        footer.Children.Add(_pinButton);
        _status = StockTheme.Label("Ctrl + Alt + Q", 8.8, StockTheme.SecondaryText);
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_status, 2);
        footer.Children.Add(_status);
        Grid.SetRow(footer, 7);
        _root.Children.Add(footer);

        Content = new Border
        {
            CornerRadius = new CornerRadius(StockTheme.DetailsCornerRadius),
            Background = StockTheme.DetailsBackground,
            BorderBrush = StockTheme.DetailsBorder,
            BorderThickness = new Thickness(1),
            BoxShadow = StockTheme.DetailsShadow,
            Child = _root
        };

        RenderTabs();
        UpdateChoiceButtons();
    }

    internal CandleChart ChartForSmoke => _chart;

    internal bool IsMoving { get; private set; }

    /// <summary>The realised chart geometry of the last paint, for the render smoke.</summary>
    internal string DescribeChartForSmoke() => _chart.LastRender?.ToString() ?? "no-render";

    /// <summary>Everything the detail window shows, so the render smoke can assert it.</summary>
    internal string DescribeDetailsForSmoke() =>
        "symbol=" + _symbol.Text
        + ",name=" + _name.Text
        + ",price=" + _price.Text
        + ",change=" + _change.Text
        + ",changeColor=" + StockWindow.DescribeColor(_change.Foreground)
        + ",metric=" + _metricLabel.Text
        + ",premium=" + _premium.Text
        + ",outline=" + _premium.OutlineEnabled
        + ",instrument=" + _instrumentLabel.Text
        + ",instrumentValue=" + _iopv.Text
        + ",pe=" + _pe.Text
        + ",percentile=" + _percentile.Text
        + ",valuation=" + _valuationSource.Text
        + ",monitor=" + _monitorButton.Content
        + ",alert=" + _alertButton.Content
        + ",alertEnabled=" + _alertButton.IsEnabled
        + ",pin=" + _pinButton.Content
        + ",status=" + _status.Text
        + ",tabs=" + _tabs.Children.Count
        + ",period=" + SelectedChoice(_periodButtons)
        + ",range=" + SelectedChoice(_rangeButtons);

    private static string SelectedChoice(WrapPanel panel) =>
        string.Join('|', panel.Children.OfType<Button>()
            .Where(button => ReferenceEquals(button.Background, StockTheme.ChoiceSelected))
            .Select(button => Convert.ToString(button.Tag)));

    internal Point LogicalPosition => _logicalPosition;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_positioned) PositionBesideOwner();
        UpdateTabNavigation();
    }

    // ---------------------------------------------------------------- layout

    /// <summary>Windows AddChoice (StockWindow.cs L608-L617).</summary>
    private void AddChoice(WrapPanel panel, string caption, string value, bool period)
    {
        var button = StockTheme.SmallButton(caption, 47);
        button.Tag = value;
        button.Margin = new Thickness(0, 0, 4, 0);
        button.Click += async (_, _) =>
        {
            _owner.SetChoice(period, value);
            UpdateChoiceButtons();
            await _owner.RefreshDetailsAsync();
        };
        panel.Children.Add(button);
    }

    /// <summary>Windows UpdateChoiceButtons (StockWindow.cs L619-L630).</summary>
    internal void UpdateChoiceButtons()
    {
        UpdateChoices(_periodButtons, _owner.Settings.KlinePeriod);
        UpdateChoices(_rangeButtons, _owner.Settings.RangeYears.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void UpdateChoices(WrapPanel panel, string selected)
    {
        foreach (var button in panel.Children.OfType<Button>())
            button.Background = Convert.ToString(button.Tag) == selected ? StockTheme.ChoiceSelected : StockTheme.ChoiceIdle;
    }

    // ----------------------------------------------------------------- render

    /// <summary>Windows RenderQuote (StockWindow.cs L495-L527), detail columns only.</summary>
    internal void RenderQuote(StockQuote? quote)
    {
        var code = quote?.Code ?? _owner.Settings.SelectedCode;
        var name = quote?.Name ?? "加载中";
        _symbol.Text = code;
        _name.Text = name;
        _price.Text = quote is null ? "--" : StockFormat.Price(quote.Price);
        _change.Text = quote is null ? "--" : StockFormat.Change(quote.ChangePercent);
        var kind = StockFormat.ChangeOf(quote?.ChangePercent ?? 0);
        _change.Foreground = StockTheme.ChangeBrush(kind);
        _price.Foreground = StockTheme.ChangeBrush(kind);
        _owner.ApplySecurityMetric(_metricLabel, _premium, quote);
        _instrumentLabel.Text = StockFormat.InstrumentLabel(quote);
        _iopv.Text = StockFormat.InstrumentValue(quote);
        UpdateActionButtons();
    }

    /// <summary>Windows RenderValuation (StockWindow.cs L529-L535).</summary>
    internal void RenderValuation(ValuationInfo? value)
    {
        _pe.Text = StockFormat.Pe(value?.CurrentPe);
        _percentile.Text = StockFormat.Percentile(value);
        _valuationSource.Text = StockFormat.ValuationSource(value);
    }

    /// <summary>Windows RenderTabs (StockWindow.cs L537-L561).</summary>
    internal void RenderTabs()
    {
        _tabs.Children.Clear();
        Button? selected = null;
        foreach (var entry in _owner.Settings.Watched)
        {
            var code = entry.Code ?? string.Empty;
            var quote = _owner.QuoteFor(code);
            var caption = StockFormat.TabCaption(code, quote);
            var button = StockTheme.SmallButton(caption, StockFormat.TabWidth(caption));
            button.Margin = new Thickness(0, 0, 7, 0);
            button.Background = code == _owner.Settings.SelectedCode ? StockTheme.TabSelected : StockTheme.TabIdle;
            button.Click += async (_, _) =>
            {
                _owner.SelectCode(code);
                _searchBox.Text = code;
                // Render from the cache as it is *now*: the quote captured when this
                // tab was built can be older than the selection it is clicked for.
                RenderQuote(_owner.QuoteFor(code));
                await _owner.RefreshSelectedAsync(true);
            };
            _tabs.Children.Add(button);
            if (code == _owner.Settings.SelectedCode) selected = button;
        }

        var reveal = selected;
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTabNavigation();
            try { reveal?.BringIntoView(); } catch { }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Windows UpdateTabNavigation (StockWindow.cs L563-L571).</summary>
    internal void UpdateTabNavigation()
    {
        var scrollable = ScrollableTabs;
        var offset = _tabScroll.Offset.X;
        _tabLeft.IsEnabled = scrollable > 0.5 && offset > 0.5;
        _tabRight.IsEnabled = scrollable > 0.5 && offset < scrollable - 0.5;
        _tabLeft.Opacity = _tabLeft.IsEnabled ? StockTheme.TabArrowActiveOpacity : StockTheme.TabArrowIdleOpacity;
        _tabRight.Opacity = _tabRight.IsEnabled ? StockTheme.TabArrowActiveOpacity : StockTheme.TabArrowIdleOpacity;
    }

    private double ScrollableTabs => Math.Max(0, _tabScroll.Extent.Width - _tabScroll.Viewport.Width);

    private void ScrollTabs(double delta)
    {
        var target = Math.Max(0, Math.Min(ScrollableTabs, _tabScroll.Offset.X + delta));
        _tabScroll.Offset = new Vector(target, _tabScroll.Offset.Y);
        UpdateTabNavigation();
    }

    /// <summary>Windows UpdateActionButtons (StockWindow.cs L590-L601).</summary>
    internal void UpdateActionButtons()
    {
        var entry = _owner.FindWatch(_owner.Settings.SelectedCode);
        _monitorButton.Content = entry is null ? "加入监控" : "移出监控";
        var quote = _owner.QuoteFor(_owner.Settings.SelectedCode);
        var supportsPremium = quote is not null && quote.IsEtf;
        _alertButton.IsEnabled = supportsPremium;
        _alertButton.Opacity = supportsPremium ? 1.0 : 0.45;
        _alertButton.Content = StockFormat.AlertCaption(supportsPremium, entry?.AlertEnabled ?? false);
        _pinButton.Content = _owner.Settings.Topmost ? "置顶 ✓" : "置顶";
    }

    internal void SetStatus(string value) => _status.Text = value;

    internal void SetChartData(IEnumerable<Candle>? candles)
    {
        _chart.SetData(candles);
    }

    // ------------------------------------------------------------ positioning

    /// <summary>Windows MoveFromDetails (StockWindow.cs L740-L754): the title drags the capsule.</summary>
    private void MoveFromDetails(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        IsMoving = true;
        try
        {
            _owner.BeginMoveDrag(args);
            _owner.SnapOrHideAtEdge();
            PositionBesideOwner();
            Activate();
        }
        catch
        {
            // A window manager may refuse the move; the capsule keeps its position.
        }
        finally
        {
            IsMoving = false;
        }
        args.Handled = true;
    }

    internal void PositionBesideOwner()
    {
        var work = _owner.WorkingArea();
        var left = _owner.LogicalPosition.X + _owner.Width - Width;
        var top = _owner.LogicalPosition.Y + _owner.Height + 8;
        ApplyPosition(
            Math.Max(work.Left, Math.Min(work.Right - Width, left)),
            Math.Max(work.Top, Math.Min(work.Bottom - Height, top)));
    }

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
        _positioned = true;
    }
}
