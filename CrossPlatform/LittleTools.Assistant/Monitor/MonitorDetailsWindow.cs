using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// The expanded detail panel, ported from the Windows <c>DetailsWindow</c>
/// (AIUsageMonitor/Program.cs L1851-L2228): the same 380x505 frame that shrinks
/// with the enabled providers (L2118-L2119), the same four sections and their
/// collapse indices (L2113-L2117), the same day/week/month and DS/GLM switches
/// (L2091-L2108) and the same self drawn trend chart with its y and x axis labels
/// (L2179-L2227).
/// </summary>
internal sealed class MonitorDetailsWindow : Window
{
    private readonly MonitorWindow _owner;
    private readonly MonitorModule _module;
    private readonly StackPanel _content = new();
    private readonly TextBlock _codexValue;
    private readonly TextBlock _codexMeta;
    private readonly TextBlock _deepSeekValue;
    private readonly TextBlock _deepSeekMeta;
    private readonly TextBlock _todayDeepSeek;
    private readonly TextBlock _glmValue;
    private readonly TextBlock _glmMeta;
    private readonly TextBlock _todayGlm;
    private readonly TextBlock _chartMaximum;
    private readonly TextBlock _chartMinimum;
    private readonly TextBlock _chartStart;
    private readonly TextBlock _chartEnd;
    private readonly TextBlock _trendTitle;
    private readonly TextBlock _updated;
    private readonly UsageChart _chart = new();
    private readonly Button _dayRange;
    private readonly Button _weekRange;
    private readonly Button _monthRange;
    private readonly Button _deepSeekTrend;
    private readonly Button _glmTrend;

    private MonitorTrendRange _selectedRange = MonitorTrendRange.Day;
    private MonitorTrendProvider _selectedProvider = MonitorTrendProvider.DeepSeek;
    private Point _logicalPosition;
    private bool _positioned;

    internal event Action? RefreshRequested;
    internal event Action? SettingsRequested;
    internal event Action? ExitRequested;

    internal bool IsMoving { get; private set; }

    public MonitorDetailsWindow(MonitorWindow owner, MonitorModule module)
    {
        _owner = owner;
        _module = module;
        Title = "Little Tools · AI 余量监控明细";
        Width = MonitorLayout.DetailsWidth;
        Height = MonitorLayout.DetailsHeight;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = GlassSurface.TransparencyLevels;
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var panel = _content;

        // Child 0: the header. Windows AI USAGE, 11pt semibold, ARGB(150).
        panel.Children.Add(MonitorTheme.Label("AI USAGE", 11, MonitorTheme.SectionLabel, bold: true));
        _content.Children[0].Margin = new Thickness(0, 0, 0, 13);

        // Children 1-4: the CODEX block.
        panel.Children.Add(SectionLabel("CODEX"));
        _codexValue = Value();
        panel.Children.Add(_codexValue);
        _codexMeta = Meta();
        panel.Children.Add(_codexMeta);
        panel.Children.Add(Separator());

        // Children 5-8: the DeepSeek block, with today's estimate on the right.
        var deepSeekHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        deepSeekHeader.Children.Add(SectionLabel("DEEPSEEK"));
        _todayDeepSeek = TodayLabel(MonitorTheme.DeepSeekColor);
        Grid.SetColumn(_todayDeepSeek, 1);
        deepSeekHeader.Children.Add(_todayDeepSeek);
        panel.Children.Add(deepSeekHeader);
        _deepSeekValue = Value();
        panel.Children.Add(_deepSeekValue);
        _deepSeekMeta = Meta();
        panel.Children.Add(_deepSeekMeta);
        panel.Children.Add(Separator());

        // Children 9-12: the GLM block. Windows uses 15pt for its value.
        var glmHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        glmHeader.Children.Add(SectionLabel("GLM · 国内智谱"));
        _todayGlm = TodayLabel(MonitorTheme.GlmColor);
        Grid.SetColumn(_todayGlm, 1);
        glmHeader.Children.Add(_todayGlm);
        panel.Children.Add(glmHeader);
        _glmValue = Value();
        _glmValue.FontSize = 15;
        panel.Children.Add(_glmValue);
        _glmMeta = Meta();
        _glmMeta.Text = "读取 GLM 账户余额与 Coding Plan 套餐配额";
        panel.Children.Add(_glmMeta);
        panel.Children.Add(Separator());

        // Children 13-15: the trend header, chart and separator.
        var trendHeader = new Grid { Margin = new Thickness(0, 0, 0, 3), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _trendTitle = SectionLabel("今日余额减少趋势");
        trendHeader.Children.Add(_trendTitle);
        var rangeButtons = new StackPanel { Orientation = Orientation.Horizontal };
        _deepSeekTrend = MonitorTheme.RangeButton("DS", 30);
        _glmTrend = MonitorTheme.RangeButton("GLM", 38);
        _dayRange = MonitorTheme.RangeButton("日", 30);
        _weekRange = MonitorTheme.RangeButton("周", 30);
        _monthRange = MonitorTheme.RangeButton("月", 30);
        _deepSeekTrend.Click += (_, _) => SelectProvider(MonitorTrendProvider.DeepSeek);
        _glmTrend.Click += (_, _) => SelectProvider(MonitorTrendProvider.Glm);
        _dayRange.Click += (_, _) => SelectRange(MonitorTrendRange.Day);
        _weekRange.Click += (_, _) => SelectRange(MonitorTrendRange.Week);
        _monthRange.Click += (_, _) => SelectRange(MonitorTrendRange.Month);
        rangeButtons.Children.Add(_deepSeekTrend);
        rangeButtons.Children.Add(_glmTrend);
        rangeButtons.Children.Add(_dayRange);
        rangeButtons.Children.Add(_weekRange);
        rangeButtons.Children.Add(_monthRange);
        Grid.SetColumn(rangeButtons, 1);
        trendHeader.Children.Add(rangeButtons);
        panel.Children.Add(trendHeader);

        // Windows chart frame (Program.cs L1980-L1997): a 48 wide axis column, an
        // 82 tall plot and a 21 tall x axis row.
        var chartFrame = new Grid { Height = 103, Margin = new Thickness(0, 1, 0, 0) };
        chartFrame.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(48)));
        chartFrame.ColumnDefinitions.Add(new ColumnDefinition());
        chartFrame.RowDefinitions.Add(new RowDefinition(new GridLength(82)));
        chartFrame.RowDefinitions.Add(new RowDefinition(new GridLength(21)));
        var yAxis = new Grid();
        _chartMaximum = AxisText();
        _chartMaximum.VerticalAlignment = VerticalAlignment.Top;
        yAxis.Children.Add(_chartMaximum);
        _chartMinimum = AxisText();
        _chartMinimum.VerticalAlignment = VerticalAlignment.Bottom;
        yAxis.Children.Add(_chartMinimum);
        chartFrame.Children.Add(yAxis);
        Grid.SetColumn(_chart, 1);
        chartFrame.Children.Add(_chart);
        var xAxis = new Grid();
        _chartStart = AxisText();
        _chartStart.HorizontalAlignment = HorizontalAlignment.Left;
        xAxis.Children.Add(_chartStart);
        _chartEnd = AxisText();
        _chartEnd.HorizontalAlignment = HorizontalAlignment.Right;
        xAxis.Children.Add(_chartEnd);
        Grid.SetColumn(xAxis, 1);
        Grid.SetRow(xAxis, 1);
        chartFrame.Children.Add(xAxis);
        panel.Children.Add(chartFrame);
        panel.Children.Add(Separator());

        // Child 16: the footer with the update time and the three buttons.
        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,68,76,52")
        };
        _updated = Meta();
        _updated.VerticalAlignment = VerticalAlignment.Center;
        buttons.Children.Add(_updated);
        var settings = MonitorTheme.SmallButton("设置", 62);
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        Grid.SetColumn(settings, 1);
        buttons.Children.Add(settings);
        var refresh = MonitorTheme.SmallButton("刷新", 70);
        refresh.Margin = new Thickness(6, 0, 0, 0);
        refresh.Click += async (_, _) => RefreshRequested?.Invoke();
        Grid.SetColumn(refresh, 2);
        buttons.Children.Add(refresh);
        var exit = MonitorTheme.SmallButton("退出", 46);
        exit.Margin = new Thickness(6, 0, 0, 0);
        exit.Click += (_, _) => ExitRequested?.Invoke();
        Grid.SetColumn(exit, 3);
        buttons.Children.Add(exit);
        panel.Children.Add(buttons);

        _chart.Clock = () => _module.Now;
        Content = new Border
        {
            CornerRadius = new CornerRadius(MonitorLayout.DetailsCornerRadius),
            Background = MonitorTheme.DetailsBackground,
            BorderBrush = MonitorTheme.DetailsBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(19, 16, 19, 16),
            BoxShadow = MonitorTheme.DetailsShadow,
            Child = panel
        };

        // Windows DetailsWindow drags the HUD from anywhere on its surface
        // (Program.cs L2022-L2026).
        PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            MoveFromDetails(args);
        };
        UpdateRangeButtons();
    }

    internal UsageChart ChartForSmoke => _chart;

    internal int SectionCount => _content.Children.Count;

    internal MonitorTrendRange SelectedRange => _selectedRange;

    internal MonitorTrendProvider SelectedProvider => _selectedProvider;

    /// <summary>Everything the detail window shows, so the render smoke can assert it.</summary>
    internal string DescribeDetailsForSmoke()
    {
        var sections = string.Concat(_content.Children.Select(child => child.IsVisible ? "1" : "0"));
        return "codex=" + _codexValue.Text + "|" + _codexMeta.Text
            + ";deepseek=" + _deepSeekValue.Text + "|" + _deepSeekMeta.Text + "|" + _todayDeepSeek.Text
            + ";glm=" + _glmValue.Text + "|" + _glmMeta.Text + "|" + _todayGlm.Text
            + ";trend=" + _trendTitle.Text
            + ";axis=" + _chartMaximum.Text + "," + _chartMinimum.Text + "," + _chartStart.Text + "," + _chartEnd.Text
            + ";updated=" + _updated.Text
            + ";range=" + _selectedRange + ";provider=" + _selectedProvider
            + ";sections=" + sections
            + ";chart=" + (_chart.LastRender?.ToString() ?? "no-render")
            + ";windowHeight=" + Height.ToString("0.#");
    }

    /// <summary>Windows DetailsWindow static factory helpers (Program.cs L2030-L2065).</summary>
    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontFamily = MonitorTheme.UiFontSemibold,
        FontSize = 10.5,
        Foreground = MonitorTheme.SectionLabel,
        Margin = new Thickness(0, 0, 0, 3)
    };

    private static TextBlock Value() => new()
    {
        Text = "正在连接",
        FontFamily = MonitorTheme.UiFontSemibold,
        FontSize = 20,
        Foreground = MonitorTheme.PrimaryText
    };

    private static TextBlock Meta() => new()
    {
        FontFamily = MonitorTheme.UiFont,
        FontSize = 10.5,
        Foreground = MonitorTheme.MetaText,
        Margin = new Thickness(0, 3, 0, 0),
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static TextBlock AxisText() => new()
    {
        FontFamily = MonitorTheme.UiFont,
        FontSize = 9,
        Foreground = MonitorTheme.AxisText,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock TodayLabel(Color color) => new()
    {
        Text = "今日 --",
        FontFamily = MonitorTheme.UiFontSemibold,
        FontSize = 10.5,
        Foreground = new SolidColorBrush(color),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Border Separator() => new()
    {
        Height = 1,
        Background = MonitorTheme.SeparatorBrush,
        Margin = new Thickness(0, 14, 0, 13)
    };

    // ------------------------------------------------------------- switches

    /// <summary>Windows SelectRange (Program.cs L2077-L2082).</summary>
    internal void SelectRange(MonitorTrendRange range)
    {
        _selectedRange = range;
        UpdateRangeButtons();
        UpdateTrend();
    }

    /// <summary>Windows SelectProvider (Program.cs L2084-L2089).</summary>
    internal void SelectProvider(MonitorTrendProvider provider)
    {
        _selectedProvider = provider;
        UpdateRangeButtons();
        UpdateTrend();
    }

    /// <summary>Windows UpdateRangeButtons (Program.cs L2091-L2108).</summary>
    private void UpdateRangeButtons()
    {
        var ranges = new[] { _dayRange, _weekRange, _monthRange };
        for (var index = 0; index < ranges.Length; index++)
        {
            var selected = (int)_selectedRange == index;
            ranges[index].Background = selected ? MonitorTheme.DeepSeekSelected : MonitorTheme.ChoiceIdle;
            ranges[index].BorderBrush = selected ? MonitorTheme.DeepSeekSelectedBorder : MonitorTheme.ChoiceIdleBorder;
        }
        var deepSeek = _selectedProvider == MonitorTrendProvider.DeepSeek;
        _deepSeekTrend.Background = deepSeek ? MonitorTheme.DeepSeekSelected : MonitorTheme.ChoiceIdle;
        _deepSeekTrend.BorderBrush = deepSeek ? MonitorTheme.DeepSeekSelectedBorder : MonitorTheme.ChoiceIdleBorder;
        _glmTrend.Background = deepSeek ? MonitorTheme.ChoiceIdle : MonitorTheme.GlmSelected;
        _glmTrend.BorderBrush = deepSeek ? MonitorTheme.ChoiceIdleBorder : MonitorTheme.GlmSelectedBorder;
    }

    // -------------------------------------------------------------- rendering

    /// <summary>Windows DetailsWindow.UpdateView (Program.cs L2110-L2164).</summary>
    internal void UpdateView(UsageSnapshot state)
    {
        // Windows SetSectionVisibility(1,4) / (5,8) / (9,12) / (13,15).
        SetSectionVisibility(1, 4, state.CodexEnabled);
        SetSectionVisibility(5, 8, state.DeepSeekEnabled);
        SetSectionVisibility(9, 12, state.GlmEnabled);
        SetSectionVisibility(13, 15, MonitorLayout.DetailsSections(
            state.CodexEnabled, state.DeepSeekEnabled, state.GlmEnabled)[3]);
        Height = MonitorLayout.DetailsWindowHeight(state.CodexEnabled, state.DeepSeekEnabled, state.GlmEnabled);

        if (_selectedProvider == MonitorTrendProvider.DeepSeek && !state.DeepSeekEnabled && state.GlmEnabled)
            _selectedProvider = MonitorTrendProvider.Glm;
        if (_selectedProvider == MonitorTrendProvider.Glm && !state.GlmEnabled && state.DeepSeekEnabled)
            _selectedProvider = MonitorTrendProvider.DeepSeek;
        _deepSeekTrend.IsVisible = state.DeepSeekEnabled;
        _glmTrend.IsVisible = state.GlmEnabled;
        UpdateRangeButtons();

        _codexValue.Text = MonitorText.CodexDetailValue(state);
        _codexMeta.Text = MonitorText.CodexDetailMeta(state);
        _deepSeekValue.Text = MonitorText.DeepSeekDetailValue(state);
        _deepSeekMeta.Text = MonitorText.DeepSeekDetailMeta(state);
        _todayDeepSeek.Text = MonitorText.TodaySpend(state.TodayDeepSeekSpend, state.DeepSeekTrackingStart,
            state.DeepSeekCurrencySymbol ?? "¥", _module.Now);
        _glmValue.Text = MonitorText.GlmDetailValue(state);
        _glmMeta.Text = MonitorText.GlmDetailMeta(state);
        _todayGlm.Text = MonitorText.TodaySpend(state.TodayGlmSpend, state.GlmTrackingStart,
            state.GlmCurrencySymbol ?? "¥", _module.Now);
        _updated.Text = MonitorText.DetailsUpdated(state);
        // The Windows meta lines are single lines; the wider Linux font can overflow
        // them, so they ellipsise and carry the full text as a tooltip.
        ToolTip.SetTip(_codexMeta, _codexMeta.Text);
        ToolTip.SetTip(_deepSeekMeta, _deepSeekMeta.Text);
        ToolTip.SetTip(_glmMeta, _glmMeta.Text);
        UpdateTrend();

        // The window grows and shrinks with the providers, so a reposition is needed.
        PositionBesideOwner();
    }

    private void SetSectionVisibility(int first, int last, bool visible)
    {
        for (var index = first; index <= last && index < _content.Children.Count; index++)
            _content.Children[index].IsVisible = visible;
    }

    /// <summary>Windows DetailsWindow.UpdateTrend (Program.cs L2179-L2227).</summary>
    internal void UpdateTrend()
    {
        var state = _module.State;
        var now = _module.Now;
        var (today, weekStart, monthStart) = MonitorUsageTracker.Boundaries(now);
        List<UsagePoint> points;
        double? spend;
        DateTime rangeStart;
        DateTime? trackingStart;
        var glm = _selectedProvider == MonitorTrendProvider.Glm;
        switch (_selectedRange)
        {
            case MonitorTrendRange.Week:
                rangeStart = weekStart;
                points = glm ? state.GlmWeekPoints : state.DeepSeekWeekPoints;
                spend = glm ? state.WeeklyGlmSpend : state.WeeklyDeepSeekSpend;
                trackingStart = glm ? state.GlmWeekTrackingStart : state.DeepSeekWeekTrackingStart;
                break;
            case MonitorTrendRange.Month:
                rangeStart = monthStart;
                points = glm ? state.GlmMonthPoints : state.DeepSeekMonthPoints;
                spend = glm ? state.MonthlyGlmSpend : state.MonthlyDeepSeekSpend;
                trackingStart = glm ? state.GlmMonthTrackingStart : state.DeepSeekMonthTrackingStart;
                break;
            default:
                rangeStart = today;
                points = glm ? state.GlmTodayPoints : state.DeepSeekTodayPoints;
                spend = glm ? state.TodayGlmSpend : state.TodayDeepSeekSpend;
                trackingStart = glm ? state.GlmTrackingStart : state.DeepSeekTrackingStart;
                break;
        }
        _ = spend;

        var partial = trackingStart.HasValue && trackingStart.Value > rangeStart.AddMinutes(1);
        _trendTitle.Text = MonitorText.TrendTitle(glm, partial, _selectedRange);
        var currency = glm ? (state.GlmCurrencySymbol ?? "¥") : (state.DeepSeekCurrencySymbol ?? "¥");
        _chart.SetData(points, rangeStart, MonitorTheme.SeriesColor(glm));

        double maximum = 0;
        foreach (var point in points) maximum = Math.Max(maximum, point.Value);
        _chartMaximum.Text = MonitorText.ChartMaximum(currency, maximum);
        _chartMinimum.Text = MonitorText.ChartMinimum(currency);
        var visibleStart = trackingStart ?? rangeStart;
        var day = _selectedRange == MonitorTrendRange.Day;
        _chartStart.Text = MonitorText.ChartStart(day, visibleStart);
        _chartEnd.Text = MonitorText.ChartEnd(day, now);
    }

    // ------------------------------------------------------------ positioning

    /// <summary>Windows MoveFromDetails (Program.cs L1734-L1746): the title drags the HUD.</summary>
    private void MoveFromDetails(PointerPressedEventArgs args)
    {
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
            // A window manager may refuse the move; the HUD keeps its position.
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

    internal Point LogicalPosition => _logicalPosition;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_positioned) PositionBesideOwner();
    }
}
