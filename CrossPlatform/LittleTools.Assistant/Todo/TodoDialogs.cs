using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;

namespace LittleTools.Assistant.Todo;

/// <summary>Shared shell for the todo dialogs: dark rounded card with a draggable header.</summary>
internal abstract class TodoDialogWindow : Window
{
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("Auto,*") };

    protected TodoDialogWindow(string title, double width, double height)
    {
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

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        var label = TodoTheme.Label(title, 13, TodoTheme.PrimaryText, bold: true);
        header.Children.Add(label);
        var close = TodoTheme.IconButton(TodoIcons.Cross(size: 10), 24);
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            try { BeginMoveDrag(args); } catch { }
        };
        Grid.SetRow(header, 0);
        _root.Children.Add(header);

        Content = new Border
        {
            Child = _root,
            CornerRadius = new CornerRadius(TodoTheme.ShellCornerRadius),
            Background = TodoTheme.ShellBackground,
            BorderBrush = TodoTheme.ShellBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 13, 16, 13)
        };
    }

    protected void SetBody(Control content)
    {
        Grid.SetRow(content, 1);
        _root.Children.Add(content);
    }
}

internal sealed record FocusDialOutcome(bool Stop, int? Minutes);

/// <summary>
/// The 0-100 minute focus dial. Selection snaps to five minute detents and clicks
/// a short sound on every change, like the Windows dial.
/// </summary>
internal sealed class FocusDialWindow : TodoDialogWindow
{
    private readonly FocusDialControl _dial;
    private readonly TextBlock _summary;
    private readonly Button _start;
    private readonly Button _stop;
    private readonly bool _running;

    public FocusDialWindow(string itemText, int minutes, bool running, ITodoSoundService? sound = null)
        : base("专注倒计时", 390, 470)
    {
        _running = running;
        _dial = new FocusDialControl(Math.Clamp(minutes, FocusTimerLimits.MinimumMinutes, FocusTimerLimits.MaximumMinutes), sound);
        _summary = TodoTheme.Label(string.Empty, 12, TodoTheme.SecondaryText);
        _summary.HorizontalAlignment = HorizontalAlignment.Center;

        _start = TodoTheme.TextButton(running ? "重新开始" : "开始专注", 11.5, 110);
        _start.Height = 30;
        _start.Click += (_, _) => Close(new FocusDialOutcome(false, _dial.Minutes));

        _stop = TodoTheme.TextButton("结束当前计时", 11.5, 110);
        _stop.Height = 30;
        _stop.IsVisible = running;
        _stop.Click += (_, _) => Close(new FocusDialOutcome(true, null));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        buttons.Children.Add(_start);
        buttons.Children.Add(_stop);

        var stack = new StackPanel { Spacing = 6 };
        var caption = TodoTheme.Label(itemText, 11, TodoTheme.MutedText);
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(caption);
        _dial.Width = 300;
        _dial.Height = 300;
        _dial.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(_dial);
        stack.Children.Add(_summary);
        stack.Children.Add(buttons);
        SetBody(stack);

        _dial.MinutesChanged += UpdateSummary;
        UpdateSummary(_dial.Minutes);
    }

    private void UpdateSummary(int minutes)
    {
        _summary.Text = minutes <= 0
            ? "顺时针拨动刻度，选择新的专注时长"
            : $"专注 {minutes} 分钟";
        _start.Content = _running ? "重新开始" : "开始专注";
    }
}

/// <summary>Draws the dial: 100 minute ticks, the selected arc and the pointer.</summary>
internal sealed class FocusDialControl : Control
{
    private readonly ITodoSoundService? _sound;
    private int _minutes;

    public FocusDialControl(int minutes, ITodoSoundService? sound)
    {
        _sound = sound;
        _minutes = minutes;
    }

    public event Action<int>? MinutesChanged;

    public int Minutes
    {
        get => _minutes;
        private set
        {
            if (_minutes == value) return;
            _minutes = value;
            InvalidateVisual();
            _sound?.Tick();
            MinutesChanged?.Invoke(value);
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
        var radius = size / 2 - 6;

        context.DrawEllipse(null, new Pen(TodoTheme.ControlBorder, 1.2), centre, radius, radius);

        for (var minute = 0; minute <= FocusTimerLimits.MaximumMinutes; minute++)
        {
            var angle = minute / (double)FocusTimerLimits.MaximumMinutes * Math.PI * 2;
            var major = minute % 5 == 0;
            var outer = radius;
            var inner = radius - (major ? 11 : 6);
            var sin = Math.Sin(angle);
            var cos = Math.Cos(angle);
            var brush = minute <= _minutes ? TodoTheme.Danger : TodoTheme.ControlBorder;
            context.DrawLine(
                new Pen(brush, major ? 1.6 : 1),
                new Point(centre.X + sin * inner, centre.Y - cos * inner),
                new Point(centre.X + sin * outer, centre.Y - cos * outer));
        }

        if (_minutes > 0)
        {
            var sweep = _minutes / (double)FocusTimerLimits.MaximumMinutes * Math.PI * 2;
            var arc = new StreamGeometry();
            using (var sink = arc.Open())
            {
                sink.BeginFigure(new Point(centre.X, centre.Y - radius + 16), false);
                sink.ArcTo(
                    new Point(centre.X + (radius - 16) * Math.Sin(sweep), centre.Y - (radius - 16) * Math.Cos(sweep)),
                    new Size(radius - 16, radius - 16), 0, sweep > Math.PI, SweepDirection.Clockwise);
                sink.EndFigure(false);
            }
            context.DrawGeometry(null, new Pen(TodoTheme.Danger, 2.4, lineCap: PenLineCap.Round), arc);
        }

        var pointerAngle = _minutes / (double)FocusTimerLimits.MaximumMinutes * Math.PI * 2;
        var pointerEnd = new Point(centre.X + (radius - 22) * Math.Sin(pointerAngle),
            centre.Y - (radius - 22) * Math.Cos(pointerAngle));
        context.DrawLine(new Pen(TodoTheme.PrimaryText, 1.6), centre, pointerEnd);
        context.DrawEllipse(TodoTheme.Danger, null, pointerEnd, 4, 4);
        context.DrawEllipse(null, new Pen(TodoTheme.SecondaryText, 1.2), centre, 3.5, 3.5);
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
        : base("周期性任务", 450, 500)
    {
        _rules = rules;
        _clock = clock;
        _ids = ids;

        _input = new TextBox
        {
            PlaceholderText = "任务内容…",
            FontSize = 12.5,
            FontFamily = TodoTheme.UiFont,
            Background = TodoTheme.ControlBackground,
            Foreground = TodoTheme.PrimaryText,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 4)
        };
        _frequency = new ComboBox { FontSize = 11, Width = 78, SelectedIndex = 0 };
        _frequency.Items.Add("每天");
        _frequency.Items.Add("每周");
        _frequency.Items.Add("每月");
        _frequency.SelectionChanged += (_, _) => UpdateScheduleItems();

        _schedule = new ComboBox { FontSize = 11, Width = 88, SelectedIndex = 0 };
        UpdateScheduleItems();

        var add = TodoTheme.TextButton("添加", 11, 56);
        add.Height = 28;
        add.Click += (_, _) => AddRule();
        _input.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            AddRule();
            args.Handled = true;
        };

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _input.Width = 150;
        addRow.Children.Add(_input);
        addRow.Children.Add(_frequency);
        addRow.Children.Add(_schedule);
        addRow.Children.Add(add);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var hint = TodoTheme.Label("关闭规则后，已经生成的事项会保留；修改只影响以后生成的事项。", 9.5, TodoTheme.MutedText);
        hint.TextWrapping = TextWrapping.Wrap;
        var done = TodoTheme.TextButton("完成", 11, 72);
        done.Height = 28;
        done.Click += (_, _) =>
        {
            Changed = true;
            Close(true);
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8 };
        Grid.SetRow(addRow, 0);
        grid.Children.Add(addRow);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Grid.SetRow(hint, 2);
        grid.Children.Add(hint);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(done);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);
        SetBody(grid);

        RenderRules();
    }

    private void UpdateScheduleItems()
    {
        _schedule.Items.Clear();
        if (_frequency.SelectedIndex == 1)
        {
            // Weekly uses the DayOfWeek value, matching the Windows data model.
            foreach (var day in new[] { 1, 2, 3, 4, 5, 6, 0 }) _schedule.Items.Add("周" + RecurringTodoEngine.WeekdayName(day));
        }
        else if (_frequency.SelectedIndex == 2)
        {
            for (var day = 1; day <= 31; day++) _schedule.Items.Add(day + " 日");
        }
        else
        {
            _schedule.Items.Add("每天");
            _schedule.IsEnabled = false;
        }
        if (_frequency.SelectedIndex != 0) _schedule.IsEnabled = true;
        _schedule.SelectedIndex = 0;
    }

    private void AddRule()
    {
        var text = (_input.Text ?? string.Empty).Trim();
        if (text.Length == 0) return;
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
    }

    private void RenderRules()
    {
        _list.Children.Clear();
        if (_rules.Count == 0)
        {
            _list.Children.Add(TodoTheme.Label("还没有周期性任务", 11, TodoTheme.MutedText));
            return;
        }

        foreach (var rule in _rules.ToArray())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6 };
            var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center };
            enabled.IsCheckedChanged += (_, _) =>
            {
                rule.Enabled = enabled.IsChecked == true;
                if (rule.Enabled) ApplyTodayRuleIds.Add(rule.Id!);
                Changed = true;
            };
            Grid.SetColumn(enabled, 0);
            row.Children.Add(enabled);

            var stack = new StackPanel { Spacing = 1 };
            var text = new TextBox
            {
                Text = rule.Text,
                FontSize = 12,
                FontFamily = TodoTheme.UiFont,
                Foreground = TodoTheme.PrimaryText,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                FontWeight = FontWeight.SemiBold
            };
            text.TextChanged += (_, _) =>
            {
                rule.Text = text.Text ?? string.Empty;
                Changed = true;
            };
            stack.Children.Add(text);
            stack.Children.Add(TodoTheme.Label(RecurringTodoEngine.Describe(rule), 9.5, TodoTheme.MutedText));
            Grid.SetColumn(stack, 1);
            row.Children.Add(stack);

            var delete = TodoTheme.IconButton(TodoIcons.Trash(size: 12), 24);
            delete.Click += (_, _) =>
            {
                _rules.Remove(rule);
                Changed = true;
                RenderRules();
            };
            Grid.SetColumn(delete, 3);
            row.Children.Add(delete);

            var card = new Border
            {
                Child = row,
                CornerRadius = new CornerRadius(10),
                Background = TodoTheme.ControlBackground,
                Padding = new Thickness(6, 5, 6, 5)
            };
            _list.Children.Add(card);
        }
    }
}

/// <summary>A compact month calendar used to jump to any date.</summary>
internal sealed class DateChooserWindow : TodoDialogWindow
{
    private DateTime _month;
    private readonly Grid _grid = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*") };
    private readonly TextBlock _monthLabel = TodoTheme.Label(string.Empty, 12, TodoTheme.PrimaryText, bold: true);

    public DateChooserWindow(DateTime current) : base("选择日期", 320, 320)
    {
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
        today.Click += (_, _) => Close(DateTime.Today);
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
            cell.Click += (_, _) => Close(date);
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
    private readonly Dictionary<string, CheckBox> _boxes = new(StringComparer.Ordinal);
    private readonly StackPanel _list = new() { Spacing = 4 };

    public List<DailyTodoItem> SelectedItems { get; private set; } = [];

    public ImportAction SelectedAction { get; private set; } = ImportAction.Ignore;

    public ImportWindow(List<DailyTodoItem> candidates)
        : base("处理昨日未完成事项", 400, Math.Min(480, 205 + candidates.Count * 43))
    {
        _candidates = candidates;
        var subtitle = TodoTheme.Label("勾选事项，再选择加入今日、堆积或忽略", 10, TodoTheme.MutedText);

        foreach (var item in candidates)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Height = 38 };
            var box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            _boxes[item.Id!] = box;
            Grid.SetColumn(box, 0);
            row.Children.Add(box);
            var text = TodoTheme.Label(item.Text ?? string.Empty, 12, TodoTheme.PrimaryText);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            _list.Children.Add(row);
        }

        var all = TodoTheme.TextButton("全选", 10, 52);
        all.Height = 26;
        all.Click += (_, _) => SetAll(true);
        var none = TodoTheme.TextButton("都不选", 10, 62);
        none.Height = 26;
        none.Click += (_, _) => SetAll(false);
        var ignore = TodoTheme.TextButton("忽略", 10, 56);
        ignore.Height = 26;
        ignore.Click += (_, _) =>
        {
            SelectedAction = ImportAction.Ignore;
            Close(true);
        };
        var backlog = TodoTheme.TextButton("堆积", 10, 56);
        backlog.Height = 26;
        backlog.Click += (_, _) => Commit(ImportAction.AddToBacklog);
        var today = TodoTheme.TextButton("加入今日", 10, 76);
        today.Height = 26;
        today.Click += (_, _) => Commit(ImportAction.AddToToday);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(all);
        footer.Children.Add(none);
        footer.Children.Add(ignore);
        footer.Children.Add(backlog);
        footer.Children.Add(today);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        Grid.SetRow(subtitle, 0);
        grid.Children.Add(subtitle);
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        SetBody(grid);
    }

    private void SetAll(bool value)
    {
        foreach (var box in _boxes.Values) box.IsChecked = value;
    }

    private void Commit(ImportAction action)
    {
        SelectedAction = action;
        SelectedItems = _candidates.Where(item => _boxes[item.Id!].IsChecked == true).ToList();
        Close(true);
    }
}
