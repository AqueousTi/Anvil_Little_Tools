using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace LittleTools.Assistant.Todo;

/// <summary>Shared shell for the todo dialogs: dark rounded card with a draggable header.</summary>
internal abstract class TodoDialogWindow : Window
{
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("Auto,*") };
    private readonly bool _showHeader;

    protected TodoDialogWindow(string title, double width, double height,
        IBrush? background = null, double cornerRadius = 15, IBrush? border = null,
        Thickness? padding = null, double headerHeight = 44, bool showHeader = true,
        BoxShadows? shadow = null)
    {
        _showHeader = showHeader;
        Width = width;
        Height = height;
        Title = title;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = headerHeight, Margin = new Thickness(0, 0, 0, 0) };
        var label = TodoTheme.Label(title, 13, TodoTheme.PrimaryText, bold: true);
        header.Children.Add(label);
        var close = TodoTheme.TextButton("×", 12, 29);
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            try { BeginMoveDrag(args); } catch { }
        };
        if (showHeader)
        {
            Grid.SetRow(header, 0);
            _root.Children.Add(header);
        }

        Content = new Border
        {
            Child = _root,
            CornerRadius = new CornerRadius(cornerRadius),
            Background = background ?? TodoTheme.DialogBackground,
            BorderBrush = border ?? TodoTheme.DialogBorder,
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(16, 13, 16, 13),
            BoxShadow = shadow ?? new BoxShadows(new BoxShadow { Blur = 24, OffsetY = 5, Color = Color.FromArgb(71, 0, 0, 0) })
        };
    }

    protected void SetBody(Control content)
    {
        Grid.SetRow(content, _showHeader ? 1 : 0);
        _root.Children.Add(content);
    }
}

/// <summary>
/// The 0-100 minute focus dial, ported from the Windows dialog: a nearly opaque
/// shell, a thick red arc outside the ticks, an outward pointer and the value in
/// large type in the middle. Starting keeps the dialog open and shows the
/// remaining time live; turning the dial stops the countdown and lets the user
/// pick a new duration.
/// </summary>
internal sealed class FocusDialWindow : TodoDialogWindow
{
    private readonly FocusDialControl _dial;
    private readonly TextBlock _hint;
    // Both handlers reference each other, so the fields are initialised up front.
    private Button _start = null!;
    private Button _stop = null!;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Func<double>? _remaining;
    private readonly Action<int>? _startTimer;
    private readonly Action? _stopTimer;

    public FocusDialWindow(string itemText, int initialMinutes, bool active, ITodoSoundService? sound = null,
        Func<double>? remainingSeconds = null, Action<int>? startTimer = null, Action? stopTimer = null)
        : base("专注于：" + (string.IsNullOrWhiteSpace(itemText) ? "当前事项" : itemText.Trim()),
            390, 475, TodoTheme.DialBackground, 18, TodoTheme.DialogBorder, new Thickness(18, 15, 18, 16), 52)
    {
        _remaining = remainingSeconds;
        _startTimer = startTimer;
        _stopTimer = stopTimer;

        _dial = new FocusDialControl(initialMinutes, sound)
        {
            Width = 310,
            Height = 310,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (active && remainingSeconds is not null)
        {
            _dial.CountdownSeconds = remainingSeconds();
            _dial.CountdownActive = true;
        }

        _hint = TodoTheme.Label(active ? "倒计时进行中 · 拨动刻度可重新设置" : "顺时针拨动刻度，选择本次专注时长",
            9.5, TodoTheme.SecondaryText);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.VerticalAlignment = VerticalAlignment.Bottom;
        _hint.Margin = new Thickness(0, 0, 0, 1);
        _hint.IsHitTestVisible = false;

        var dialArea = new Grid();
        dialArea.Children.Add(_dial);
        dialArea.Children.Add(_hint);

        _stop = TodoTheme.TextButton("结束当前计时", 11.5, 104);
        _stop.Height = 34;
        _stop.Margin = new Thickness(0, 6, 0, 0);
        _stop.IsVisible = active;
        _stop.Click += (_, _) =>
        {
            _stopTimer?.Invoke();
            _countdown.Stop();
            _dial.CountdownActive = false;
            _hint.Text = "顺时针拨动刻度，选择本次专注时长";
            _start.Content = "开始专注";
            _stop.IsVisible = false;
        };

        _start = TodoTheme.TextButton(active ? "重新开始" : "开始专注", 11.5, 104);
        _start.Height = 34;
        _start.IsEnabled = initialMinutes > 0;
        _start.Click += (_, _) =>
        {
            if (_dial.Minutes <= 0 || _startTimer is null) return;
            _startTimer(_dial.Minutes);
            _dial.CountdownSeconds = _remaining is null ? _dial.Minutes * 60.0 : _remaining();
            _dial.CountdownActive = true;
            _hint.Text = "倒计时进行中 · 拨动刻度可重新设置";
            _start.Content = "重新开始";
            _stop.IsVisible = true;
            _countdown.Start();
        };

        _dial.MinutesChanged += value =>
        {
            if (_dial.CountdownActive)
            {
                _dial.CountdownActive = false;
                _hint.Text = "顺时针拨动刻度，选择新的专注时长";
                _countdown.Stop();
            }
            _start.IsEnabled = value > 0;
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(_start);
        actions.Children.Add(_stop);

        var grid = new Grid { RowDefinitions = new RowDefinitions("*,72") };
        Grid.SetRow(dialArea, 0);
        grid.Children.Add(dialArea);
        Grid.SetRow(actions, 1);
        grid.Children.Add(actions);
        SetBody(grid);

        _countdown.Tick += (_, _) =>
        {
            if (!_dial.CountdownActive || _remaining is null)
            {
                _countdown.Stop();
                return;
            }
            var seconds = _remaining();
            _dial.CountdownSeconds = seconds;
            if (seconds > 0) return;
            _countdown.Stop();
            Close(false);
        };
        if (_dial.CountdownActive) _countdown.Start();
        Closed += (_, _) => _countdown.Stop();
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Close(false);
        };
    }
}

/// <summary>Draws the dial exactly like the Windows FocusTimerDial.</summary>
internal sealed class FocusDialControl : Control
{
    private readonly ITodoSoundService? _sound;
    private int _minutes;
    private bool _countdownActive;
    private double _countdownSeconds;

    public FocusDialControl(int minutes, ITodoSoundService? sound)
    {
        _sound = sound;
        _minutes = Math.Clamp(minutes, FocusTimerLimits.MinimumMinutes, FocusTimerLimits.MaximumMinutes);
    }

    public event Action<int>? MinutesChanged;

    public int Minutes
    {
        get => _minutes;
        private set
        {
            var clamped = Math.Clamp(value, FocusTimerLimits.MinimumMinutes, FocusTimerLimits.MaximumMinutes);
            if (_minutes == clamped) return;
            _minutes = clamped;
            InvalidateVisual();
            _sound?.Tick();
            MinutesChanged?.Invoke(clamped);
        }
    }

    public bool CountdownActive
    {
        get => _countdownActive;
        set
        {
            if (_countdownActive == value) return;
            _countdownActive = value;
            InvalidateVisual();
        }
    }

    public double CountdownSeconds
    {
        get => _countdownSeconds;
        set
        {
            _countdownSeconds = value;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        Update(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured != this) return;
        Update(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void Update(Point point) =>
        Minutes = FocusTimerMath.AngleToMinutes(point.X, point.Y, Bounds.Width, Bounds.Height, _minutes);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 20) return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var tickOuter = size / 2 - 23;
        var red = TodoTheme.DialRed;

        // Thick arc just outside the ticks; a full circle at 100 minutes.
        if (_minutes == 100)
        {
            context.DrawEllipse(null, new Pen(red, 8, lineCap: PenLineCap.Round), centre, tickOuter + 1, tickOuter + 1);
        }
        else if (_minutes > 1)
        {
            const double endpointInsetMinutes = 0.7;
            var startAngle = -90 + endpointInsetMinutes * 3.6;
            var sweep = Math.Max(0, (_minutes - endpointInsetMinutes * 2) * 3.6);
            context.DrawGeometry(null, new Pen(red, 8, lineCap: PenLineCap.Round),
                BuildArc(centre, tickOuter + 1, startAngle, sweep));
        }

        for (var index = 0; index < 100; index++)
        {
            var isMajor = index % 5 == 0;
            var selected = _minutes == 100 || index <= _minutes;
            var inner = tickOuter - (isMajor ? 17 : 10);
            var angle = (-90 + index * 3.6) * Math.PI / 180.0;
            var from = new Point(centre.X + Math.Cos(angle) * inner, centre.Y + Math.Sin(angle) * inner);
            var to = new Point(centre.X + Math.Cos(angle) * tickOuter, centre.Y + Math.Sin(angle) * tickOuter);
            context.DrawLine(
                new Pen(selected ? red : isMajor ? TodoTheme.DialTickMajor : TodoTheme.DialTickMinor,
                    isMajor ? 2.2 : 1.25),
                from, to);
        }

        // The pointer sits outside the ring, like the Windows dial.
        var pointerAngle = (-90 + (_minutes == 100 ? 360 : _minutes * 3.6)) * Math.PI / 180.0;
        var pointerFrom = new Point(centre.X + Math.Cos(pointerAngle) * (tickOuter + 3),
            centre.Y + Math.Sin(pointerAngle) * (tickOuter + 3));
        var pointerTo = new Point(centre.X + Math.Cos(pointerAngle) * (tickOuter + 22),
            centre.Y + Math.Sin(pointerAngle) * (tickOuter + 22));
        context.DrawLine(new Pen(red, 2, lineCap: PenLineCap.Round), pointerFrom, pointerTo);
        context.DrawEllipse(red, null, pointerTo, 4.2, 4.2);

        DrawValue(context, centre);
    }

    private void DrawValue(DrawingContext context, Point centre)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
        var typeface = new Typeface(TodoTheme.UiFont, FontStyle.Normal, FontWeight.Light);
        var totalSeconds = Math.Max(0, (int)Math.Ceiling(_countdownSeconds));
        var minutes = _countdownActive ? totalSeconds / 60 : _minutes;

        var value = new FormattedText(minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            culture, FlowDirection.LeftToRight, typeface, 58, TodoTheme.PrimaryText);
        FormattedText? seconds = _countdownActive
            ? new FormattedText(":" + (totalSeconds % 60).ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                culture, FlowDirection.LeftToRight, typeface, 24, TodoTheme.SecondaryText)
            : null;

        var totalWidth = value.Width + (seconds?.Width ?? 0);
        var left = centre.X - totalWidth / 2;
        var baseline = centre.Y - value.Height / 2;
        context.DrawText(value, new Point(left, baseline));
        if (seconds is not null)
            context.DrawText(seconds, new Point(left + value.Width, baseline + value.Height - seconds.Height - 6));
    }

    private static StreamGeometry BuildArc(Point centre, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        using var sink = geometry.Open();
        var start = startDegrees * Math.PI / 180.0;
        var end = (startDegrees + sweepDegrees) * Math.PI / 180.0;
        sink.BeginFigure(new Point(centre.X + Math.Cos(start) * radius, centre.Y + Math.Sin(start) * radius), false);
        sink.ArcTo(
            new Point(centre.X + Math.Cos(end) * radius, centre.Y + Math.Sin(end) * radius),
            new Size(radius, radius), 0, sweepDegrees > 180, SweepDirection.Clockwise);
        sink.EndFigure(false);
        return geometry;
    }
}

/// <summary>Manages the recurring rules. Returns true when anything changed.</summary>
internal sealed class RecurringRulesWindow : TodoDialogWindow
{
    private readonly List<RecurringTodoRule> _rules;
    private readonly ITodoClock _clock;
    private readonly ITodoIdGenerator _ids;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBox _input;
    private readonly ComboBox _frequency;
    private readonly ComboBox _schedule;

    public bool Changed { get; private set; }

    public List<string> ApplyTodayRuleIds { get; } = [];

    public RecurringRulesWindow(List<RecurringTodoRule> rules, ITodoClock clock, ITodoIdGenerator ids)
        : base("周期性任务", 450, 500, TodoTheme.DialogBackground, 15,
            new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), new Thickness(17), showHeader: false,
            shadow: new BoxShadows(new BoxShadow { Blur = 16, Color = Color.FromArgb(56, 0, 0, 0) }))
    {
        _rules = rules;
        _clock = clock;
        _ids = ids;

        var title = new StackPanel();
        title.Children.Add(TodoTheme.Label("周期性任务", 14, TodoTheme.PrimaryText, bold: true));
        var subtitle = TodoTheme.Label("每天、每周或每月自动放入当天待办 · 修改只影响以后生成的事项", 9.5, TodoTheme.SecondaryText);
        subtitle.Margin = new Thickness(0, 4, 0, 0);
        title.Children.Add(subtitle);

        _input = TodoTheme.InputBox("固定事项内容");

        _frequency = DarkComboBox();
        foreach (var text in new[] { "每天", "每周", "每月" }) _frequency.Items.Add(DarkItem(text));
        _frequency.SelectedIndex = 0;
        _frequency.SelectionChanged += (_, _) => RebuildScheduleOptions();

        _schedule = DarkComboBox();
        _schedule.Margin = new Thickness(7, 0, 7, 0);

        var add = TodoTheme.TextButton("添加", 10, 66);
        add.Click += (_, _) => AddRule();

        var scheduleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("114,*,74"), Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetColumn(_frequency, 0);
        scheduleRow.Children.Add(_frequency);
        Grid.SetColumn(_schedule, 1);
        scheduleRow.Children.Add(_schedule);
        Grid.SetColumn(add, 2);
        scheduleRow.Children.Add(add);

        var form = new Grid { RowDefinitions = new RowDefinitions("38,38") };
        Grid.SetRow(_input, 0);
        form.Children.Add(_input);
        Grid.SetRow(scheduleRow, 1);
        form.Children.Add(scheduleRow);

        _list.Margin = new Thickness(0, 7, 0, 4);
        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var hint = TodoTheme.Label("关闭规则后，已经生成的事项会保留", 9.5, TodoTheme.SecondaryText);
        hint.VerticalAlignment = VerticalAlignment.Center;
        var done = TodoTheme.TextButton("完成", 10, 68);
        done.Click += (_, _) =>
        {
            Changed = true;
            Close(true);
        };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,76"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(hint, 0);
        footer.Children.Add(hint);
        Grid.SetColumn(done, 1);
        footer.Children.Add(done);

        var grid = new Grid { RowDefinitions = new RowDefinitions("57,84,*,39") };
        Grid.SetRow(title, 0);
        grid.Children.Add(title);
        Grid.SetRow(form, 1);
        grid.Children.Add(form);
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);
        SetBody(grid);

        // Windows DarkComboItemStyle: white text, a 6px radius and the highlighted
        // and selected surfaces.
        Styles.Add(new Style(x => x.OfType<ComboBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.ForegroundProperty, TodoTheme.PrimaryText),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(9, 7, 9, 7)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6))
            }
        });
        Styles.Add(new Style(x => x.OfType<ComboBoxItem>().Class(":pointerover"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromArgb(62, 255, 255, 255))) }
        });
        Styles.Add(new Style(x => x.OfType<ComboBoxItem>().Class(":selected"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromArgb(46, 255, 255, 255))) }
        });

        RebuildScheduleOptions();
        RenderRules();
        _input.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            AddRule();
            args.Handled = true;
        };
    }

    /// <summary>Windows DarkComboBox: 30 tall, dark surface, corner 7.</summary>
    private static ComboBox DarkComboBox()
    {
        var combo = new ComboBox
        {
            Height = 30,
            FontFamily = TodoTheme.UiFont,
            FontSize = 10.5,
            Foreground = TodoTheme.PrimaryText,
            Background = new SolidColorBrush(Color.FromArgb(252, 27, 30, 37)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 2, 30, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        combo.Resources["ComboBoxDropDownBackground"] = new SolidColorBrush(Color.FromArgb(255, 24, 27, 34));
        combo.Resources["ComboBoxDropDownBorderBrush"] = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        return combo;
    }

    private static ComboBoxItem DarkItem(string text) => new()
    {
        Content = text,
        FontFamily = TodoTheme.UiFont,
        FontSize = 10.5,
        Foreground = TodoTheme.PrimaryText,
        Background = Brushes.Transparent,
        Padding = new Thickness(8, 5, 8, 5)
    };

    private void RebuildScheduleOptions()
    {
        _schedule.Items.Clear();
        if (_frequency.SelectedIndex == 1)
        {
            // Weekly uses the DayOfWeek value, matching the Windows data model.
            foreach (var day in new[] { 1, 2, 3, 4, 5, 6, 0 })
                _schedule.Items.Add(DarkItem("每周" + RecurringTodoEngine.WeekdayName(day)));
        }
        else if (_frequency.SelectedIndex == 2)
        {
            for (var day = 1; day <= 31; day++) _schedule.Items.Add(DarkItem("每月 " + day + " 日"));
        }
        else
        {
            _schedule.Items.Add(DarkItem("每天自动生成"));
            _schedule.IsEnabled = false;
            _schedule.SelectedIndex = 0;
            return;
        }
        _schedule.IsEnabled = true;
        _schedule.SelectedIndex = 0;
    }

    private void AddRule()
    {
        var text = (_input.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            _input.Focus();
            return;
        }
        var frequency = _frequency.SelectedIndex switch
        {
            1 => RecurringTodoEngine.Weekly,
            2 => RecurringTodoEngine.Monthly,
            _ => RecurringTodoEngine.Daily
        };
        var scheduleValue = frequency switch
        {
            RecurringTodoEngine.Weekly => new[] { 1, 2, 3, 4, 5, 6, 0 }[Math.Max(0, _schedule.SelectedIndex)],
            RecurringTodoEngine.Monthly => Math.Max(0, _schedule.SelectedIndex) + 1,
            _ => 0
        };
        var rule = new RecurringTodoRule
        {
            Id = _ids.NewId(),
            Text = text,
            Frequency = frequency,
            ScheduleValue = scheduleValue,
            Enabled = true,
            CreatedDate = TodoLogic.DateKey(_clock.Today)
        };
        _rules.Add(rule);
        ApplyTodayRuleIds.Add(rule.Id);
        Changed = true;
        _input.Text = string.Empty;
        RenderRules();
        _input.Focus();
    }

    private void RenderRules()
    {
        _list.Children.Clear();
        if (_rules.Count == 0)
        {
            var empty = TodoTheme.Label("还没有周期性任务", 11, TodoTheme.SecondaryText);
            empty.Margin = new Thickness(8, 18, 0, 0);
            _list.Children.Add(empty);
            return;
        }

        foreach (var rule in _rules.ToArray())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("29,*,90,30") };

            var enabled = new LightCheckBox(rule.Enabled);
            enabled.Changed += value =>
            {
                rule.Enabled = value;
                if (value) ApplyTodayRuleIds.Add(rule.Id!);
                Changed = true;
            };
            Grid.SetColumn(enabled, 0);
            row.Children.Add(enabled);

            var ruleText = TodoTheme.InputBox("修改固定事项文字");
            ruleText.Text = rule.Text ?? string.Empty;
            ruleText.Height = 30;
            ruleText.Padding = new Thickness(7, 3, 7, 3);
            ruleText.TextChanged += (_, _) =>
            {
                rule.Text = (ruleText.Text ?? string.Empty).Trim();
                ApplyTodayRuleIds.Add(rule.Id!);
                Changed = true;
            };
            Grid.SetColumn(ruleText, 1);
            row.Children.Add(ruleText);

            var description = TodoTheme.Label(RecurringTodoEngine.Describe(rule), 9.5, TodoTheme.SecondaryText);
            description.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(description, 2);
            row.Children.Add(description);

            var delete = TodoTheme.TextButton("×", 12, 25);
            delete.Height = 25;
            delete.VerticalAlignment = VerticalAlignment.Center;
            delete.Click += (_, _) =>
            {
                _rules.Remove(rule);
                Changed = true;
                RenderRules();
            };
            Grid.SetColumn(delete, 3);
            row.Children.Add(delete);

            _list.Children.Add(new Border
            {
                Child = row,
                CornerRadius = new CornerRadius(10),
                Background = TodoTheme.ListCardBackground,
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 7, 7, 7),
                Margin = new Thickness(0, 0, 0, 7)
            });
        }
    }

}

/// <summary>A compact month calendar used to jump to any date.</summary>
internal sealed class DateChooserWindow : TodoDialogWindow
{
    private DateTime _month;
    private readonly Grid _grid = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*") };
    private readonly TextBlock _monthLabel = TodoTheme.Label(string.Empty, 12, TodoTheme.PrimaryText, bold: true);

    /// <summary>
    /// The picked date, or null when the picker was dismissed. Windows exposes the
    /// same <c>SelectedDate</c> because the picker is a plain owned window, not a
    /// modal dialog.
    /// </summary>
    public DateTime? SelectedDate { get; private set; }

    public DateChooserWindow(DateTime current)
        : base("选择日期", 300, 285, TodoTheme.DateBackground, 14,
            new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), new Thickness(12), showHeader: false,
            shadow: new BoxShadows(new BoxShadow { Blur = 14, Color = Color.FromArgb(51, 0, 0, 0) }))
    {
        // Windows DateChooserWindow L3682: closes the picker as soon as it loses
        // focus. The picker must be shown with Show(owner), not ShowDialog: a modal
        // owner is disabled and swallows the click, so the picker never sees a
        // deactivation and a click next to it left it open.
        Deactivated += (_, _) =>
        {
            if (IsVisible && SelectedDate is null) Close();
        };
        _month = new DateTime(current.Year, current.Month, 1);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        var previous = TodoTheme.IconButton(TodoIcons.Chevron(true, size: 10), 24);
        previous.Click += (_, _) => { _month = _month.AddMonths(-1); Render(); };
        header.Children.Add(previous);
        _monthLabel.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(_monthLabel, 1);
        header.Children.Add(_monthLabel);
        var next = TodoTheme.IconButton(TodoIcons.Chevron(false, size: 10), 24);
        next.Click += (_, _) => { _month = _month.AddMonths(1); Render(); };
        Grid.SetColumn(next, 2);
        header.Children.Add(next);

        var weekdays = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*") };
        var names = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var index = 0; index < names.Length; index++)
        {
            var label = TodoTheme.Label(names[index], 9.5, TodoTheme.MutedText);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(label, index);
            weekdays.Children.Add(label);
        }

        var today = TodoTheme.TextButton("回到今天", 10, 80);
        today.Height = 26;
        today.Click += (_, _) => Pick(DateTime.Today);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        footer.Children.Add(today);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(header);
        stack.Children.Add(weekdays);
        stack.Children.Add(_grid);
        stack.Children.Add(footer);
        SetBody(stack);
        Render();
    }

    /// <summary>Remembers the choice and closes; <see cref="SelectedDate"/> carries it back.</summary>
    private void Pick(DateTime date)
    {
        SelectedDate = date;
        Close();
    }

    private void Render()
    {
        _grid.Children.Clear();
        _monthLabel.Text = _month.ToString("yyyy 年 M 月", System.Globalization.CultureInfo.InvariantCulture);
        var offset = ((int)_month.DayOfWeek + 6) % 7; // Monday first
        var days = DateTime.DaysInMonth(_month.Year, _month.Month);
        for (var day = 1; day <= days; day++)
        {
            var date = new DateTime(_month.Year, _month.Month, day);
            var cell = TodoTheme.TextButton(day.ToString(), 10.5, 32);
            cell.Height = 28;
            cell.Margin = new Thickness(1);
            if (date == DateTime.Today)
            {
                cell.Background = TodoTheme.AccentSoft;
                cell.Foreground = TodoTheme.Accent;
            }
            cell.Click += (_, _) => Pick(date);
            Grid.SetRow(cell, (offset + day - 1) / 7);
            Grid.SetColumn(cell, (offset + day - 1) % 7);
            _grid.Children.Add(cell);
        }
    }
}

internal enum ImportAction
{
    Ignore,
    AddToToday,
    AddToBacklog
}

/// <summary>Asks what to do with yesterday's unfinished items.</summary>
internal sealed class ImportWindow : TodoDialogWindow
{
    private readonly List<DailyTodoItem> _candidates;
    private readonly Dictionary<string, LightCheckBox> _boxes = new(StringComparer.Ordinal);
    private readonly StackPanel _list = new() { Spacing = 4 };

    public List<DailyTodoItem> SelectedItems { get; private set; } = [];

    public ImportAction SelectedAction { get; private set; } = ImportAction.Ignore;

    public ImportWindow(List<DailyTodoItem> candidates)
        : base("处理昨日未完成事项", 400, Math.Min(480, 185 + candidates.Count * 43),
            TodoTheme.DialogBackground, 15, new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            new Thickness(17), showHeader: false,
            shadow: new BoxShadows(new BoxShadow { Blur = 16, Color = Color.FromArgb(56, 0, 0, 0) }))
    {
        _candidates = candidates;

        var title = new StackPanel();
        var heading = TodoTheme.Label("处理昨日未完成事项", 14, TodoTheme.PrimaryText, bold: true);
        title.Children.Add(heading);
        var subtitle = TodoTheme.Label("勾选事项，再选择加入今日、堆积或忽略", 9.5, TodoTheme.SecondaryText);
        subtitle.Margin = new Thickness(0, 3, 0, 0);
        title.Children.Add(subtitle);

        var rows = new StackPanel();
        foreach (var item in candidates)
        {
            var check = new LightCheckBox(true);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Height = 38 };
            Grid.SetColumn(check, 0);
            row.Children.Add(check);
            var label = TodoTheme.Label(item.Text ?? string.Empty, 11.5, TodoTheme.PrimaryText);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            _boxes[item.Id!] = check;
            rows.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        buttons.ColumnDefinitions = new ColumnDefinitions("*,70,86,76");
        var selectAll = TodoTheme.TextButton("全选", 10, 58);
        selectAll.HorizontalAlignment = HorizontalAlignment.Left;
        selectAll.Click += (_, _) => SetAll(true);
        Grid.SetColumn(selectAll, 0);
        buttons.Children.Add(selectAll);

        var ignore = TodoTheme.TextButton("忽略", 10, 62);
        ignore.Click += (_, _) => Commit(ImportAction.Ignore);
        Grid.SetColumn(ignore, 1);
        buttons.Children.Add(ignore);

        var backlog = TodoTheme.TextButton("堆积", 10, 78);
        backlog.Click += (_, _) => Commit(ImportAction.AddToBacklog);
        Grid.SetColumn(backlog, 2);
        buttons.Children.Add(backlog);

        var today = TodoTheme.TextButton("加入今日", 10, 68);
        today.Click += (_, _) => Commit(ImportAction.AddToToday);
        Grid.SetColumn(today, 3);
        buttons.Children.Add(today);

        var grid = new Grid { RowDefinitions = new RowDefinitions("48,*,39") };
        Grid.SetRow(title, 0);
        grid.Children.Add(title);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        SetBody(grid);
    }

    private void SetAll(bool value)
    {
        foreach (var box in _boxes.Values) box.SetChecked(value);
    }

    private void Commit(ImportAction action)
    {
        SelectedAction = action;
        SelectedItems = _candidates.Where(item => _boxes[item.Id!].IsChecked).ToList();
        Close(true);
    }
}
