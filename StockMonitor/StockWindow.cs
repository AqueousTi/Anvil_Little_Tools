using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LittleTools.StockMonitor
{
    internal sealed class OutlinedValueText : Control
    {
        private string value = "--";
        public string Text { get { return value; } set { this.value = value ?? ""; InvalidateMeasure(); InvalidateVisual(); } }
        public bool OutlineEnabled { get; set; }
        public Brush OutlineBrush { get; set; }
        public double OutlineThickness { get; set; }

        protected override Size MeasureOverride(Size constraint)
        {
            FormattedText text = CreateText();
            return new Size(text.Width + 5, text.Height + 4);
        }

        protected override void OnRender(DrawingContext context)
        {
            base.OnRender(context);
            FormattedText text = CreateText();
            double x = HorizontalContentAlignment == HorizontalAlignment.Right ? Math.Max(2, ActualWidth - text.Width - 2)
                : HorizontalContentAlignment == HorizontalAlignment.Center ? Math.Max(2, (ActualWidth - text.Width) / 2) : 2;
            double y = Math.Max(1, (ActualHeight - text.Height) / 2);
            Geometry geometry = text.BuildGeometry(new Point(x, y));
            Pen outline = null;
            if (OutlineEnabled && OutlineBrush != null)
            {
                outline = new Pen(OutlineBrush, Math.Max(0.5, OutlineThickness));
                outline.LineJoin = PenLineJoin.Round;
            }
            context.DrawGeometry(Foreground, outline, geometry);
        }

        private FormattedText CreateText()
        {
            return new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground ?? Brushes.White, 1.0);
        }
    }

    internal sealed class CandleChart : FrameworkElement
    {
        private List<Candle> candles = new List<Candle>();
        public void SetData(IEnumerable<Candle> value) { candles = value == null ? new List<Candle>() : value.ToList(); InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = ActualWidth;
            double height = ActualHeight;
            if (width < 20 || height < 20) return;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(55, 8, 9, 12)), new Pen(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1), new Rect(0.5, 0.5, width - 1, height - 1), 10, 10);
            if (candles.Count < 2)
            {
                var text = new FormattedText("暂无走势数据", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 11, new SolidColorBrush(Color.FromArgb(135, 255, 255, 255)), 1.0);
                dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
                return;
            }

            double left = 12, right = 43, top = 12, bottom = 22;
            double plotWidth = Math.Max(1, width - left - right);
            double plotHeight = Math.Max(1, height - top - bottom);
            double min = candles.Min(delegate(Candle item) { return item.Low; });
            double max = candles.Max(delegate(Candle item) { return item.High; });
            if (max <= min) max = min + 1;
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
            var labelBrush = new SolidColorBrush(Color.FromArgb(125, 255, 255, 255));
            for (int line = 0; line < 4; line++)
            {
                double y = top + plotHeight * line / 3.0;
                dc.DrawLine(gridPen, new Point(left, y), new Point(left + plotWidth, y));
                double value = max - (max - min) * line / 3.0;
                var label = new FormattedText(value.ToString(value < 10 ? "0.000" : "0.00", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, labelBrush, 1.0);
                dc.DrawText(label, new Point(width - right + 5, y - label.Height / 2));
            }

            double step = plotWidth / candles.Count;
            bool lineMode = candles.Count > 260 || candles.All(delegate(Candle item) { return Math.Abs(item.Open - item.Close) < 0.0000001; });
            if (lineMode)
            {
                var geometry = new StreamGeometry();
                using (StreamGeometryContext context = geometry.Open())
                {
                    for (int index = 0; index < candles.Count; index++)
                    {
                        Point point = new Point(left + (index + 0.5) * step, Y(candles[index].Close, min, max, top, plotHeight));
                        if (index == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
                    }
                }
                geometry.Freeze();
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(238, 92, 94)), 1.5), geometry);
            }
            else
            {
                double bodyWidth = Math.Max(1, Math.Min(8, step * 0.58));
                for (int index = 0; index < candles.Count; index++)
                {
                    Candle item = candles[index];
                    double x = left + (index + 0.5) * step;
                    bool up = item.Close >= item.Open;
                    Brush brush = up ? new SolidColorBrush(Color.FromRgb(239, 88, 91)) : new SolidColorBrush(Color.FromRgb(156, 161, 171));
                    Pen pen = new Pen(brush, 1);
                    dc.DrawLine(pen, new Point(x, Y(item.High, min, max, top, plotHeight)), new Point(x, Y(item.Low, min, max, top, plotHeight)));
                    double y1 = Y(item.Open, min, max, top, plotHeight);
                    double y2 = Y(item.Close, min, max, top, plotHeight);
                    dc.DrawRectangle(brush, null, new Rect(x - bodyWidth / 2, Math.Min(y1, y2), bodyWidth, Math.Max(1, Math.Abs(y2 - y1))));
                }
            }

            int[] labels = { 0, candles.Count / 2, candles.Count - 1 };
            foreach (int index in labels.Distinct())
            {
                string format = candles[index].Time.TimeOfDay == TimeSpan.Zero ? "MM-dd" : "HH:mm";
                var label = new FormattedText(candles[index].Time.ToString(format), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, labelBrush, 1.0);
                double x = left + (index + 0.5) * step - label.Width / 2;
                x = Math.Max(left, Math.Min(left + plotWidth - label.Width, x));
                dc.DrawText(label, new Point(x, height - bottom + 4));
            }
        }

        private static double Y(double value, double min, double max, double top, double height)
        {
            return top + (max - value) / (max - min) * height;
        }
    }

    internal sealed class StockWindow : Window
    {
        private const int HotkeyId = 0x534D;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint VkQ = 0x51;
        private readonly StockStore store = new StockStore();
        private readonly StockDataService service = new StockDataService();
        private readonly StockSettings settings;
        private readonly DispatcherTimer refreshTimer;
        private readonly DispatcherTimer edgeHideTimer;
        private readonly Dictionary<string, StockQuote> quotes = new Dictionary<string, StockQuote>();
        private readonly Dictionary<string, bool> lastBelow = new Dictionary<string, bool>();
        private Border shell;
        private Window detailsWindow;
        private bool movingPrimary;
        private bool movingDetails;
        private bool edgeHideEnabled = true;
        private int hiddenEdge;
        private double revealedLeft;
        private TextBlock compactSymbolText;
        private TextBlock compactNameText;
        private TextBlock compactPriceText;
        private TextBlock compactMetricLabelText;
        private OutlinedValueText compactPremiumText;
        private TextBlock compactUpdatedText;
        private TextBox searchBox;
        private StackPanel tabs;
        private ScrollViewer tabScroll;
        private Button tabLeftButton;
        private Button tabRightButton;
        private TextBlock symbolText;
        private TextBlock nameText;
        private TextBlock priceText;
        private TextBlock changeText;
        private TextBlock detailMetricLabelText;
        private OutlinedValueText premiumText;
        private TextBlock detailInstrumentLabelText;
        private TextBlock iopvText;
        private TextBlock peText;
        private TextBlock percentileText;
        private TextBlock valuationSourceText;
        private TextBlock statusText;
        private Button monitorButton;
        private Button alertButton;
        private Button pinButton;
        private CandleChart chart;
        private WrapPanel periodButtons;
        private WrapPanel rangeButtons;
        private bool permanentlyClosing;
        private bool refreshing;
        private int requestVersion;
        private HwndSource hwndSource;
        public event Action<string, string> AlertRaised;

        public StockWindow()
        {
            settings = store.Load();
            Title = "Little Tools · 股票观察";
            Width = 316;
            Height = 92;
            MinWidth = Width; MaxWidth = Width; MinHeight = Height; MaxHeight = Height;
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.Transparent;
            Topmost = settings.Topmost;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            if (IsOnScreen(settings.Left, settings.Top, Width, Height)) { Left = settings.Left; Top = settings.Top; }
            else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Build();

            SourceInitialized += RegisterGlobalHotkey;
            LocationChanged += delegate { if (hiddenEdge == 0) { settings.Left = Left; settings.Top = Top; SaveSettings(); } PositionDetails(); };
            edgeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            edgeHideTimer.Tick += delegate { edgeHideTimer.Stop(); if (edgeHideEnabled && hiddenEdge != 0 && !IsMouseOver && (detailsWindow == null || !detailsWindow.IsVisible)) HideToEdge(); };
            MouseEnter += delegate { edgeHideTimer.Stop(); shell.Background = HoverBackground(); RevealFromEdge(); };
            MouseLeave += delegate { shell.Background = NormalBackground(); if (edgeHideEnabled && hiddenEdge != 0 && (detailsWindow == null || !detailsWindow.IsVisible)) edgeHideTimer.Start(); };
            MouseLeftButtonDown += PrimaryMouseLeftButtonDown;
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                if (permanentlyClosing) return;
                args.Cancel = true;
                CloseDetails();
                Hide();
            };
            Closed += delegate
            {
                refreshTimer.Stop();
                edgeHideTimer.Stop();
                CloseDetails();
                UnregisterGlobalHotkey();
                service.Dispose();
            };

            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            refreshTimer.Tick += async delegate { await RefreshAllMonitoredAsync(false); };
            refreshTimer.Start();
            Loaded += async delegate { await RefreshAllMonitoredAsync(true); };
        }

        private void Build()
        {
            shell = new Border
            {
                CornerRadius = new CornerRadius(15),
                Background = NormalBackground(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 14, 9),
                Effect = new DropShadowEffect { BlurRadius = 12, Opacity = 0.16, ShadowDepth = 1, Color = Colors.Black }
            };
            Content = shell;
            BuildCompact();
        }

        private void BuildCompact()
        {
            var grid = new Grid { Background = Brushes.Transparent };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var identity = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            compactSymbolText = Text(settings.SelectedCode, 10.5, new SolidColorBrush(Color.FromArgb(175, 255, 255, 255)), FontWeights.SemiBold);
            compactNameText = Text("加载中", 10.5, new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), FontWeights.Normal);
            compactNameText.Margin = new Thickness(7, 0, 0, 0);
            identity.Children.Add(compactSymbolText); identity.Children.Add(compactNameText); top.Children.Add(identity);
            compactPriceText = Text("--", 13, Brushes.White, FontWeights.SemiBold); compactPriceText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(compactPriceText, 1); top.Children.Add(compactPriceText); grid.Children.Add(top);

            var middle = new Grid(); middle.ColumnDefinitions.Add(new ColumnDefinition()); middle.ColumnDefinitions.Add(new ColumnDefinition());
            compactMetricLabelText = Text("参考溢价", 10, new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), FontWeights.Normal);
            middle.Children.Add(compactMetricLabelText);
            compactPremiumText = PremiumValue(12.5); compactPremiumText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(compactPremiumText, 1); middle.Children.Add(compactPremiumText); Grid.SetRow(middle, 1); grid.Children.Add(middle);

            compactUpdatedText = Text("单击查看明细", 9.5, new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), FontWeights.Normal);
            compactUpdatedText.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetRow(compactUpdatedText, 2); grid.Children.Add(compactUpdatedText);
            shell.Child = grid;
            RenderQuote(quotes.ContainsKey(settings.SelectedCode) ? quotes[settings.SelectedCode] : null);
        }

        private Border BuildDetailsContent()
        {
            var root = new Grid { Margin = new Thickness(16, 11, 16, 13) };
            for (int index = 0; index < 8; index++) root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions[0].Height = new GridLength(36);
            root.RowDefinitions[1].Height = new GridLength(43);
            root.RowDefinitions[2].Height = new GridLength(43);
            root.RowDefinitions[3].Height = new GridLength(104);
            root.RowDefinitions[4].Height = new GridLength(34);
            root.RowDefinitions[5].Height = new GridLength(224);
            root.RowDefinitions[6].Height = new GridLength(100);
            root.RowDefinitions[7].Height = new GridLength(43);

            var title = new Grid { Background = Brushes.Transparent };
            title.MouseLeftButtonDown += MoveFromDetails;
            title.Children.Add(Text("股票观察", 14, Brushes.White, FontWeights.SemiBold));
            root.Children.Add(title);

            var search = new Grid { Margin = new Thickness(0, 3, 0, 5) };
            search.ColumnDefinitions.Add(new ColumnDefinition()); search.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            searchBox = InputBox("输入沪深代码"); searchBox.Text = settings.SelectedCode;
            searchBox.KeyDown += async delegate(object sender, KeyEventArgs args) { if (args.Key == Key.Enter) { args.Handled = true; await QueryAsync(); } };
            search.Children.Add(searchBox);
            var query = SmallButton("查询", 68); query.Margin = new Thickness(8, 0, 0, 0); query.Click += async delegate { await QueryAsync(); }; Grid.SetColumn(query, 1); search.Children.Add(query);
            Grid.SetRow(search, 1); root.Children.Add(search);

            var tabStrip = new Grid();
            tabStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
            tabStrip.ColumnDefinitions.Add(new ColumnDefinition());
            tabStrip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
            tabLeftButton = SmallButton("‹", 25);
            tabLeftButton.Margin = new Thickness(0, 0, 4, 0);
            tabLeftButton.Click += delegate { tabScroll.ScrollToHorizontalOffset(Math.Max(0, tabScroll.HorizontalOffset - 170)); };
            tabStrip.Children.Add(tabLeftButton);
            tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            tabScroll = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            tabScroll.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs args)
            {
                if (tabScroll.ScrollableWidth <= 0) return;
                tabScroll.ScrollToHorizontalOffset(tabScroll.HorizontalOffset - args.Delta * 0.45);
                args.Handled = true;
            };
            tabScroll.ScrollChanged += delegate { UpdateTabNavigation(); };
            Grid.SetColumn(tabScroll, 1); tabStrip.Children.Add(tabScroll);
            tabRightButton = SmallButton("›", 25);
            tabRightButton.Margin = new Thickness(4, 0, 0, 0);
            tabRightButton.Click += delegate { tabScroll.ScrollToHorizontalOffset(Math.Min(tabScroll.ScrollableWidth, tabScroll.HorizontalOffset + 170)); };
            Grid.SetColumn(tabRightButton, 2); tabStrip.Children.Add(tabRightButton);
            Grid.SetRow(tabStrip, 2); root.Children.Add(tabStrip);

            var quoteCard = Card(new Thickness(0, 3, 0, 6));
            var quoteGrid = new Grid { Margin = new Thickness(13, 9, 13, 8) };
            quoteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
            quoteGrid.ColumnDefinitions.Add(new ColumnDefinition()); quoteGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var main = new StackPanel();
            var identity = new StackPanel { Orientation = Orientation.Horizontal };
            symbolText = Text(settings.SelectedCode, 10.5, Secondary(), FontWeights.SemiBold);
            nameText = Text("加载中", 10.5, Secondary(), FontWeights.Normal); nameText.Margin = new Thickness(7, 0, 0, 0);
            identity.Children.Add(symbolText); identity.Children.Add(nameText); main.Children.Add(identity);
            var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            priceText = Text("--", 28, Brushes.White, FontWeights.SemiBold);
            changeText = Text("--", 11, Secondary(), FontWeights.SemiBold); changeText.Margin = new Thickness(8, 12, 0, 0);
            priceRow.Children.Add(priceText); priceRow.Children.Add(changeText); main.Children.Add(priceRow); quoteGrid.Children.Add(main);
            var premium = PremiumMetric("参考溢价", out premiumText); detailMetricLabelText = premium.Children[0] as TextBlock; Grid.SetColumn(premium, 1); quoteGrid.Children.Add(premium);
            var iopv = Metric("IOPV", out iopvText); detailInstrumentLabelText = iopv.Children[0] as TextBlock; Grid.SetColumn(iopv, 2); quoteGrid.Children.Add(iopv);
            quoteCard.Child = quoteGrid; Grid.SetRow(quoteCard, 3); root.Children.Add(quoteCard);

            periodButtons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            AddChoice(periodButtons, "分时", "Minute", true); AddChoice(periodButtons, "日K", "Daily", true); AddChoice(periodButtons, "周K", "Weekly", true); AddChoice(periodButtons, "月K", "Monthly", true);
            rangeButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            AddChoice(rangeButtons, "1月", "0", false); AddChoice(rangeButtons, "1年", "1", false); AddChoice(rangeButtons, "3年", "3", false); AddChoice(rangeButtons, "5年", "5", false);
            var choices = new Grid(); choices.ColumnDefinitions.Add(new ColumnDefinition()); choices.ColumnDefinitions.Add(new ColumnDefinition()); choices.Children.Add(periodButtons); Grid.SetColumn(rangeButtons, 1); choices.Children.Add(rangeButtons);
            Grid.SetRow(choices, 4); root.Children.Add(choices);

            chart = new CandleChart { Margin = new Thickness(0, 2, 0, 7) }; Grid.SetRow(chart, 5); root.Children.Add(chart);

            var valuation = Card(new Thickness(0, 0, 0, 7));
            var valuationGrid = new Grid { Margin = new Thickness(13, 9, 10, 8) };
            valuationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) }); valuationGrid.ColumnDefinitions.Add(new ColumnDefinition()); valuationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            var pe = Metric("滚动 PE", out peText); valuationGrid.Children.Add(pe);
            var percentile = new StackPanel(); percentile.Children.Add(Text("历史分位", 9.5, Secondary(), FontWeights.Normal)); percentileText = Text("--", 19, Brushes.White, FontWeights.SemiBold); percentileText.Margin = new Thickness(0, 5, 0, 0); percentile.Children.Add(percentileText);
            valuationSourceText = Text("等待估值数据", 8.5, Secondary(), FontWeights.Normal); valuationSourceText.Margin = new Thickness(0, 3, 0, 0); percentile.Children.Add(valuationSourceText); Grid.SetColumn(percentile, 1); valuationGrid.Children.Add(percentile);
            monitorButton = SmallButton("加入监控", 94); monitorButton.VerticalAlignment = VerticalAlignment.Center; monitorButton.Click += delegate { ToggleMonitor(); }; Grid.SetColumn(monitorButton, 2); valuationGrid.Children.Add(monitorButton);
            valuation.Child = valuationGrid; Grid.SetRow(valuation, 6); root.Children.Add(valuation);

            var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) }); footer.ColumnDefinitions.Add(new ColumnDefinition());
            alertButton = SmallButton("提醒 ≤ 2%", 180); alertButton.HorizontalAlignment = HorizontalAlignment.Left; alertButton.Click += delegate { ConfigureAlert(); }; footer.Children.Add(alertButton);
            pinButton = SmallButton(settings.Topmost ? "置顶 ✓" : "置顶", 65); pinButton.Click += delegate
            {
                settings.Topmost = !settings.Topmost;
                Topmost = settings.Topmost;
                if (detailsWindow != null) detailsWindow.Topmost = settings.Topmost;
                SaveSettings();
                UpdateActionButtons();
            }; Grid.SetColumn(pinButton, 1); footer.Children.Add(pinButton);
            statusText = Text("Ctrl + Alt + Q", 8.8, Secondary(), FontWeights.Normal); statusText.HorizontalAlignment = HorizontalAlignment.Right; statusText.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(statusText, 2); footer.Children.Add(statusText);
            Grid.SetRow(footer, 7); root.Children.Add(footer);

            var detailsShell = new Border
            {
                CornerRadius = new CornerRadius(17),
                Background = new SolidColorBrush(Color.FromArgb(72, 17, 20, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 1, Opacity = 0.16, Color = Colors.Black },
                Child = root
            };
            RenderTabs();
            RenderQuote(quotes.ContainsKey(settings.SelectedCode) ? quotes[settings.SelectedCode] : null);
            UpdateChoiceButtons(); UpdateActionButtons();
            return detailsShell;
        }

        private async Task QueryAsync()
        {
            string code = (searchBox.Text ?? "").Trim();
            if (!StockDataService.IsValidCode(code)) { SetStatus("请输入六位沪深代码"); return; }
            settings.SelectedCode = code; SaveSettings();
            RenderTabs();
            await RefreshSelectedAsync(true);
        }

        private async Task RefreshAllMonitoredAsync(bool includeChart)
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                foreach (StockWatchEntry entry in settings.Watched.ToArray())
                {
                    try
                    {
                        StockQuote quote = await service.GetQuoteAsync(entry.Code);
                        quotes[entry.Code] = quote;
                        CheckAlert(entry, quote);
                    }
                    catch { }
                }
                if (!quotes.ContainsKey(settings.SelectedCode))
                {
                    try { quotes[settings.SelectedCode] = await service.GetQuoteAsync(settings.SelectedCode); } catch (Exception exception) { SetStatus(exception.Message); }
                }
                RenderTabs();
                RenderQuote(quotes.ContainsKey(settings.SelectedCode) ? quotes[settings.SelectedCode] : null);
                if (includeChart && detailsWindow != null && detailsWindow.IsVisible) await RefreshDetailsAsync();
            }
            finally { refreshing = false; }
        }

        private async Task RefreshSelectedAsync(bool details)
        {
            int version = ++requestVersion;
            SetStatus("正在查询…");
            try
            {
                StockQuote quote = await service.GetQuoteAsync(settings.SelectedCode);
                if (version != requestVersion) return;
                quotes[settings.SelectedCode] = quote;
                RenderTabs(); RenderQuote(quote);
                if (details && detailsWindow != null && detailsWindow.IsVisible) await RefreshDetailsAsync();
            }
            catch (Exception exception) { if (version == requestVersion) SetStatus(exception.Message); }
        }

        private async Task RefreshDetailsAsync()
        {
            string code = settings.SelectedCode;
            int version = ++requestVersion;
            StockQuote quote = quotes.ContainsKey(code) ? quotes[code] : null;
            try
            {
                Task<List<Candle>> candlesTask = service.GetCandlesAsync(code, settings.KlinePeriod, settings.RangeYears);
                Task<ValuationInfo> valuationTask = service.GetValuationAsync(code, quote != null && quote.IsEtf, quote == null ? null : quote.Pe,
                    Math.Max(1, settings.RangeYears), quote == null ? null : quote.Name);
                await Task.WhenAll(candlesTask, valuationTask);
                if (version != requestVersion || settings.SelectedCode != code) return;
                chart.SetData(candlesTask.Result);
                RenderValuation(valuationTask.Result);
                SetStatus((quote == null ? "" : "更新于 " + quote.UpdatedAt.ToString("MM-dd HH:mm")) + "  ·  Ctrl + Alt + Q");
            }
            catch (Exception exception)
            {
                if (version == requestVersion) { chart.SetData(null); RenderValuation(null); SetStatus("部分数据暂不可用 · " + exception.Message); }
            }
        }

        private void CheckAlert(StockWatchEntry entry, StockQuote quote)
        {
            if (!entry.AlertEnabled || !quote.IsEtf || !quote.PremiumPercent.HasValue) return;
            bool below = quote.PremiumPercent.Value < entry.PremiumThreshold;
            bool previous;
            bool known = lastBelow.TryGetValue(entry.Code, out previous);
            lastBelow[entry.Code] = below;
            if (below && (!known || !previous))
            {
                Action<string, string> handler = AlertRaised;
                if (handler != null) handler("Little Tools · 溢价提醒",
                    quote.Code + " " + quote.Name + " 当前参考溢价 " + quote.PremiumPercent.Value.ToString("0.00") + "%（阈值 " + entry.PremiumThreshold.ToString("0.##") + "%）");
            }
        }

        private void RenderQuote(StockQuote quote)
        {
            string code = quote == null ? settings.SelectedCode : quote.Code;
            string name = quote == null ? "加载中" : quote.Name;
            string price = quote == null ? "--" : quote.Price.ToString(quote.Price < 10 ? "0.000" : "0.00", CultureInfo.InvariantCulture);
            compactSymbolText.Text = code;
            compactNameText.Text = ShortName(name);
            compactPriceText.Text = price;
            compactPriceText.Foreground = ChangeBrush(quote == null ? 0 : quote.ChangePercent);
            ApplySecurityMetric(compactMetricLabelText, compactPremiumText, quote);
            compactUpdatedText.Text = quote == null ? "单击查看明细" : "更新 " + quote.UpdatedAt.ToString("HH:mm") + "  ·  单击查看明细";
            if (symbolText == null) return;
            symbolText.Text = code;
            nameText.Text = name;
            priceText.Text = price;
            if (changeText != null)
            {
                changeText.Text = quote == null ? "--" : (quote.ChangePercent >= 0 ? "+" : "") + quote.ChangePercent.ToString("0.00") + "%";
                changeText.Foreground = ChangeBrush(quote == null ? 0 : quote.ChangePercent);
                priceText.Foreground = ChangeBrush(quote == null ? 0 : quote.ChangePercent);
            }
            if (premiumText != null)
            {
                ApplySecurityMetric(detailMetricLabelText, premiumText, quote);
            }
            if (iopvText != null)
            {
                bool ordinaryStock = quote != null && !quote.IsEtf;
                if (detailInstrumentLabelText != null) detailInstrumentLabelText.Text = ordinaryStock ? "证券类型" : "IOPV";
                iopvText.Text = ordinaryStock ? "普通股票" : quote != null && quote.Iopv.HasValue ? quote.Iopv.Value.ToString("0.0000") : "--";
            }
            UpdateActionButtons();
        }

        private void RenderValuation(ValuationInfo value)
        {
            if (peText == null) return;
            peText.Text = value != null && value.CurrentPe.HasValue ? value.CurrentPe.Value.ToString("0.00") + "×" : "--";
            percentileText.Text = value != null && value.Percentile.HasValue ? value.Years + "年 · " + value.Percentile.Value.ToString("0") + "%" : "样本不足";
            valuationSourceText.Text = value == null ? "估值数据暂不可用" : value.Source + (value.DataDate.HasValue ? " · " + value.DataDate.Value.ToString("yyyy-MM-dd") : "");
        }

        private void RenderTabs()
        {
            if (tabs == null) return;
            tabs.Children.Clear();
            Button selectedButton = null;
            foreach (StockWatchEntry entry in settings.Watched)
            {
                StockWatchEntry selectedEntry = entry;
                StockQuote quote;
                quotes.TryGetValue(entry.Code, out quote);
                string caption = entry.Code + (quote == null ? "" : "  " + ShortName(quote.Name));
                Button button = SmallButton(caption, Math.Max(100, Math.Min(185, 48 + caption.Length * 8)));
                button.Margin = new Thickness(0, 0, 7, 0);
                button.Background = entry.Code == settings.SelectedCode ? new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(12, 255, 255, 255));
                button.Click += async delegate { settings.SelectedCode = selectedEntry.Code; if (searchBox != null) searchBox.Text = selectedEntry.Code; SaveSettings(); RenderTabs(); RenderQuote(quote); await RefreshSelectedAsync(true); };
                tabs.Children.Add(button);
                if (entry.Code == settings.SelectedCode) selectedButton = button;
            }
            Button buttonToReveal = selectedButton;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                UpdateTabNavigation();
                if (buttonToReveal != null && buttonToReveal.Parent == tabs) buttonToReveal.BringIntoView();
            }));
        }

        private void UpdateTabNavigation()
        {
            if (tabScroll == null || tabLeftButton == null || tabRightButton == null) return;
            bool hasOverflow = tabScroll.ScrollableWidth > 0.5;
            tabLeftButton.IsEnabled = hasOverflow && tabScroll.HorizontalOffset > 0.5;
            tabRightButton.IsEnabled = hasOverflow && tabScroll.HorizontalOffset < tabScroll.ScrollableWidth - 0.5;
            tabLeftButton.Opacity = tabLeftButton.IsEnabled ? 0.9 : 0.28;
            tabRightButton.Opacity = tabRightButton.IsEnabled ? 0.9 : 0.28;
        }

        private void ToggleMonitor()
        {
            StockWatchEntry existing = FindWatch(settings.SelectedCode);
            if (existing == null) settings.Watched.Add(new StockWatchEntry { Code = settings.SelectedCode, AlertEnabled = true, PremiumThreshold = 2.0 });
            else if (settings.Watched.Count > 1) settings.Watched.Remove(existing);
            SaveSettings(); RenderTabs(); UpdateActionButtons();
        }

        private void ConfigureAlert()
        {
            StockWatchEntry entry = FindWatch(settings.SelectedCode);
            if (entry == null) { settings.Watched.Add(entry = new StockWatchEntry { Code = settings.SelectedCode }); }
            entry.AlertEnabled = !entry.AlertEnabled;
            entry.PremiumThreshold = 2.0;
            SaveSettings(); RenderTabs(); UpdateActionButtons();
        }

        private void UpdateActionButtons()
        {
            if (monitorButton == null) return;
            StockWatchEntry entry = FindWatch(settings.SelectedCode);
            monitorButton.Content = entry == null ? "加入监控" : "移出监控";
            StockQuote quote;
            bool supportsPremium = quotes.TryGetValue(settings.SelectedCode, out quote) && quote.IsEtf;
            alertButton.IsEnabled = supportsPremium;
            alertButton.Opacity = supportsPremium ? 1.0 : 0.45;
            alertButton.Content = !supportsPremium ? "溢价提醒不适用" : entry != null && entry.AlertEnabled ? "提醒 < 2%  ✓" : "提醒 < 2%";
            pinButton.Content = settings.Topmost ? "置顶 ✓" : "置顶";
        }

        private StockWatchEntry FindWatch(string code)
        {
            return settings.Watched.FirstOrDefault(delegate(StockWatchEntry item) { return item.Code == code; });
        }

        private void AddChoice(WrapPanel panel, string caption, string value, bool period)
        {
            Button button = SmallButton(caption, 47); button.Tag = value; button.Margin = new Thickness(0, 0, 4, 0);
            button.Click += async delegate
            {
                if (period) settings.KlinePeriod = value; else settings.RangeYears = int.Parse(value, CultureInfo.InvariantCulture);
                SaveSettings(); UpdateChoiceButtons(); await RefreshDetailsAsync();
            };
            panel.Children.Add(button);
        }

        private void UpdateChoiceButtons()
        {
            UpdateChoices(periodButtons, settings.KlinePeriod);
            UpdateChoices(rangeButtons, settings.RangeYears.ToString(CultureInfo.InvariantCulture));
        }

        private static void UpdateChoices(WrapPanel panel, string selected)
        {
            if (panel == null) return;
            foreach (Button button in panel.Children.OfType<Button>())
                button.Background = Convert.ToString(button.Tag) == selected ? new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
        }

        private void PrimaryMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
        {
            if (args.LeftButton != MouseButtonState.Pressed) return;
            RevealFromEdge();
            double originalLeft = Left;
            double originalTop = Top;
            movingPrimary = true;
            try { DragMove(); } catch { }
            finally { movingPrimary = false; }

            bool moved = Math.Abs(Left - originalLeft) > 1 || Math.Abs(Top - originalTop) > 1;
            SnapOrHideAtEdge();
            PositionDetails();
            if (!moved) ToggleDetails();
            args.Handled = true;
        }

        private void ToggleDetails()
        {
            if (detailsWindow != null && detailsWindow.IsVisible)
            {
                CloseDetails();
                return;
            }

            var created = new Window
            {
                Title = "Little Tools · 股票观察明细",
                Width = 470,
                Height = 650,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = settings.Topmost,
                Owner = this,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                Content = BuildDetailsContent()
            };
            detailsWindow = created;
            edgeHideTimer.Stop();
            RevealFromEdge();
            created.Deactivated += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (detailsWindow == created && created.IsVisible && !movingPrimary && !movingDetails && !IsMouseOver && !created.IsMouseOver)
                        CloseDetails();
                }), DispatcherPriority.Background);
            };
            created.Closed += delegate
            {
                if (detailsWindow == created) detailsWindow = null;
                ClearDetailReferences();
            };
            PositionDetails();
            created.Show();
            Dispatcher.BeginInvoke(new Action(async delegate { await RefreshSelectedAsync(true); }));
        }

        private void CloseDetails()
        {
            Window current = detailsWindow;
            detailsWindow = null;
            if (current != null)
            {
                try { current.Close(); } catch { }
            }
            ClearDetailReferences();
            if (edgeHideEnabled && hiddenEdge != 0 && !IsMouseOver) edgeHideTimer.Start();
        }

        private void ClearDetailReferences()
        {
            searchBox = null;
            tabs = null;
            symbolText = null;
            nameText = null;
            priceText = null;
            changeText = null;
            detailMetricLabelText = null;
            premiumText = null;
            detailInstrumentLabelText = null;
            iopvText = null;
            peText = null;
            percentileText = null;
            valuationSourceText = null;
            statusText = null;
            monitorButton = null;
            alertButton = null;
            pinButton = null;
            chart = null;
            periodButtons = null;
            rangeButtons = null;
        }

        private void PositionDetails()
        {
            if (detailsWindow == null) return;
            Rect area = SystemParameters.WorkArea;
            double left = Left + Width - detailsWindow.Width;
            double top = Top + Height + 8;
            detailsWindow.Left = Math.Max(area.Left, Math.Min(area.Right - detailsWindow.Width, left));
            detailsWindow.Top = Math.Max(area.Top, Math.Min(area.Bottom - detailsWindow.Height, top));
        }

        private void MoveFromDetails(object sender, MouseButtonEventArgs args)
        {
            if (args.LeftButton != MouseButtonState.Pressed) return;
            movingDetails = true;
            try
            {
                DragMove();
                SnapOrHideAtEdge();
                PositionDetails();
                if (detailsWindow != null && detailsWindow.IsVisible) detailsWindow.Activate();
            }
            catch { }
            finally { movingDetails = false; }
            args.Handled = true;
        }

        private void RegisterGlobalHotkey(object sender, EventArgs args)
        {
            WindowInteropHelper helper = new WindowInteropHelper(this);
            hwndSource = HwndSource.FromHwnd(helper.Handle);
            if (hwndSource != null) hwndSource.AddHook(WndProc);
            RegisterHotKey(helper.Handle, HotkeyId, ModControl | ModAlt, VkQ);
        }

        private void UnregisterGlobalHotkey()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) UnregisterHotKey(handle, HotkeyId);
            if (hwndSource != null) { hwndSource.RemoveHook(WndProc); hwndSource = null; }
        }

        private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0312 && wParam.ToInt32() == HotkeyId) { ToggleVisibility(); handled = true; }
            return IntPtr.Zero;
        }

        public void ToggleVisibility()
        {
            if (IsVisible) { CloseDetails(); Hide(); }
            else { Show(); WindowState = WindowState.Normal; Activate(); Topmost = settings.Topmost; }
        }

        private void SnapOrHideAtEdge()
        {
            const double snapDistance = 28;
            Rect work = SystemParameters.WorkArea;
            Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
            if (!edgeHideEnabled)
            {
                hiddenEdge = 0;
                Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            }
            else if (Left <= work.Left + snapDistance)
            {
                hiddenEdge = -1; revealedLeft = work.Left;
                if (detailsWindow == null || !detailsWindow.IsVisible) HideToEdge(); else Left = revealedLeft;
            }
            else if (Left + Width >= work.Right - snapDistance)
            {
                hiddenEdge = 1; revealedLeft = work.Right - Width;
                if (detailsWindow == null || !detailsWindow.IsVisible) HideToEdge(); else Left = revealedLeft;
            }
            else
            {
                hiddenEdge = 0; Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            }
        }

        private void RevealFromEdge() { if (hiddenEdge != 0) Left = revealedLeft; }
        private void HideToEdge()
        {
            if (!edgeHideEnabled || (detailsWindow != null && detailsWindow.IsVisible)) return;
            Rect work = SystemParameters.WorkArea;
            Left = hiddenEdge < 0 ? work.Left - Width + 9 : work.Right - 9;
        }

        public void SetEdgeHideEnabled(bool enabled)
        {
            edgeHideEnabled = enabled;
            edgeHideTimer.Stop();
            if (!enabled) { RevealFromEdge(); hiddenEdge = 0; }
        }

        public void ClosePermanently()
        {
            permanentlyClosing = true;
            CloseDetails();
            try { Close(); } catch { }
        }

        private void SaveSettings()
        {
            try { store.Save(settings); } catch { }
        }

        private void SetStatus(string value) { if (statusText != null) statusText.Text = value; }

        private static Border Card(Thickness margin)
        {
            return new Border { CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(33, 255, 255, 255)), BorderThickness = new Thickness(1), Margin = margin };
        }

        private static StackPanel Metric(string label, out TextBlock value)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(Text(label, 9.5, Secondary(), FontWeights.Normal));
            value = Text("--", 19, Brushes.White, FontWeights.SemiBold); value.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(value);
            return stack;
        }

        private static StackPanel PremiumMetric(string label, out OutlinedValueText value)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(Text(label, 9.5, Secondary(), FontWeights.Normal));
            value = PremiumValue(19); value.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(value);
            return stack;
        }

        private static OutlinedValueText PremiumValue(double size)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(0.52, 0.5),
                SpreadMethod = GradientSpreadMethod.Repeat
            };
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(89, 210, 255), 0));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(104, 156, 255), 0.18));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(190, 154, 255), 0.36));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(255, 112, 174), 0.55));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(255, 198, 92), 0.76));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(124, 237, 174), 1));
            return new OutlinedValueText
            {
                Text = "--", FontFamily = new FontFamily("Segoe UI"), FontSize = size, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, OutlineBrush = gradient, OutlineThickness = 0.75,
                HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static void ApplyPremiumStyle(OutlinedValueText target, double? value)
        {
            target.Text = value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%" : "不适用";
            target.OutlineEnabled = value.HasValue && value.Value < 2;
            target.Foreground = target.OutlineEnabled ? Brushes.White : new SolidColorBrush(Color.FromRgb(165, 170, 180));
            target.InvalidateVisual();
        }

        private static void ApplySecurityMetric(TextBlock label, OutlinedValueText target, StockQuote quote)
        {
            if (quote != null && !quote.IsEtf)
            {
                if (label != null) label.Text = "滚动 PE";
                target.Text = quote.Pe.HasValue ? quote.Pe.Value.ToString("0.00", CultureInfo.InvariantCulture) + "×" : "--";
                target.OutlineEnabled = false;
                target.Foreground = quote.Pe.HasValue ? Brushes.White : new SolidColorBrush(Color.FromRgb(165, 170, 180));
                target.InvalidateVisual();
                return;
            }

            if (label != null) label.Text = "参考溢价";
            ApplyPremiumStyle(target, quote == null ? null : quote.PremiumPercent);
        }

        internal static TextBox InputBox(string hint)
        {
            var input = new TextBox
            {
                Height = 34, Padding = new Thickness(10, 5, 10, 5), FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                Foreground = Brushes.White, CaretBrush = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), BorderThickness = new Thickness(1), ToolTip = hint
            };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            host.SetValue(ScrollViewer.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(host);
            input.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = border };
            return input;
        }

        internal static Button SmallButton(string text, double width)
        {
            var button = new Button
            {
                Content = text, Width = width, Height = 29, FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5,
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 1, 5, 2), Cursor = Cursors.Arrow
            };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetValue(ContentPresenter.ContentTemplateProperty, new System.Windows.Data.Binding("ContentTemplate") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetValue(ContentPresenter.ContentStringFormatProperty, new System.Windows.Data.Binding("ContentStringFormat") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.MarginProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetValue(TextElement.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetValue(TextElement.FontFamilyProperty, new System.Windows.Data.Binding("FontFamily") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetValue(TextElement.FontSizeProperty, new System.Windows.Data.Binding("FontSize") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(presenter);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            return button;
        }

        private static TextBlock Text(string text, double size, Brush brush, FontWeight weight)
        {
            return new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, Foreground = brush, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };
        }

        private static Brush NormalBackground() { return new SolidColorBrush(Color.FromArgb(62, 17, 20, 27)); }
        private static Brush HoverBackground() { return new SolidColorBrush(Color.FromArgb(112, 17, 20, 27)); }
        private static Brush Secondary() { return new SolidColorBrush(Color.FromArgb(145, 255, 255, 255)); }
        private static Brush ChangeBrush(double value) { return value > 0 ? new SolidColorBrush(Color.FromRgb(239, 88, 91)) : value < 0 ? new SolidColorBrush(Color.FromRgb(124, 237, 174)) : Brushes.White; }
        private static string ShortName(string value) { return string.IsNullOrWhiteSpace(value) ? "" : value.Replace("华泰柏瑞", "").Replace("博时", ""); }

        private static bool IsOnScreen(double left, double top, double width, double height)
        {
            if (double.IsNaN(left) || double.IsNaN(top)) return false;
            foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            {
                System.Drawing.Rectangle area = screen.WorkingArea;
                if (left + width > area.Left && left < area.Right && top + height > area.Top && top < area.Bottom) return true;
            }
            return false;
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr handle, int id);
    }
}
