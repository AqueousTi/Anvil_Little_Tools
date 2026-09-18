using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// The daily todo window: a compact "current item" capsule that expands into the
/// stacked card deck. Behaviour follows the Windows module, including the drag
/// ordering, the remembered position of completed items, the backlog drawer and
/// the focus countdown.
/// </summary>
internal sealed class TodoWindow : Window
{
    private readonly TodoStore _store;
    private readonly DailyTodoData _data;
    private readonly ITodoClock _clock;
    private readonly ITodoIdGenerator _ids;
    private readonly ITodoSoundService _sound;

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _edgeHideTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private readonly DispatcherTimer _recurringTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _focusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private readonly Border _shell;
    private readonly Grid _root;
    private readonly Grid _compactView;
    private readonly Grid _expandedView;
    private readonly Border _backlogHost;
    private readonly Canvas _cardCanvas = new();
    private ScrollViewer _cardScroll = null!;
    private readonly StackPanel _backlogList = new();
    private readonly Grid _backlogFooter;
    private readonly Button _importButton;
    private readonly TextBlock _compactItem;
    private readonly TextBlock _compactProgress;
    private readonly TextBlock _dateLabel;
    private readonly TextBlock _progressLabel;
    private readonly TextBox _addInput;
    private readonly StackPanel _undoNotice;
    private TextBlock _backlogTitle = null!;
    private readonly TextBlock _undoLabel;

    private readonly Dictionary<string, Control> _cards = new(StringComparer.Ordinal);
    private readonly List<BacklogUndoEntry> _undoEntries = [];
    private readonly List<string> _selectedBacklogIds = [];

    private DateTime _viewedDate;
    private DateTime _observedToday;
    private bool _expanded;
    private bool _multiSelect;
    private bool _dialogOpen;
    private bool _cardAnimating;
    private bool _compactCompleting;
    private bool _edgeHideEnabled = true;
    private bool _movingWindow;
    private int _hiddenEdge;
    private bool _positioned;
    private BacklogTabWindow? _backlogTab;
    private double _lastFocusSoundSecond = -1;
    private DateTime _interactionAt = DateTime.MinValue;

    internal event Action<string>? FocusFinished;

    public TodoWindow(TodoStore store, DailyTodoData data, ITodoClock clock, ITodoIdGenerator ids,
        ITodoSoundService sound, bool edgeHideEnabled)
    {
        _store = store;
        _data = data;
        _clock = clock;
        _ids = ids;
        _sound = sound;
        _edgeHideEnabled = edgeHideEnabled;
        _viewedDate = _clock.Today;
        _observedToday = _clock.Today;

        Title = "Little Tools · 每日待办";
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = TodoTheme.CompactWidth;
        Height = TodoTheme.CompactHeight;
        // Manual placement must be set before the window is mapped, otherwise the
        // window manager decides the position and the bottom-right anchor is lost.
        WindowStartupLocation = WindowStartupLocation.Manual;

        _compactItem = TodoTheme.Label("暂无待办", 14, TodoTheme.PrimaryText, bold: true);
        _compactItem.TextTrimming = TextTrimming.CharacterEllipsis;
        _compactItem.Margin = new Thickness(0, 0, 4, 0);
        _compactProgress = TodoTheme.Label("今日完成 0 / 0", 9.5, TodoTheme.MutedText);
        _compactProgress.HorizontalAlignment = HorizontalAlignment.Right;
        _compactProgress.VerticalAlignment = VerticalAlignment.Bottom;

        _dateLabel = TodoTheme.Label("今天", 12, TodoTheme.PrimaryText, bold: true);
        _dateLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _progressLabel = TodoTheme.Label("已完成 0 / 全部 0", 10, TodoTheme.SecondaryText);
        _addInput = new TextBox
        {
            PlaceholderText = "添加待办事项…",
            FontSize = 13,
            FontFamily = TodoTheme.UiFont,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = TodoTheme.PrimaryText,
            Padding = new Thickness(4, 2),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _importButton = TodoTheme.TextButton("处理昨日事项", 10, 88);

        _compactView = BuildCompactView();
        _expandedView = BuildExpandedView();

        _backlogFooter = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 6 };
        _undoNotice = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false };
        _undoLabel = TodoTheme.Label(string.Empty, 10, TodoTheme.SecondaryText);
        _undoNotice.Children.Add(_undoLabel);
        _backlogFooter.Children.Add(_undoNotice);

        _backlogHost = BuildBacklogHost();
        _backlogHost.IsVisible = false;

        _root = new Grid();
        _root.Children.Add(_compactView);
        _root.Children.Add(_expandedView);
        _root.Children.Add(_backlogHost);

        _shell = new Border
        {
            Child = _root,
            CornerRadius = new CornerRadius(TodoTheme.ShellCornerRadius),
            Background = TodoTheme.ShellBackground,
            BorderBrush = TodoTheme.ShellBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 10)
        };
        Content = _shell;

        _shell.PointerEntered += (_, _) =>
        {
            _shell.Background = TodoTheme.ShellBackgroundHover;
            _edgeHideTimer.Stop();
            RevealFromEdge();
        };
        _shell.PointerExited += (_, _) =>
        {
            _shell.Background = TodoTheme.ShellBackground;
            if (_edgeHideEnabled && !_expanded && _hiddenEdge != 0 && !IsPointerOver) _edgeHideTimer.Start();
        };

        _addInput.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            AddTodo();
            args.Handled = true;
        };
        _importButton.Click += (_, _) => OpenImportDialog();
        _saveTimer.Tick += (_, _) => SaveNow();
        _edgeHideTimer.Tick += (_, _) =>
        {
            _edgeHideTimer.Stop();
            if (_edgeHideEnabled && !_expanded && _hiddenEdge != 0 && !IsPointerOver) HideToEdge();
        };
        _recurringTimer.Tick += (_, _) => OnMinuteTick();
        _focusTimer.Tick += (_, _) => UpdateFocusTimer();
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            ClearUndoNotice();
        };

        Deactivated += (_, _) =>
        {
            if (_dialogOpen || _movingWindow || !_expanded) return;
            // Interacting with the backlog tab or drawer must never collapse the
            // window, and a click on the tab briefly deactivates the owner.
            if (_backlogHost.IsVisible) return;
            if ((DateTime.UtcNow - _interactionAt).TotalMilliseconds < 600) return;
            if (IsActive || _backlogTab?.IsActive == true || _backlogTab?.IsPointerOver == true) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsActive || _dialogOpen || _backlogHost.IsVisible) return;
                if ((DateTime.UtcNow - _interactionAt).TotalMilliseconds < 600) return;
                Collapse();
            }, DispatcherPriority.Background);
        };
        Closing += (_, _) => SaveNow();

        NormalizeFocus();
        EnsureRecurring(_clock.Today);
        RenderAll();
        StartTimers();
        PositionDefault();
    }

    public bool IsExpanded => _expanded;

    /// <summary>Opens the window on today and expands it, matching the tray entry.</summary>
    public void ShowToday()
    {
        if (!IsVisible) Show();
        ShowDate(_clock.Today);
        Expand();
        Activate();
    }

    public void SetEdgeHideEnabled(bool enabled)
    {
        _edgeHideEnabled = enabled;
        if (!enabled && _hiddenEdge != 0)
        {
            _hiddenEdge = 0;
            _edgeHideTimer.Stop();
        }
    }

    /// <summary>Hides without collapsing, so the next open restores the state.</summary>
    public void HideWindow()
    {
        SaveNow();
        _backlogTab?.Hide();
        Hide();
    }

    // ---------------------------------------------------------------- layout

    private Grid BuildCompactView()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("33,*,29"),
            RowDefinitions = new RowDefinitions("18,29,*"),
            // A transparent background keeps the whole capsule clickable; a null
            // background would make Avalonia skip hit testing on the empty areas.
            Background = Brushes.Transparent
        };

        var title = TodoTheme.Label("当前事项", 10, TodoTheme.MutedText);
        Grid.SetRow(title, 0);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var check = new Border
        {
            Width = 25,
            Height = 25,
            CornerRadius = new CornerRadius(13),
            BorderBrush = TodoTheme.ControlBorder,
            BorderThickness = new Thickness(1.4),
            Background = Brushes.Transparent,
            Child = TodoIcons.Check(TodoTheme.SecondaryText, 11),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        check.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(check).Properties.IsLeftButtonPressed) return;
            args.Handled = true;
            CompactCheckClick();
        };
        Grid.SetRow(check, 0);
        Grid.SetRowSpan(check, 2);
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        Grid.SetRow(_compactItem, 1);
        Grid.SetColumn(_compactItem, 1);
        grid.Children.Add(_compactItem);

        var focusButton = TodoTheme.IconButton(new FocusRingIcon { Width = 22, Height = 22 }, 22);
        focusButton.VerticalAlignment = VerticalAlignment.Center;
        focusButton.Click += (_, _) => OpenFocusDial();
        Grid.SetRow(focusButton, 0);
        Grid.SetRowSpan(focusButton, 2);
        Grid.SetColumn(focusButton, 2);
        grid.Children.Add(focusButton);

        Grid.SetRow(_compactProgress, 2);
        Grid.SetColumn(_compactProgress, 1);
        Grid.SetColumnSpan(_compactProgress, 2);
        grid.Children.Add(_compactProgress);

        grid.PointerPressed += CompactPointerPressed;
        return grid;
    }

    private Grid BuildExpandedView()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("39,48,*,35"), IsVisible = false };

        // Header: previous day, today, date, next day, collapse.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("36,36,*,36,36") };
        var previous = TodoTheme.IconButton(TodoIcons.Chevron(true, size: 11), 30);
        previous.Click += (_, _) => ShowDate(_viewedDate.AddDays(-1));
        Grid.SetColumn(previous, 0);
        header.Children.Add(previous);

        var today = TodoTheme.TextButton("今", 10.5, 32);
        today.Height = 26;
        today.Click += (_, _) => ShowDate(_clock.Today);
        Grid.SetColumn(today, 1);
        header.Children.Add(today);

        var dateStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        dateStack.Children.Add(_dateLabel);
        dateStack.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(dateStack).Properties.IsLeftButtonPressed) return;
            args.Handled = true;
            OpenDateChooser();
        };
        Grid.SetColumn(dateStack, 2);
        header.Children.Add(dateStack);

        var next = TodoTheme.IconButton(TodoIcons.Chevron(false, size: 11), 30);
        next.Click += (_, _) => ShowDate(_viewedDate.AddDays(1));
        Grid.SetColumn(next, 3);
        header.Children.Add(next);

        var collapse = TodoTheme.IconButton(TodoIcons.Minimize(size: 11), 30);
        collapse.Click += (_, _) => Collapse();
        Grid.SetColumn(collapse, 4);
        header.Children.Add(collapse);

        var headerHost = new Border { Child = header, Cursor = new Cursor(StandardCursorType.SizeAll) };
        headerHost.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(headerHost).Properties.IsLeftButtonPressed) return;
            // Pressing a header button must click it, not start a window move.
            if (IsInsideButton(args.Source)) return;
            try { BeginMoveDrag(args); } catch { }
        };
        Grid.SetRow(headerHost, 0);
        grid.Children.Add(headerHost);

        // Add row.
        var addRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,42") };
        Grid.SetColumn(_addInput, 0);
        addRow.Children.Add(_addInput);
        var addButton = TodoTheme.TextButton("＋", 14, 36);
        addButton.Height = 30;
        addButton.Click += (_, _) => AddTodo();
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(addButton);
        Grid.SetRow(addRow, 1);
        grid.Children.Add(addRow);

        // Card deck.
        _cardCanvas.Width = 372;
        _cardScroll = new ScrollViewer
        {
            Content = _cardCanvas,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 2, 2, 2)
        };
        _cardScroll.SizeChanged += (_, _) =>
        {
            if (_cardScroll.Viewport.Width > 40) _cardCanvas.Width = _cardScroll.Viewport.Width - 4;
        };
        Grid.SetRow(_cardScroll, 2);
        grid.Children.Add(_cardScroll);

        // Footer.
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        _progressLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_progressLabel, 0);
        footer.Children.Add(_progressLabel);
        _importButton.Height = 26;
        Grid.SetColumn(_importButton, 1);
        footer.Children.Add(_importButton);
        var rulesButton = TodoTheme.TextButton("周期性任务", 10, 88);
        rulesButton.Height = 26;
        rulesButton.Click += (_, _) => OpenRecurringRules();
        Grid.SetColumn(rulesButton, 2);
        footer.Children.Add(rulesButton);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        return grid;
    }

    private Border BuildBacklogHost()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        _backlogTitle = TodoTheme.Label("堆积事项", 12, TodoTheme.PrimaryText, bold: true);
        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        headerRow.Children.Add(_backlogTitle);
        var close = TodoTheme.IconButton(TodoIcons.Cross(size: 10), 24);
        close.Click += (_, _) => CloseBacklog();
        Grid.SetColumn(close, 1);
        headerRow.Children.Add(close);
        Grid.SetRow(headerRow, 0);
        grid.Children.Add(headerRow);

        var scroll = new ScrollViewer { Content = _backlogList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        Grid.SetRow(_backlogFooter, 2);
        grid.Children.Add(_backlogFooter);

        return new Border
        {
            Child = grid,
            Background = TodoTheme.DialogBackground,
            BorderBrush = TodoTheme.DialogBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(12),
            ZIndex = 20
        };
    }

    // ---------------------------------------------------------------- render

    private void RenderAll()
    {
        RenderCompact();
        RenderExpanded();
        if (_backlogHost.IsVisible) RenderBacklog();
    }

    private void RenderCompact()
    {
        var today = TodoLogic.FindDay(_data, _clock.Today, false);
        var current = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        var total = today?.Items.Count ?? 0;
        var completed = today is null ? 0 : TodoLogic.CountCompleted(today);

        _compactItem.Text = current?.Text
                            ?? (total > 0 && completed == total ? "今日事项已完成" : "暂无待办");
        _compactProgress.Text = $"今日完成 {completed} / {total}";
        UpdateFocusIcons();
    }

    private void RenderExpanded()
    {
        _dateLabel.Text = _viewedDate == _clock.Today
            ? "今天 · " + _viewedDate.ToString("M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"))
            : _viewedDate.ToString("M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

        var day = TodoLogic.FindDay(_data, _viewedDate, true)!;
        _importButton.IsVisible = _viewedDate == _clock.Today && TodoLogic.ImportCandidates(_data, _clock.Today).Count > 0;
        RenderCards(day);
        UpdateExpandedProgress(day);
    }

    private void UpdateExpandedProgress(TodoDay day)
    {
        _progressLabel.Text = $"已完成 {TodoLogic.CountCompleted(day)} / 全部 {day.Items.Count}";
    }

    private void RenderCards(TodoDay day)
    {
        _cardCanvas.Children.Clear();
        _cards.Clear();
        _cardCanvas.Height = TodoLogic.CardCanvasHeight(day.Items.Count);

        for (var index = 0; index < day.Items.Count; index++)
        {
            var card = CreateCard(day, day.Items[index], index);
            _cardCanvas.Children.Add(card);
            _cards[day.Items[index].Id!] = card;
        }
    }

    private Control CreateCard(TodoDay day, DailyTodoItem item, int index)
    {
        var inset = TodoLogic.CardInset(index);
        var width = Math.Max(300, _cardCanvas.Width - 4 - inset * 2);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*,32,32,33,32") };

        var check = new Button
        {
            Content = item.Completed ? "✓" : string.Empty,
            Width = 25,
            Height = 25,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            FontSize = 12,
            FontFamily = TodoTheme.UiFont,
            Foreground = item.Completed ? TodoTheme.PrimaryText : TodoTheme.SecondaryText,
            Background = TodoTheme.ControlBackground,
            BorderBrush = TodoTheme.ControlBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        // Avalonia's TextBox has no TextDecorations, so the completed strike is a
        // measured line drawn over the editable text.
        var textHost = new Grid();
        var text = new TextBox
        {
            Text = item.Text,
            FontSize = 12.5,
            FontFamily = TodoTheme.UiFont,
            FontWeight = item.Completed ? FontWeight.Normal : FontWeight.SemiBold,
            Foreground = item.Completed ? TodoTheme.SecondaryText : TodoTheme.PrimaryText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 1, 4, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = false
        };
        var strike = new Border
        {
            Height = 1,
            Background = TodoTheme.SecondaryText,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            IsHitTestVisible = false,
            IsVisible = item.Completed
        };
        void SyncStrike()
        {
            var formatted = new FormattedText(text.Text ?? string.Empty,
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface(TodoTheme.UiFont, FontStyle.Normal, FontWeight.Normal), text.FontSize, TodoTheme.SecondaryText);
            strike.Width = formatted.Width;
        }
        SyncStrike();
        text.TextChanged += (_, _) =>
        {
            item.Text = text.Text ?? string.Empty;
            SyncStrike();
            QueueSave();
            RenderCompact();
        };
        textHost.Children.Add(text);
        textHost.Children.Add(strike);
        Grid.SetColumn(textHost, 1);
        grid.Children.Add(textHost);

        var focusIcon = new FocusRingIcon { Width = 22, Height = 22 };
        var focusButton = TodoTheme.IconButton(focusIcon, 27);
        focusButton.Tag = item.Id;
        focusButton.VerticalAlignment = VerticalAlignment.Center;
        focusButton.IsVisible = IsFocusRingVisible(day, item);
        focusButton.Click += (_, _) => OpenFocusDial();
        Grid.SetColumn(focusButton, 2);
        grid.Children.Add(focusButton);

        var backlogButton = TodoTheme.IconButton(TodoIcons.StackedItems(size: 14), 26);
        backlogButton.Height = 25;
        backlogButton.IsVisible = false;
        backlogButton.Click += (_, _) => MoveToBacklog(day, item);
        Grid.SetColumn(backlogButton, 3);
        grid.Children.Add(backlogButton);

        var handle = TodoTheme.IconButton(TodoTheme.Label("⋮⋮", 13, TodoTheme.SecondaryText), 27);
        handle.Cursor = new Cursor(StandardCursorType.SizeAll);
        handle.IsVisible = !item.Completed;
        Grid.SetColumn(handle, 4);
        grid.Children.Add(handle);

        var delete = new Button
        {
            Content = "×",
            Width = 26,
            Height = 25,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            FontSize = 13,
            FontFamily = TodoTheme.UiFont,
            Foreground = TodoTheme.SecondaryText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        delete.Click += (_, _) => DeleteItem(day, item);
        Grid.SetColumn(delete, 5);
        grid.Children.Add(delete);

        var border = new Border
        {
            Child = grid,
            Width = width,
            Height = TodoLogic.CardHeight,
            CornerRadius = new CornerRadius(TodoTheme.CardCornerRadius),
            Background = TodoTheme.CardBackground(index),
            BorderBrush = TodoTheme.CardBorder,
            BorderThickness = new Thickness(1),
            Padding = TodoTheme.CardPadding,
            Cursor = new Cursor(StandardCursorType.Hand),
            BoxShadow = TodoTheme.CardShadow(index)
        };
        border.PointerEntered += (_, _) =>
        {
            if (!item.Completed) backlogButton.IsVisible = true;
        };
        border.PointerExited += (_, _) => backlogButton.IsVisible = false;
        border.Tag = item;
        Canvas.SetLeft(border, 2 + inset);
        Canvas.SetTop(border, index * TodoLogic.CardStep);
        border.ZIndex = day.Items.Count - index;

        check.Click += (_, _) => ToggleCompleted(day, item, border);
        WireDrag(border, handle, day, item);
        return border;
    }

    /// <summary>Staggered entrance used when the deck unfolds, like the Windows module.</summary>
    private void AnimateCardEntrance()
    {
        var index = 0;
        foreach (var child in _cardCanvas.Children)
        {
            if (child is Visual visual) TodoAnim.Materialize(visual, 13, 220, Math.Min(index, 8) * 38);
            index++;
        }
    }

    private bool IsFocusRingVisible(TodoDay day, DailyTodoItem item)
    {
        var current = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        return ReferenceEquals(current, item) || FocusTimerMath.IsActiveFor(_data.FocusTimer, item.Id);
    }

    private void WireDrag(Border card, Control handle, TodoDay day, DailyTodoItem item)
    {
        var dragging = false;
        var startTop = 0.0;

        handle.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            if (item.Completed || _cardAnimating) return;
            dragging = true;
            startTop = Canvas.GetTop(card);
            card.ZIndex = 10000;
            card.Opacity = 0.96;
            args.Pointer.Capture(handle);
            args.Handled = true;
        };

        handle.PointerMoved += (_, args) =>
        {
            if (!dragging) return;
            var point = args.GetPosition(_cardCanvas);
            var openCount = TodoLogic.FirstCompletedIndex(day.Items);
            var index = TodoLogic.ComputeDropIndex(point.Y - TodoLogic.CardHeight / 2, openCount);
            Canvas.SetTop(card, Math.Min(Math.Max(0, point.Y - TodoLogic.CardHeight / 2), Math.Max(0, (openCount - 1) * TodoLogic.CardStep)));
            ReflowAround(day, item, index);
            args.Handled = true;
        };

        handle.PointerReleased += (_, args) =>
        {
            if (!dragging) return;
            dragging = false;
            args.Pointer.Capture(null);
            card.Opacity = 1;
            var point = args.GetPosition(_cardCanvas);
            var openCount = TodoLogic.FirstCompletedIndex(day.Items);
            var target = TodoLogic.ComputeDropIndex(point.Y - TodoLogic.CardHeight / 2, openCount);

            // Dragging out of the left edge drops the item on the backlog tab,
            // which sits beside the window.
            if (point.X < -20)
            {
                Canvas.SetTop(card, startTop);
                MoveToBacklog(day, item);
                args.Handled = true;
                return;
            }

            var from = day.Items.IndexOf(item);
            if (from >= 0 && target >= 0 && from != target && from < openCount)
            {
                day.Items.RemoveAt(from);
                day.Items.Insert(target, item);
                SaveNow();
            }
            RenderAll();
            args.Handled = true;
        };
    }

    /// <summary>Moves the other cards out of the way while one is being dragged.</summary>
    private void ReflowAround(TodoDay day, DailyTodoItem dragged, int targetIndex)
    {
        var openCount = TodoLogic.FirstCompletedIndex(day.Items);
        var slot = 0;
        foreach (var item in day.Items)
        {
            if (ReferenceEquals(item, dragged)) continue;
            if (!_cards.TryGetValue(item.Id!, out var card)) continue;
            var index = slot < targetIndex ? slot : slot + 1;
            if (slot >= openCount && targetIndex >= openCount) index = slot;
            Canvas.SetTop(card, index * TodoLogic.CardStep);
            slot++;
        }
    }

    // ---------------------------------------------------------------- actions

    private void CompactCheckClick()
    {
        if (_compactCompleting) return;
        var current = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        if (current is null) return;
        var day = TodoLogic.FindDay(_data, _clock.Today, false);
        if (day is null) return;
        _compactCompleting = true;
        _compactItem.Opacity = 1;
        TodoAnim.Fade(_compactItem, 1, 0, 260, () =>
        {
            TodoLogic.Complete(day, current, _data);
            SaveNow();
            RenderAll();
            _compactItem.Opacity = 0;
            TodoAnim.Materialize(_compactItem, 6, 220);
            _compactCompleting = false;
        });
    }

    private void CompactPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(_compactView).Properties.IsLeftButtonPressed) return;
        // The check and focus buttons live inside the draggable capsule.
        if (IsInsideButton(args.Source)) return;
        RevealFromEdge();
        var original = _logicalPosition;
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
        // The window manager owns the position during a drag, so read it back
        // before deciding whether this was a click or a move.
        SyncFromPlatform();
        var moved = Math.Abs(_logicalPosition.X - original.X) > 1 || Math.Abs(_logicalPosition.Y - original.Y) > 1;
        SnapOrHideAtEdge();
        if (!moved) Expand();
        args.Handled = true;
    }

    private void ToggleCompleted(TodoDay day, DailyTodoItem item, Control card)
    {
        if (_cardAnimating) return;
        _cardAnimating = true;
        var completing = !item.Completed;
        var cardGrid = (Grid)((Border)card).Child!;
        var check = (Button)cardGrid.Children[0];
        var textHost = (Grid)cardGrid.Children[1];
        var text = (TextBox)textHost.Children[0];
        var strike = (Border)textHost.Children[1];

        check.Content = completing ? "✓" : string.Empty;
        check.Foreground = completing ? TodoTheme.PrimaryText : TodoTheme.SecondaryText;
        text.Foreground = completing ? TodoTheme.SecondaryText : TodoTheme.PrimaryText;
        text.FontWeight = completing ? FontWeight.Normal : FontWeight.SemiBold;
        strike.IsVisible = completing;
        _cardCanvas.IsHitTestVisible = false;

        TodoAnim.Fade(card, card.Opacity, 0, 235, () =>
        {
            if (completing) TodoLogic.Complete(day, item, _data);
            else TodoLogic.Uncomplete(day, item);
            SaveNow();
            RenderAll();
            _cardCanvas.IsHitTestVisible = true;
            if (_cards.TryGetValue(item.Id!, out var recreated))
                TodoAnim.Materialize(recreated, 8, 285);
            _cardAnimating = false;
        });
    }

    private void DeleteItem(TodoDay day, DailyTodoItem item)
    {
        TodoLogic.Delete(day, item, _data);
        SaveNow();
        RenderAll();
    }

    private void AddTodo()
    {
        var text = (_addInput.Text ?? string.Empty).Trim();
        if (text.Length == 0) return;
        var day = TodoLogic.FindDay(_data, _viewedDate, true)!;
        TodoLogic.AddItem(day, text, _clock, _ids);
        _addInput.Text = string.Empty;
        SaveNow();
        RenderAll();
        if (_cards.Count > 0 && _cards.Values.Last() is { } last) TodoAnim.Materialize(last, 8, 210);
    }

    private void MoveToBacklog(TodoDay day, DailyTodoItem item)
    {
        if (!TodoLogic.MoveToBacklog(day, item, _data)) return;
        _store.Save(_data);
        RenderAll();
    }

    // ---------------------------------------------------------------- focus

    private void OpenFocusDial()
    {
        var current = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        if (current is null) return;
        _dialogOpen = true;
        var window = new FocusDialWindow(
            current.Text ?? string.Empty,
            _data.FocusTimer?.DurationMinutes ?? 25,
            FocusTimerMath.IsActiveFor(_data.FocusTimer, current.Id),
            _sound,
            () => FocusTimerMath.RemainingSeconds(_data.FocusTimer, _clock.UtcNow),
            minutes =>
            {
                _data.FocusTimer = FocusTimerMath.Create(current.Id!, current.Text ?? string.Empty, minutes, _clock);
                SaveNow();
                RenderAll();
            },
            () =>
            {
                _data.FocusTimer = null;
                SaveNow();
                RenderAll();
            });
        window.ShowDialog<bool>(this).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            _dialogOpen = false;
            RenderAll();
        }));
    }

    private void UpdateFocusTimer()
    {
        var timer = _data.FocusTimer;
        if (timer is null)
        {
            _lastFocusSoundSecond = -1;
            return;
        }

        var remaining = FocusTimerMath.RemainingSeconds(timer, _clock.UtcNow);
        if (remaining <= 0)
        {
            _data.FocusTimer = null;
            SaveNow();
            RenderAll();
            _sound.FocusFinished();
            FocusFinished?.Invoke(timer.ItemText ?? string.Empty);
            _lastFocusSoundSecond = -1;
            return;
        }

        UpdateFocusIcons();
        var seconds = (int)remaining;
        if (seconds == _lastFocusSoundSecond) return;
        _lastFocusSoundSecond = seconds;
    }

    private void UpdateFocusIcons()
    {
        var remaining = FocusTimerMath.RemainingSeconds(_data.FocusTimer, _clock.UtcNow);
        var fill = FocusTimerMath.Fill(_data.FocusTimer, _clock.UtcNow);
        var urgent = FocusTimerMath.IsUrgent(_data.FocusTimer, _clock.UtcNow);

        foreach (var icon in EnumerateFocusIcons(_compactView))
        {
            icon.Active = _data.FocusTimer is not null;
            icon.Progress = fill;
            icon.Urgent = urgent;
        }
        foreach (var icon in EnumerateFocusIcons(_expandedView))
        {
            var itemId = (icon.Parent as Control)?.Tag as string;
            var active = FocusTimerMath.IsActiveFor(_data.FocusTimer, itemId);
            icon.Active = active;
            icon.Progress = active ? fill : 0;
            icon.Urgent = active && urgent;
        }
        if (_data.FocusTimer is not null)
            ToolTip.SetTip(_shell, "专注剩余 " + FocusTimerMath.FormatRemaining(remaining));
    }

    private static bool IsInsideButton(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Button) return true;
        return false;
    }

    private static IEnumerable<FocusRingIcon> EnumerateFocusIcons(Visual root)
    {
        foreach (var child in root.GetVisualDescendants())
            if (child is FocusRingIcon icon) yield return icon;
    }

    private void NormalizeFocus()
    {
        var normalized = FocusTimerMath.Normalize(_data.FocusTimer, _clock.UtcNow);
        if (!ReferenceEquals(normalized, _data.FocusTimer))
        {
            _data.FocusTimer = normalized;
            _store.Save(_data);
        }
    }

    // ---------------------------------------------------------------- backlog

    internal void ToggleBacklog()
    {
        _interactionAt = DateTime.UtcNow;
        if (_backlogHost.IsVisible) CloseBacklog();
        else OpenBacklog();
    }

    private void OpenBacklog()
    {
        _multiSelect = false;
        _selectedBacklogIds.Clear();
        RenderBacklog();
        // The drawer replaces the main view instead of floating over it, matching
        // the Windows module and avoiding see-through content behind.
        _expandedView.IsVisible = false;
        _backlogHost.IsVisible = true;
    }

    private void CloseBacklog()
    {
        if (!_backlogHost.IsVisible) return;
        _backlogHost.IsVisible = false;
        _multiSelect = false;
        _selectedBacklogIds.Clear();
        if (_expanded) _expandedView.IsVisible = true;
    }

    private void ToggleMultiSelect()
    {
        _multiSelect = !_multiSelect;
        _selectedBacklogIds.Clear();
        RenderBacklog();
    }

    private void RenderBacklog()
    {
        _backlogList.Children.Clear();
        _backlogFooter.Children.Clear();
        _backlogFooter.Children.Add(_undoNotice);
        _backlogTitle.Text = _data.BacklogItems.Count > 0
            ? $"堆积事项 {_data.BacklogItems.Count}"
            : "堆积事项";

        if (_data.BacklogItems.Count == 0)
        {
            _backlogList.Children.Add(TodoTheme.Label("把暂时不处理的事项放在这里", 11, TodoTheme.MutedText));
            return;
        }

        foreach (var item in _data.BacklogItems.ToArray())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6 };
            row.Height = 42;

            if (_multiSelect)
            {
                var selected = _selectedBacklogIds.Contains(item.Id!);
                var box = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(4),
                    Background = selected ? TodoTheme.SelectedRow : Brushes.Transparent,
                    BorderBrush = selected ? Brushes.Transparent : TodoTheme.ControlBorder,
                    BorderThickness = new Thickness(1.2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                box.PointerPressed += (_, args) =>
                {
                    args.Handled = true;
                    if (!_selectedBacklogIds.Remove(item.Id!)) _selectedBacklogIds.Add(item.Id!);
                    RenderBacklog();
                };
                Grid.SetColumn(box, 0);
                row.Children.Add(box);
            }
            else
            {
                var check = new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    BorderBrush = TodoTheme.ControlBorder,
                    BorderThickness = new Thickness(1.3),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand)
                };
                check.PointerPressed += (_, args) =>
                {
                    args.Handled = true;
                    TodoLogic.ToggleBacklogCompleted(_data, item, _clock.Today);
                    SaveNow();
                    RenderAll();
                };
                Grid.SetColumn(check, 0);
                row.Children.Add(check);
            }

            var text = TodoTheme.Label(item.Text ?? string.Empty, 11.5,
                item.Completed ? TodoTheme.SecondaryText : TodoTheme.PrimaryText);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var source = TodoTheme.Label(FormatSourceDate(item.BacklogSourceDate), 9.5, TodoTheme.MutedText);
            Grid.SetColumn(source, 2);
            row.Children.Add(source);

            if (!_multiSelect)
            {
                var move = TodoTheme.TextButton("移入今日", 9.5, 62);
                move.Height = 24;
                move.Click += (_, _) =>
                {
                    TodoLogic.MoveBacklogToToday(_data, [item], _clock.Today);
                    SaveNow();
                    RenderAll();
                };
                Grid.SetColumn(move, 3);
                row.Children.Add(move);
            }

            _backlogList.Children.Add(row);
        }

        if (!_multiSelect)
        {
            var open = TodoTheme.TextButton("多选", 10, 56);
            open.Height = 26;
            open.Click += (_, _) => ToggleMultiSelect();
            Grid.SetColumn(open, 1);
            _backlogFooter.Children.Add(open);
            return;
        }

        var selectedItems = _data.BacklogItems.Where(item => _selectedBacklogIds.Contains(item.Id!)).ToList();
        var exit = TodoTheme.TextButton("退出多选", 10, 72);
        exit.Height = 26;
        exit.Click += (_, _) => ToggleMultiSelect();
        Grid.SetColumn(exit, 1);
        _backlogFooter.Children.Add(exit);

        var moveToday = TodoTheme.TextButton("移入今日", 10, 68);
        moveToday.Height = 26;
        moveToday.IsEnabled = selectedItems.Count > 0;
        moveToday.Opacity = selectedItems.Count > 0 ? 1 : 0.4;
        moveToday.Click += (_, _) =>
        {
            TodoLogic.MoveBacklogToToday(_data, selectedItems, _clock.Today);
            _selectedBacklogIds.Clear();
            SaveNow();
            RenderAll();
        };
        Grid.SetColumn(moveToday, 2);
        _backlogFooter.Children.Add(moveToday);

        var delete = TodoTheme.IconButton(TodoIcons.Trash(size: 13), 26);
        delete.IsEnabled = selectedItems.Count > 0;
        delete.Opacity = selectedItems.Count > 0 ? 1 : 0.4;
        delete.Click += (_, _) => DeleteSelectedBacklogItems(selectedItems);
        Grid.SetColumn(delete, 3);
        _backlogFooter.Children.Add(delete);
    }

    private void DeleteSelectedBacklogItems(List<DailyTodoItem> items)
    {
        if (items.Count == 0) return;
        _undoTimer.Stop();
        _undoEntries.Clear();
        _undoEntries.AddRange(TodoLogic.DeleteBacklogItems(_data, items));
        if (_undoEntries.Count == 0) return;
        _selectedBacklogIds.Clear();
        SaveNow();
        RenderBacklog();
        ShowUndoNotice(_undoEntries.Count);
        _undoTimer.Start();
    }

    private void ShowUndoNotice(int count)
    {
        _undoLabel.Text = $"已删除 {count} 项";
        _undoNotice.IsVisible = true;
        if (_undoNotice.Children.Count > 1) return;
        var undo = TodoTheme.TextButton("撤销", 10, 48);
        undo.Height = 22;
        undo.Click += (_, _) =>
        {
            _undoTimer.Stop();
            TodoLogic.UndoBacklogDelete(_data, _undoEntries);
            _undoEntries.Clear();
            SaveNow();
            ClearUndoNotice();
            RenderAll();
        };
        _undoNotice.Children.Add(undo);
    }

    private void ClearUndoNotice()
    {
        _undoNotice.IsVisible = false;
        _undoEntries.Clear();
    }

    private static string FormatSourceDate(string? value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("M月d日", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"))
            : string.Empty;

    // ---------------------------------------------------------------- dialogs

    private void OpenDateChooser()
    {
        _dialogOpen = true;
        var chooser = new DateChooserWindow(_viewedDate);
        chooser.ShowDialog<DateTime?>(this).ContinueWith(result => Dispatcher.UIThread.Post(() =>
        {
            _dialogOpen = false;
            if (result.IsCompletedSuccessfully && result.Result is { } selected) ShowDate(selected);
        }));
    }

    private void OpenRecurringRules()
    {
        _dialogOpen = true;
        var dialog = new RecurringRulesWindow(_data.RecurringRules, _clock, _ids);
        dialog.ShowDialog<bool>(this).ContinueWith(result => Dispatcher.UIThread.Post(() =>
        {
            _dialogOpen = false;
            if (!result.IsCompletedSuccessfully || !result.Result) return;
            TodoLogic.RestoreRecurringRulesForToday(_data, dialog.ApplyTodayRuleIds, _clock.Today);
            EnsureRecurring(_clock.Today);
            if (_viewedDate != _clock.Today) EnsureRecurring(_viewedDate);
            SaveNow();
            RenderAll();
        }));
    }

    private void OpenImportDialog()
    {
        if (_viewedDate != _clock.Today) return;
        var candidates = TodoLogic.ImportCandidates(_data, _clock.Today);
        if (candidates.Count == 0)
        {
            RenderExpanded();
            return;
        }
        _dialogOpen = true;
        var dialog = new ImportWindow(candidates);
        dialog.ShowDialog<bool>(this).ContinueWith(result => Dispatcher.UIThread.Post(() =>
        {
            _dialogOpen = false;
            if (!result.IsCompletedSuccessfully || !result.Result) return;
            if (dialog.SelectedAction == ImportAction.AddToToday)
                TodoLogic.ImportItems(_data, dialog.SelectedItems, _clock.Today, _clock, _ids);
            else if (dialog.SelectedAction == ImportAction.AddToBacklog)
                TodoLogic.ImportItemsToBacklog(_data, dialog.SelectedItems, _clock.Today, _clock, _ids);
            SaveNow();
            RenderAll();
        }));
    }

    private void MaybePromptImport()
    {
        if (_viewedDate != _clock.Today || _dialogOpen) return;
        var todayKey = TodoLogic.DateKey(_clock.Today);
        if (_data.LastImportPromptDate == todayKey) return;
        if (TodoLogic.ImportCandidates(_data, _clock.Today).Count == 0) return;
        _data.LastImportPromptDate = todayKey;
        SaveNow();
        OpenImportDialog();
    }

    // ---------------------------------------------------------------- dates

    private void ShowDate(DateTime date)
    {
        _viewedDate = date.Date;
        EnsureRecurring(_viewedDate);
        RenderAll();
        if (_viewedDate == _clock.Today)
            Dispatcher.UIThread.Post(MaybePromptImport, DispatcherPriority.Background);
    }

    private bool EnsureRecurring(DateTime date)
    {
        if (!RecurringTodoEngine.Ensure(_data, date, _clock, _ids)) return false;
        _store.Save(_data);
        return true;
    }

    private void OnMinuteTick()
    {
        var today = _clock.Today;
        if (today != _observedToday)
        {
            _observedToday = today;
            NormalizeFocus();
            ShowDate(today);
            return;
        }
        if (EnsureRecurring(_viewedDate)) RenderAll();
        RenderCompact();
    }

    private void StartTimers()
    {
        _recurringTimer.Start();
        _focusTimer.Start();
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        _store.Save(_data);
        RenderCompact();
    }

    // ---------------------------------------------------------------- window

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        _compactView.IsVisible = false;
        _expandedView.IsVisible = true;
        ResizeAnchored(TodoTheme.ExpandedWidth, TodoTheme.ExpandedHeight);
        ShowBacklogTab();
        RenderAll();
        AnimateCardEntrance();
        _addInput.Focus();
    }

    private void Collapse()
    {
        if (!_expanded) return;
        SaveNow();
        _expanded = false;
        CloseBacklog();
        HideBacklogTab();
        _expandedView.IsVisible = false;
        _compactView.IsVisible = true;
        ResizeAnchored(TodoTheme.CompactWidth, TodoTheme.CompactHeight);
        RenderCompact();
        if (_edgeHideEnabled && _hiddenEdge != 0 && !IsPointerOver) _edgeHideTimer.Start();
    }

    /// <summary>
    /// Resizes while keeping the bottom-right corner anchored. On X11 the resize
    /// and the move are separate asynchronous requests, so the anchor is re-asserted
    /// once the platform has processed the new geometry.
    /// </summary>
    private void ResizeAnchored(double newWidth, double newHeight)
    {
        var targetX = _logicalPosition.X + Width - newWidth;
        var targetY = _logicalPosition.Y + Height - newHeight;
        Width = newWidth;
        Height = newHeight;
        ApplyPosition(targetX, targetY);
        KeepOnScreen();
        Dispatcher.UIThread.Post(() =>
        {
            ApplyPosition(targetX, targetY);
            KeepOnScreen();
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------- positioning
    //
    // Setting Window.Position is asynchronous on X11: reading it back right after
    // assigning returns the previous value, and a window manager may adjust the
    // request. The intended logical position is therefore tracked here and the
    // platform value is only read back after the user drags the window.

    private Point _logicalPosition;

    internal Point LogicalPosition => _logicalPosition;

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
        if (_expanded) _backlogTab?.PositionBesideOwner();
    }

    private void SyncFromPlatform()
    {
        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        _logicalPosition = new Point(Position.X / scaling, Position.Y / scaling);
    }

    private Rect WorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return new Rect(0, 0, 1920, 1080);
        var area = screen.WorkingArea;
        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        return new Rect(area.X / scaling, area.Y / scaling, area.Width / scaling, area.Height / scaling);
    }

    private void PositionDefault()
    {
        var work = WorkingArea();
        ApplyPosition(work.Right - Width - 18, work.Bottom - Height - 18);
        _positioned = true;
    }

    public void EnsurePositioned()
    {
        if (!_positioned) PositionDefault();
    }

    private void KeepOnScreen()
    {
        var work = WorkingArea();
        var x = Math.Max(work.Left, Math.Min(_logicalPosition.X, work.Right - Width));
        var y = Math.Max(work.Top, Math.Min(_logicalPosition.Y, work.Bottom - Height));
        ApplyPosition(x, y);
    }

    private void SnapOrHideAtEdge()
    {
        var work = WorkingArea();
        var x = _logicalPosition.X;
        var y = Math.Max(work.Top, Math.Min(_logicalPosition.Y, work.Bottom - Height));
        const double snapDistance = 28;
        const double visibleStrip = 10;
        if (!_edgeHideEnabled)
        {
            ApplyPosition(Math.Max(work.Left, Math.Min(x, work.Right - Width)), y);
            _hiddenEdge = 0;
            return;
        }

        if (x - work.Left <= snapDistance)
        {
            _hiddenEdge = -1;
            ApplyPosition(work.Left - Width + visibleStrip, y);
            return;
        }
        if (work.Right - (x + Width) <= snapDistance)
        {
            _hiddenEdge = 1;
            ApplyPosition(work.Right - visibleStrip, y);
            return;
        }
        _hiddenEdge = 0;
        ApplyPosition(Math.Max(work.Left, Math.Min(x, work.Right - Width)), y);
    }

    private void RevealFromEdge()
    {
        if (_hiddenEdge == 0) return;
        var work = WorkingArea();
        ApplyPosition(_hiddenEdge < 0 ? work.Left : work.Right - Width, _logicalPosition.Y);
    }

    private void HideToEdge()
    {
        var work = WorkingArea();
        ApplyPosition(_hiddenEdge < 0 ? work.Left - Width + 10 : work.Right - 10, _logicalPosition.Y);
    }

    private void ShowBacklogTab()
    {
        if (_data.BacklogItems.Count == 0 && _backlogTab is null) return;
        _backlogTab ??= new BacklogTabWindow(this);
        if (!_backlogTab.IsVisible) _backlogTab.Show(this);
        _backlogTab.PositionBesideOwner();
        _backlogTab.SetCount(_data.BacklogItems.Count);
    }

    private void HideBacklogTab() => _backlogTab?.Hide();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_positioned) PositionDefault();
        RenderAll();
    }

}
