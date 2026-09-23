using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    /// <summary>Windows compactMessageTimer: the gentle lines rotate every five minutes.</summary>
    private readonly DispatcherTimer _compactMessageTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly CompactMessageRotation _compactMessages = new();

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
    private readonly Border _compactItemHost;
    private readonly RowDefinition _compactSubItemsRow = new();
    private readonly StackPanel _compactSubItemsPanel = new();
    private Button _compactCheck = null!;
    private Button _compactFocusButton = null!;
    private readonly TextBlock _compactProgress;
    private readonly TextBlock _dateLabel;
    private readonly TextBlock _progressLabel;
    private readonly TextBox _addInput;
    private TextBlock _backlogTitle = null!;

    private readonly Dictionary<string, Control> _cards = new(StringComparer.Ordinal);
    private readonly List<BacklogUndoEntry> _undoEntries = [];
    private readonly List<string> _selectedBacklogIds = [];

    /// <summary>Sub item panel state, mirroring the Windows owner id fields.</summary>
    private string? _expandedSubItemOwnerId;
    private string? _revealedSubItemInputOwnerId;
    private TextBox? _activeSubItemInput;

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
    private Point _compactPressPosition;
    private double _revealedLeft;
    private bool _compactMoved;
    private bool _pendingCompactClick;

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

        Title = "Little Tools · Daily Todo";
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = GlassSurface.TransparencyLevels;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        // mutter deletes _NET_WM_STATE when the window is unmapped and Avalonia only
        // pushes these flags when they change, so a hide/show cycle lost SKIP_TASKBAR
        // and ABOVE. Re-apply them after every show, like the assistant window does.
        if (OperatingSystem.IsLinux())
        {
            PropertyChanged += (_, args) =>
            {
                if (args.Property != Visual.IsVisibleProperty || !IsVisible) return;
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsVisible) Stock.StockWindow.ReapplyWindowFlags(this, Topmost);
                }, DispatcherPriority.Background);
            };
        }
        CanResize = false;
        Width = TodoTheme.CompactWidth;
        Height = TodoTheme.CompactHeight;
        // Manual placement must be set before the window is mapped, otherwise the
        // window manager decides the position and the bottom-right anchor is lost.
        WindowStartupLocation = WindowStartupLocation.Manual;

        _compactItem = TodoTheme.Label("暂无待办", 14, TodoTheme.PrimaryText, bold: true);
        _compactItem.TextTrimming = TextTrimming.CharacterEllipsis;
        _compactItem.VerticalAlignment = VerticalAlignment.Center;
        // Windows attaches the sub item toggle to the priority text itself. A
        // transparent border gives the text a hit testable background here.
        _compactItemHost = new Border
        {
            Child = _compactItem,
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };
        _compactItemHost.PointerPressed += CompactItemPointerPressed;
        _compactProgress = TodoTheme.Label("今日完成 0 / 0", 9.5, TodoTheme.SecondaryText);
        _compactProgress.HorizontalAlignment = HorizontalAlignment.Right;
        _compactProgress.VerticalAlignment = VerticalAlignment.Bottom;

        _dateLabel = TodoTheme.Label("今天", 11.5, TodoTheme.PrimaryText, bold: true);
        _dateLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _dateLabel.VerticalAlignment = VerticalAlignment.Center;
        _dateLabel.Padding = new Thickness(10, 7, 10, 7);
        _progressLabel = TodoTheme.Label("已完成 0 / 全部 0", 9.5, TodoTheme.SecondaryText);
        _addInput = TodoTheme.InputBox("添加待办事项…");
        _importButton = TodoTheme.TextButton("处理昨日事项", 10, 88);

        _compactView = BuildCompactView();
        _expandedView = BuildExpandedView();

        _backlogFooter = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 6 };
        _backlogHost = BuildBacklogHost();
        _backlogHost.IsVisible = false;

        _root = new Grid();
        _root.Children.Add(_compactView);
        _root.Children.Add(_expandedView);
        _root.Children.Add(_backlogHost);
        // Windows sets MinimalScrollBarStyle on the todo root, so the capsule list,
        // the card deck and the backlog drawer all use the thin bar.
        TodoTheme.ApplyMinimalScrollBarStyle(_root);

        _shell = new Border
        {
            Child = _root,
            CornerRadius = new CornerRadius(TodoTheme.ShellCornerRadius),
            Background = TodoTheme.ShellBackground,
            BorderBrush = TodoTheme.ShellBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 10),
            BoxShadow = TodoTheme.ShellShadow
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
        _compactMessageTimer.Tick += (_, _) => RotateCompactMessage();
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            _undoEntries.Clear();
            if (_backlogHost.IsVisible) RenderBacklog();
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
        Closing += (_, _) =>
        {
            // Windows stops every timer on Closed; the message rotation would
            // otherwise keep ticking on a window that is gone.
            _compactMessageTimer.Stop();
            _saveTimer.Stop();
            _edgeHideTimer.Stop();
            _recurringTimer.Stop();
            _focusTimer.Stop();
            _undoTimer.Stop();
            SaveNow();
        };
        // X11 completes a window move asynchronously, so the platform position is
        // adopted from the notification instead of being read back after the drag.
        PositionChanged += (_, args) =>
        {
            var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
            var logical = new Point(args.Point.X / scaling, args.Point.Y / scaling);
            if (Math.Abs(logical.X - _logicalPosition.X) < 1 && Math.Abs(logical.Y - _logicalPosition.Y) < 1) return;
            _logicalPosition = logical;
            if (_pendingCompactClick) _compactMoved = true;
            if (_expanded) _backlogTab?.PositionBesideOwner();
        };

        NormalizeFocus();
        EnsureRecurring(_clock.Today);
        RenderAll();
        StartTimers();
        if (_data.FocusTimer is not null) _focusTimer.Start();
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

    public void SetEdgeHideEnabled(bool enabled) => SetEdgeHideEnabledInternal(enabled);

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
            // Windows: rows 18 / 29 / sub items / *, a 33px left inset for the tick
            // and a 31px right inset for the focus ring.
            ColumnDefinitions = new ColumnDefinitions("33,*,31"),
            Background = Brushes.Transparent
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
        // Windows keeps a RowDefinition for the sub item list and grows it in
        // RenderCompactSubItems, so the capsule height follows the row count.
        grid.RowDefinitions.Add(_compactSubItemsRow);
        grid.RowDefinitions.Add(new RowDefinition());

        var title = TodoTheme.Label("当前事项", 10, TodoTheme.SecondaryText, bold: true);
        title.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(title, 0);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        Grid.SetRow(_compactItemHost, 1);
        Grid.SetColumn(_compactItemHost, 1);
        grid.Children.Add(_compactItemHost);

        _compactCheck = new Button
        {
            Content = string.Empty,
            Width = 25,
            Height = 25,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            FontSize = 10,
            FontFamily = TodoTheme.UiFont,
            Foreground = TodoTheme.PrimaryText,
            Background = TodoTheme.ControlBackground,
            BorderBrush = TodoTheme.ControlBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        TodoTheme.ApplyPressFeedback(_compactCheck);
        _compactCheck.Click += (_, _) => CompactCheckClick();
        Grid.SetRow(_compactCheck, 1);
        Grid.SetColumn(_compactCheck, 0);
        grid.Children.Add(_compactCheck);

        _compactFocusButton = TodoTheme.IconButton(new FocusRingIcon { Width = 22, Height = 22 }, 27);
        // Windows builds both focus buttons from one factory that sets Cursors.Hand,
        // so the capsule ring must show the hand cursor too.
        _compactFocusButton.Cursor = new Cursor(StandardCursorType.Hand);
        _compactFocusButton.HorizontalAlignment = HorizontalAlignment.Right;
        _compactFocusButton.VerticalAlignment = VerticalAlignment.Center;
        _compactFocusButton.Click += (_, _) => OpenFocusDial();
        Grid.SetRow(_compactFocusButton, 1);
        Grid.SetColumn(_compactFocusButton, 2);
        grid.Children.Add(_compactFocusButton);

        // Windows: a scroller with a 28,2,5,1 inset holding one row per sub item.
        _compactSubItemsPanel.Orientation = Orientation.Vertical;
        var subItemsScroll = new ScrollViewer
        {
            Content = _compactSubItemsPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(28, 2, 5, 1)
        };
        Grid.SetRow(subItemsScroll, 2);
        Grid.SetColumnSpan(subItemsScroll, 3);
        grid.Children.Add(subItemsScroll);

        _compactProgress.VerticalAlignment = VerticalAlignment.Bottom;
        _compactProgress.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(_compactProgress, 3);
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
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("36,36,*,36,36"), Background = Brushes.Transparent };
        var previous = TodoTheme.TextButton(string.Empty, 10, 32);
        previous.Content = TodoIcons.Chevron(true);
        previous.Click += (_, _) => ShowDate(_viewedDate.AddDays(-1));
        Grid.SetColumn(previous, 0);
        header.Children.Add(previous);

        var todayButton = TodoTheme.TextButton("今", 10, 30);
        todayButton.Click += (_, _) => ShowDate(_clock.Today);
        Grid.SetColumn(todayButton, 1);
        header.Children.Add(todayButton);

        var dateHost = new Border { Child = _dateLabel, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        dateHost.PointerReleased += (_, args) =>
        {
            if (args.InitialPressMouseButton != MouseButton.Left) return;
            args.Handled = true;
            OpenDateChooser();
        };
        Grid.SetColumn(dateHost, 2);
        header.Children.Add(dateHost);

        var next = TodoTheme.TextButton(string.Empty, 10, 32);
        next.Content = TodoIcons.Chevron(false);
        next.Click += (_, _) => ShowDate(_viewedDate.AddDays(1));
        Grid.SetColumn(next, 3);
        header.Children.Add(next);

        var collapse = TodoTheme.TextButton("—", 10, 30);
        collapse.Click += (_, _) => Collapse();
        Grid.SetColumn(collapse, 4);
        header.Children.Add(collapse);

        var headerHost = new Border { Child = header, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Arrow) };
        headerHost.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(headerHost).Properties.IsLeftButtonPressed) return;
            // Pressing a header button or the date label must click it, not start a
            // window move, matching HeaderMouseLeftButtonDown.
            if (IsInsideButton(args.Source) || IsInsideDateLabel(args.Source)) return;
            try { BeginMoveDrag(args); } catch { }
        };
        Grid.SetRow(headerHost, 0);
        grid.Children.Add(headerHost);

        // Add row.
        var addRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,42"), Margin = new Thickness(0, 4, 0, 7) };
        Grid.SetColumn(_addInput, 0);
        addRow.Children.Add(_addInput);
        var addButton = TodoTheme.TextButton("＋", 10, 36);
        addButton.Margin = new Thickness(6, 0, 0, 0);
        addButton.Click += (_, _) => AddTodo();
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(addButton);
        Grid.SetRow(addRow, 1);
        grid.Children.Add(addRow);

        // Card deck.
        _cardCanvas.Width = 372;
        _cardCanvas.ClipToBounds = false;
        _cardScroll = new ScrollViewer
        {
            Content = _cardCanvas,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 2, 0, 3)
        };
        _cardScroll.SizeChanged += (_, _) =>
        {
            if (_cardScroll.Viewport.Width > 40) _cardCanvas.Width = _cardScroll.Viewport.Width;
        };
        Grid.SetRow(_cardScroll, 2);
        grid.Children.Add(_cardScroll);

        // Footer.
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        _progressLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_progressLabel, 0);
        footer.Children.Add(_progressLabel);
        Grid.SetColumn(_importButton, 1);
        footer.Children.Add(_importButton);
        var rulesButton = TodoTheme.TextButton("周期性任务", 10, 88);
        rulesButton.Margin = new Thickness(7, 0, 0, 0);
        rulesButton.Click += (_, _) => OpenRecurringRules();
        Grid.SetColumn(rulesButton, 2);
        footer.Children.Add(rulesButton);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);

        return grid;
    }

    private Border BuildBacklogHost()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("42,*,44") };
        // Windows' drawer header is just the title; the side tab toggles it.
        _backlogTitle = TodoTheme.Label("堆积事项", 12.5, TodoTheme.PrimaryText, bold: true);
        _backlogTitle.FontSize = 12.5;
        _backlogTitle.FontWeight = FontWeight.SemiBold;
        _backlogTitle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(_backlogTitle, 0);
        grid.Children.Add(_backlogTitle);

        var scroll = new ScrollViewer
        {
            Content = _backlogList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 1, 0, 5)
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        _backlogFooter.Margin = new Thickness(1, 6, 1, 0);
        Grid.SetRow(_backlogFooter, 2);
        grid.Children.Add(_backlogFooter);

        return new Border
        {
            Child = new Border { Child = grid, Margin = new Thickness(14, 12, 12, 11) },
            CornerRadius = new CornerRadius(15),
            Background = TodoTheme.DialogBackground,
            BorderBrush = TodoTheme.DialogBorder,
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 3, Color = Color.FromArgb(61, 0, 0, 0) }),
            ZIndex = 20
        };
    }

    private void RenderAll()
    {
        RenderCompact();
        // Windows renders the expanded view with animateCards: true, so the deck
        // re-runs its staggered entrance after every change.
        RenderExpanded(true);
        if (_backlogHost.IsVisible) RenderBacklog();
    }

    /// <summary>Drives the capsule completion for the render smoke test.</summary>
    internal void BeginCompactCompletionForSmoke() => CompactCheckClick();

    /// <summary>Smoke hook: opens (or closes) a card's sub item panel.</summary>
    internal void ShowSubItemsForSmoke(string? itemId, bool revealInput)
    {
        _expandedSubItemOwnerId = itemId;
        _revealedSubItemInputOwnerId = revealInput ? itemId : null;
        RenderExpanded(false);
    }

    /// <summary>
    /// Smoke hook: adds items to the open day after the window is already laid
    /// out, which is the path that has to grow the deck and reveal the card bar
    /// without any further interaction.
    /// </summary>
    internal void GrowDeckForSmoke(int count, string prefix)
    {
        if (TodoLogic.FindDay(_data, _viewedDate, true) is not { } day) return;
        for (var index = 0; index < count; index++)
            TodoLogic.AddItem(day, prefix + (index + 1), _clock, _ids);
        RenderAll();
    }

    /// <summary>
    /// Smoke hook: the realized height of the open sub item panel and the top of
    /// the card below it, so the render can be checked against the Windows
    /// CardHeightAt / CardTopAt values.
    /// </summary>
    internal string DescribeOpenPanelForSmoke()
    {
        if (TodoLogic.FindDay(_data, _viewedDate, false) is not { } day) return "no-day";
        var ownerIndex = -1;
        for (var index = 0; index < day.Items.Count; index++)
            if (day.Items[index].Id == _expandedSubItemOwnerId) { ownerIndex = index; break; }
        if (ownerIndex < 0) return "no-owner";
        if (!_cards.TryGetValue(day.Items[ownerIndex].Id!, out var ownerControl) || ownerControl is not Border owner)
            return "no-card";
        var nextTop = -1.0;
        if (ownerIndex + 1 < day.Items.Count
            && _cards.TryGetValue(day.Items[ownerIndex + 1].Id!, out var nextControl)
            && nextControl is Border nextCard)
            nextTop = Canvas.GetTop(nextCard);
        return "ownerHeight=" + owner.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",nextTop=" + nextTop.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Smoke hook: folds or unfolds the capsule sub item list.</summary>
    internal void SetCompactSubItemsForSmoke(bool show)
    {
        _data.ShowCompactSubItems = show;
        RenderCompact();
    }

    /// <summary>
    /// Smoke hook: describes the realized scroll bars, so the ported Windows
    /// MinimalScrollBarStyle can be verified from the render run. The Windows grip
    /// is the Border inside the thumb template, which carries the 1,2 inset and
    /// the 3px radius; the thumb itself only carries the 26px minimum length.
    /// </summary>
    internal string DescribeScrollBarsForSmoke()
    {
        var parts = new List<string>();
        foreach (var bar in _root.GetVisualDescendants().OfType<ScrollBar>()
                     .Where(candidate => candidate.Orientation == Orientation.Vertical))
        {
            var thumb = bar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
            if (thumb is null) continue;
            var grip = thumb.GetVisualDescendants().OfType<Border>().FirstOrDefault();
            var color = (grip?.Background ?? thumb.Background) is ISolidColorBrush solid
                ? solid.Color.ToString()
                : "null";
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            parts.Add("width=" + bar.Width.ToString(culture)
                + ",minHeight=" + thumb.MinHeight.ToString(culture)
                + ",radius=" + (grip?.CornerRadius.TopLeft ?? thumb.CornerRadius.TopLeft).ToString(culture)
                + ",margin=" + (grip?.Margin ?? thumb.Margin).ToString()
                + ",thumb=" + color);
        }
        return parts.Count == 0 ? "none" : string.Join(" | ", parts.Distinct());
    }

    /// <summary>
    /// Smoke hook: the live geometry of the card scroller, so the smoke test can
    /// prove that a deck which grows past the window realizes a full sized bar
    /// without any further interaction. The bar is matched through its templated
    /// parent: every card's editable text also owns a ScrollViewer, so a plain
    /// descendant search finds a text box bar instead.
    /// </summary>
    internal string DescribeCardScrollForSmoke()
    {
        var bar = CardScrollBar();
        var thumb = bar?.GetVisualDescendants().OfType<Thumb>().FirstOrDefault();
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return "extent=" + _cardScroll.Extent.Height.ToString(culture)
            + ",viewport=" + _cardScroll.Viewport.Height.ToString(culture)
            + ",offset=" + _cardScroll.Offset.Y.ToString(culture)
            + ",canvas=" + _cardCanvas.Height.ToString(culture)
            + ",barVisible=" + (bar?.IsVisible == true)
            + ",barMaximum=" + (bar?.Maximum ?? -1).ToString(culture)
            + ",barViewport=" + (bar?.ViewportSize ?? -1).ToString(culture)
            + ",barAutoHide=" + (bar?.AllowAutoHide == true)
            + ",barExpanded=" + (bar?.IsExpanded == true)
            + ",thumbVisible=" + (thumb?.IsVisible == true)
            + ",thumbMinHeight=" + (thumb?.MinHeight ?? -1).ToString(culture)
            + ",thumbTop=" + (thumb?.Bounds.Y ?? -1).ToString(culture)
            + ",thumbHeight=" + (thumb?.Bounds.Height ?? -1).ToString(culture)
            + ",thumbWidth=" + (thumb?.Bounds.Width ?? -1).ToString(culture)
            + ",thumbTransform=" + (thumb?.RenderTransform is null ? "none" : thumb.RenderTransform.ToString());
    }

    /// <summary>
    /// Smoke hook: fires the card scroller's invisible paging button, so the smoke
    /// run proves the transparent track still pages. The Windows template keeps the
    /// RepeatButtons but draws nothing, and a code built template only wires them
    /// when their names are registered.
    /// </summary>
    internal void PageCardScrollForSmoke(bool down)
    {
        var name = down ? "PART_PageDownButton" : "PART_PageUpButton";
        var button = CardScrollBar()?.GetVisualDescendants().OfType<RepeatButton>()
            .FirstOrDefault(candidate => candidate.Name == name);
        if (button is null) throw new InvalidOperationException("Card scroll bar has no " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private ScrollBar? CardScrollBar() => _cardScroll.GetVisualDescendants().OfType<ScrollBar>()
        .FirstOrDefault(candidate => candidate.Orientation == Orientation.Vertical
            && ReferenceEquals(candidate.TemplatedParent, _cardScroll));

    /// <summary>The backlog tab window, so the smoke test can render it too.</summary>
    internal Window? BacklogTabForSmoke => _backlogTab;

    private void RenderCompact()
    {
        // A render during the completion animation would wipe the tick and the
        // strike, so the Windows method bails out here.
        if (_compactCompleting) return;

        var today = TodoLogic.FindDay(_data, _clock.Today, false);
        var current = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        var total = today?.Items.Count ?? 0;
        var completed = today is null ? 0 : TodoLogic.CountCompleted(today);

        _compactItem.TextDecorations = null;
        _compactItem.Text = _compactMessages.Update(current, total, _data.BacklogItems.Count);
        // Windows uses 14pt for an item and 13.2pt for a status message.
        _compactItem.FontSize = current is not null ? 14 : 13.2;
        if (current is not null) _compactMessageTimer.Stop();
        else if (!_compactMessageTimer.IsEnabled) _compactMessageTimer.Start();

        // With nothing left to do the Windows capsule hides both buttons.
        _compactCheck.IsVisible = current is not null;
        _compactFocusButton.IsVisible = current is not null;
        UpdateFocusIcons();

        var childProgress = string.Empty;
        if (current is not null && current.SubItems.Count > 0)
        {
            var childCompleted = current.SubItems.Count(subItem => subItem.Completed);
            childProgress = " · 子事项 " + childCompleted + "/" + current.SubItems.Count;
        }
        _compactProgress.Text = "今日完成 " + completed + " / " + total + childProgress;
        RenderCompactSubItems(current);
    }

    /// <summary>Windows RotateCompactMessage: advances the line every five minutes.</summary>
    private void RotateCompactMessage()
    {
        var current = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        if (_compactMessages.Rotate(current) is { } text) _compactItem.Text = text;
    }

    /// <summary>Windows RenderCompactSubItems: the tickable list under the capsule text.</summary>
    private void RenderCompactSubItems(DailyTodoItem? item)
    {
        _compactSubItemsPanel.Children.Clear();
        var count = item?.SubItems.Count ?? 0;
        var showSubItems = count > 0 && _data.ShowCompactSubItems;
        _compactItemHost.Cursor = new Cursor(count > 0 ? StandardCursorType.Hand : StandardCursorType.Arrow);
        ToolTip.SetTip(_compactItemHost, count == 0
            ? null
            : _data.ShowCompactSubItems ? "点击隐藏子事项" : "点击显示子事项");

        var subItemsHeight = TodoLogic.CompactSubItemsHeight(count, _data.ShowCompactSubItems);
        _compactSubItemsRow.Height = new GridLength(subItemsHeight);

        if (showSubItems && item is not null)
        {
            foreach (var subItem in item.SubItems)
                _compactSubItemsPanel.Children.Add(BuildCompactSubItemRow(subItem));
        }

        if (_expanded) return;
        // Windows keeps the bottom edge anchored while the capsule grows.
        var desiredHeight = TodoLogic.CompactHeightFor(item, _data.ShowCompactSubItems);
        if (Math.Abs(Height - desiredHeight) > 0.5)
            ResizeAnchored(TodoTheme.CompactWidth, desiredHeight);
    }

    /// <summary>Windows RenderCompactSubItems row: an 18px tick and the sub item text.</summary>
    private Control BuildCompactSubItemRow(TodoSubItem subItem)
    {
        var row = new Grid
        {
            Height = TodoLogic.CompactSubItemRowHeight,
            ColumnDefinitions = new ColumnDefinitions("25,*")
        };

        var check = TodoTheme.TextButton(subItem.Completed ? "✓" : string.Empty, 8, 18);
        check.Height = 18;
        check.FontSize = 8;
        check.Padding = new Thickness(0);
        check.Background = Brushes.Transparent;
        check.BorderBrush = TodoTheme.CompactSubItemBorder;
        check.CornerRadius = new CornerRadius(9);
        check.VerticalAlignment = VerticalAlignment.Center;
        check.Click += (_, _) => ToggleSubItem(subItem);
        row.Children.Add(check);

        var text = TodoTheme.Label(subItem.Text ?? string.Empty, 10.5,
            subItem.Completed ? TodoTheme.SecondaryText : TodoTheme.PrimaryText);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        if (subItem.Completed) text.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>Windows ToggleSubItem: flips one sub item and refreshes both views.</summary>
    private void ToggleSubItem(TodoSubItem subItem)
    {
        TodoLogic.ToggleSubItem(subItem);
        _store.Save(_data);
        RenderCompact();
        RenderExpanded(false);
    }

    /// <summary>Windows' capsule text click: remembers whether the list is unfolded.</summary>
    private void CompactItemPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(_compactItemHost).Properties.IsLeftButtonPressed) return;
        var current = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        if (current is null || current.SubItems.Count == 0) return;
        _data.ShowCompactSubItems = !_data.ShowCompactSubItems;
        _store.Save(_data);
        RenderCompact();
        args.Handled = true;
    }

    private void RenderExpanded(bool animateCards = true)
    {
        _dateLabel.Text = _viewedDate == _clock.Today
            ? "今天 · " + _viewedDate.ToString("M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"))
            : _viewedDate.ToString("M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

        var day = TodoLogic.FindDay(_data, _viewedDate, true)!;
        _importButton.IsVisible = _viewedDate == _clock.Today && TodoLogic.ImportCandidates(_data, _clock.Today).Count > 0;
        RenderCards(day, animateCards);
        UpdateExpandedProgress(day);
    }

    private void UpdateExpandedProgress(TodoDay day)
    {
        _progressLabel.Text = $"已完成 {TodoLogic.CountCompleted(day)} / 全部 {day.Items.Count}";
    }

    private void RenderCards(TodoDay day, bool animateCards)
    {
        _cardCanvas.Children.Clear();
        _cards.Clear();
        _activeSubItemInput = null;
        _cardCanvas.Width = Math.Max(330, Width - 30);
        // Windows drops a stale panel owner before it measures the deck.
        if (!string.IsNullOrEmpty(_expandedSubItemOwnerId)
            && !day.Items.Any(candidate => candidate.Id == _expandedSubItemOwnerId))
            _expandedSubItemOwnerId = null;
        _cardCanvas.Height = TodoLogic.CardCanvasHeight(day.Items, _expandedSubItemOwnerId,
            _revealedSubItemInputOwnerId);

        if (day.Items.Count == 0)
        {
            var empty = TodoTheme.Label(
                _viewedDate == _clock.Today ? "写下今天最重要的一件事" : "这一天还没有待办",
                12, TodoTheme.SecondaryText);
            Canvas.SetLeft(empty, 14);
            Canvas.SetTop(empty, 28);
            _cardCanvas.Children.Add(empty);
            return;
        }

        for (var index = 0; index < day.Items.Count; index++)
        {
            var item = day.Items[index];
            var card = CreateCard(day, item, index);
            // Windows CardTopAt pushes the cards below an expanded panel down.
            Canvas.SetTop(card, TodoLogic.CardTopAt(index, day.Items, _expandedSubItemOwnerId,
                _revealedSubItemInputOwnerId));
            _cardCanvas.Children.Add(card);
            _cards[item.Id!] = card;
        }

        if (animateCards) AnimateCardEntrance();
    }

    private Control CreateCard(TodoDay day, DailyTodoItem item, int index)
    {
        var inset = TodoLogic.CardInset(index);
        var width = Math.Max(300, _cardCanvas.Width - 4 - inset * 2);
        var subItemsExpanded = _expandedSubItemOwnerId == item.Id;

        // Windows card content: a 34 tall main row, a 22 tall sub item footer and,
        // while the panel is open, the sub item list itself.
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        if (subItemsExpanded) content.RowDefinitions.Add(new RowDefinition());

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
        var (textHost, text, strike) = BuildEditableText(item.Text, item.Completed, 12.5,
            item.Completed ? FontWeight.Normal : FontWeight.SemiBold, new Thickness(2, 1, 4, 1));
        text.TextChanged += (_, _) =>
        {
            item.Text = text.Text ?? string.Empty;
            SyncStrike(text, strike);
            QueueSave();
            RenderCompact();
        };
        Grid.SetColumn(textHost, 1);
        grid.Children.Add(textHost);

        var focusIcon = new FocusRingIcon { Width = 22, Height = 22 };
        var focusButton = TodoTheme.IconButton(focusIcon, 27);
        focusButton.Cursor = new Cursor(StandardCursorType.Hand);
        focusButton.Tag = item.Id;
        focusButton.VerticalAlignment = VerticalAlignment.Center;
        focusButton.IsVisible = IsFocusRingVisible(day, item);
        focusButton.Click += (_, _) => OpenFocusDial();
        Grid.SetColumn(focusButton, 2);
        grid.Children.Add(focusButton);

        var backlogButton = TodoTheme.IconButton(new StackedItemsIcon { Width = 18, Height = 18 }, 26);
        backlogButton.Height = 25;
        ToolTip.SetTip(backlogButton, "放入堆积事项");
        backlogButton.IsVisible = false;
        backlogButton.Click += (_, _) => MoveToBacklog(day, item);
        Grid.SetColumn(backlogButton, 3);
        grid.Children.Add(backlogButton);

        // A Button would swallow PointerPressed in its own class handler, so the
        // drag handle is a Border like the Windows module's handle element.
        var handle = new Border
        {
            Width = 27,
            Height = 27,
            Background = Brushes.Transparent,
            // Windows uses the plain arrow on the handle; SizeAll renders as an
            // anonymous box on some Linux cursor themes.
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = new DragHandleIcon { Width = 9, Height = 15 },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = !item.Completed
        };
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

        content.Children.Add(grid);
        var subFooter = BuildSubItemFooter(item, subItemsExpanded);
        Grid.SetRow(subFooter, 1);
        content.Children.Add(subFooter);
        if (subItemsExpanded)
        {
            var subPanel = BuildSubItemsPanel(item);
            Grid.SetRow(subPanel, 2);
            content.Children.Add(subPanel);
        }

        var border = new Border
        {
            Child = content,
            Width = width,
            Height = TodoLogic.CardHeightAt(item, _expandedSubItemOwnerId, _revealedSubItemInputOwnerId),
            CornerRadius = new CornerRadius(TodoTheme.CardCornerRadius),
            Background = TodoTheme.CardBackground(index),
            BorderBrush = TodoTheme.CardBorder,
            BorderThickness = new Thickness(1),
            Padding = TodoTheme.CardPadding,
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

    /// <summary>
    /// Avalonia's TextBox has no TextDecorations, so the Windows strikethrough is
    /// an overlay line measured from the editable text.
    /// </summary>
    private static (Grid Host, TextBox Box, Border Strike) BuildEditableText(string? value, bool completed,
        double fontSize, FontWeight weight, Thickness padding)
    {
        var host = new Grid();
        var box = new TextBox
        {
            Text = value,
            FontSize = fontSize,
            FontFamily = TodoTheme.UiFont,
            FontWeight = weight,
            Foreground = completed ? TodoTheme.SecondaryText : TodoTheme.PrimaryText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = padding,
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
            Margin = new Thickness(padding.Left, 0, 0, 0),
            IsHitTestVisible = false,
            IsVisible = completed
        };
        host.Children.Add(box);
        host.Children.Add(strike);
        SyncStrike(box, strike);
        return (host, box, strike);
    }

    private static void SyncStrike(TextBox box, Border strike)
    {
        var formatted = new FormattedText(box.Text ?? string.Empty,
            System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(TodoTheme.UiFont, FontStyle.Normal, FontWeight.Normal), box.FontSize,
            TodoTheme.SecondaryText);
        strike.Width = formatted.Width;
    }

    /// <summary>
    /// Windows sub item footer: the expand chevron and the x/y progress. A completed
    /// item with no sub items hides the chevron, and completed items keep the panel
    /// readable but lose the add control.
    /// </summary>
    private Control BuildSubItemFooter(DailyTodoItem item, bool subItemsExpanded)
    {
        var footer = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var toggle = new Button
        {
            Content = subItemsExpanded ? "⌄" : "›",
            Width = 22,
            Height = 20,
            Padding = new Thickness(0, 0, 0, 1),
            FontSize = 13,
            FontFamily = TodoTheme.UiFont,
            Foreground = TodoTheme.SecondaryText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = !(item.Completed && item.SubItems.Count == 0)
        };
        ToolTip.SetTip(toggle, subItemsExpanded ? "收起子事项" : "展开子事项");
        TodoTheme.ApplyPressFeedback(toggle);
        toggle.Click += (_, _) =>
        {
            _expandedSubItemOwnerId = subItemsExpanded ? null : item.Id;
            _revealedSubItemInputOwnerId = null;
            RenderExpanded(false);
        };
        footer.Children.Add(toggle);

        var subCompleted = item.SubItems.Count(subItem => subItem.Completed);
        var progress = TodoTheme.Label(
            item.SubItems.Count == 0 ? string.Empty : subCompleted + "/" + item.SubItems.Count,
            9.5,
            item.SubItems.Count > 0 && subCompleted == item.SubItems.Count
                ? TodoTheme.SubItemDone
                : TodoTheme.SecondaryText);
        progress.IsVisible = item.SubItems.Count > 0;
        Grid.SetColumn(progress, 1);
        footer.Children.Add(progress);
        return footer;
    }

    /// <summary>Windows BuildSubItemsPanel: separator, one 30px row per sub item, then the add control.</summary>
    private Control BuildSubItemsPanel(DailyTodoItem item)
    {
        var panel = new Grid { Margin = new Thickness(1, 3, 2, 0) };
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = TodoTheme.SubItemSeparator,
            VerticalAlignment = VerticalAlignment.Top
        });

        var rowIndex = 1;
        foreach (var subItem in item.SubItems)
        {
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            var row = BuildSubItemRow(item, subItem);
            Grid.SetRow(row, rowIndex++);
            panel.Children.Add(row);
        }

        // Windows offers no add control on a completed item.
        if (item.Completed) return panel;
        var inputVisible = _revealedSubItemInputOwnerId == item.Id;
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(inputVisible ? 31 : 24) });
        var addControl = inputVisible ? BuildSubItemInput(item) : BuildRevealSubItemInput(item);
        Grid.SetRow(addControl, rowIndex);
        panel.Children.Add(addControl);
        return panel;
    }

    /// <summary>Windows BuildRevealSubItemInput: the small, dimmed plus button.</summary>
    private Control BuildRevealSubItemInput(DailyTodoItem item)
    {
        var reveal = new Button
        {
            Content = "＋",
            Width = 22,
            Height = 18,
            Margin = new Thickness(31, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(0),
            FontSize = 11,
            FontFamily = TodoTheme.UiFont,
            Foreground = TodoTheme.SecondaryText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Opacity = 0.62,
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(reveal, "添加子事项");
        TodoTheme.ApplyPressFeedback(reveal);
        reveal.Click += (_, _) =>
        {
            _revealedSubItemInputOwnerId = item.Id;
            RenderExpanded(false);
            FocusSubItemInput(item.Id);
        };
        return reveal;
    }

    /// <summary>Windows BuildSubItemInput: the inline "添加一步…" field.</summary>
    private Control BuildSubItemInput(DailyTodoItem item)
    {
        var host = new Border
        {
            Height = 25,
            Margin = new Thickness(32, 3, 28, 3),
            Background = TodoTheme.SubItemInputBackground,
            BorderBrush = TodoTheme.SubItemInputBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(5)
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,23") };

        var placeholder = TodoTheme.Label("添加一步…", 9.5, TodoTheme.SubItemPlaceholder);
        placeholder.Margin = new Thickness(7, 0, 0, 0);
        placeholder.IsHitTestVisible = false;

        var input = new TextBox
        {
            FontSize = 10.5,
            FontFamily = TodoTheme.UiFont,
            Foreground = TodoTheme.PrimaryText,
            CaretBrush = TodoTheme.PrimaryText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 1, 3, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = false
        };
        _activeSubItemInput = input;
        input.TextChanged += (_, _) =>
            placeholder.IsVisible = (input.Text ?? string.Empty).Length == 0 && !input.IsKeyboardFocusWithin;
        input.GotFocus += (_, _) => placeholder.IsVisible = false;
        input.LostFocus += (_, _) =>
            placeholder.IsVisible = (input.Text ?? string.Empty).Length == 0;

        void AddSubItem()
        {
            var value = (input.Text ?? string.Empty).Trim();
            if (value.Length == 0) return;
            item.SubItems.Add(new TodoSubItem
            {
                Id = _ids.NewId(), Text = value, CreatedAt = _clock.Now
            });
            _revealedSubItemInputOwnerId = item.Id;
            _store.Save(_data);
            RenderCompact();
            RenderExpanded(false);
            FocusSubItemInput(item.Id);
        }

        input.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                AddSubItem();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape)
            {
                _revealedSubItemInputOwnerId = null;
                RenderExpanded(false);
                args.Handled = true;
            }
        };

        grid.Children.Add(placeholder);
        grid.Children.Add(input);
        var enterHint = TodoTheme.Label("↵", 10, TodoTheme.SubItemHint);
        enterHint.HorizontalAlignment = HorizontalAlignment.Center;
        enterHint.IsHitTestVisible = false;
        Grid.SetColumn(enterHint, 1);
        grid.Children.Add(enterHint);
        host.Child = grid;
        return host;
    }

    /// <summary>Windows BuildSubItemRow: a 21px tick, the editable text and delete.</summary>
    private Control BuildSubItemRow(DailyTodoItem owner, TodoSubItem subItem)
    {
        var row = new Grid
        {
            Margin = new Thickness(25, 2, 1, 1),
            ColumnDefinitions = new ColumnDefinitions("29,*,28")
        };

        var check = TodoTheme.TextButton(subItem.Completed ? "✓" : string.Empty, 9, 21);
        check.Height = 21;
        check.FontSize = 9;
        check.Foreground = subItem.Completed ? TodoTheme.PrimaryText : TodoTheme.SecondaryText;
        check.VerticalAlignment = VerticalAlignment.Center;
        check.Click += (_, _) => ToggleSubItem(subItem);
        row.Children.Add(check);

        var (textHost, text, strike) = BuildEditableText(subItem.Text, subItem.Completed, 10.5,
            FontWeight.Normal, new Thickness(1, 0, 4, 0));
        text.TextChanged += (_, _) =>
        {
            subItem.Text = text.Text ?? string.Empty;
            SyncStrike(text, strike);
            QueueSave();
        };
        Grid.SetColumn(textHost, 1);
        row.Children.Add(textHost);

        var delete = TodoTheme.TextButton("×", 9, 22);
        delete.Height = 21;
        delete.FontSize = 9;
        delete.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(delete, "删除子事项");
        delete.Click += (_, _) =>
        {
            owner.SubItems.Remove(subItem);
            if (owner.Completed && owner.SubItems.Count == 0) _expandedSubItemOwnerId = null;
            _store.Save(_data);
            RenderAll();
        };
        Grid.SetColumn(delete, 2);
        row.Children.Add(delete);
        return row;
    }

    /// <summary>Windows FocusSubItemInput: focus after the panel has been rebuilt.</summary>
    private void FocusSubItemInput(string? itemId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_expandedSubItemOwnerId != itemId || _revealedSubItemInputOwnerId != itemId
                || _activeSubItemInput is null) return;
            _activeSubItemInput.Focus();
            _activeSubItemInput.CaretIndex = (_activeSubItemInput.Text ?? string.Empty).Length;
        }, DispatcherPriority.Input);
    }

    /// <summary>Staggered entrance used when the deck unfolds, like the Windows module.</summary>
    private void AnimateCardEntrance()
    {
        var index = 0;
        foreach (var child in _cardCanvas.Children)
        {
            // Windows AnimateCard: a 210ms fade and a 240ms slide, delayed by 38ms
            // per card up to eight cards.
            if (child is Visual visual)
                TodoAnim.Materialize(visual, 13, 210, Math.Min(index, 8) * 38, slideMilliseconds: 240);
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
        var grabOffset = 0.0;

        handle.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            if (item.Completed || _cardAnimating) return;
            dragging = true;
            var point = args.GetPosition(_cardCanvas);
            var top = Canvas.GetTop(card);
            // Remember where inside the card the drag started (Windows BeginCardDrag)
            // so the card follows the pointer faithfully.
            grabOffset = point.Y - (double.IsNaN(top) ? 0 : top);
            card.ZIndex = 10000;
            card.Opacity = 0.96;
            card.BoxShadow = TodoTheme.CardShadow(0, lifted: true);
            args.Pointer.Capture(handle);
            args.Handled = true;
        };

        handle.PointerMoved += (_, args) =>
        {
            if (!dragging) return;
            var point = args.GetPosition(_cardCanvas);
            _backlogTab?.SetDropHighlight(IsOverBacklogTab(point));

            var openCount = TodoLogic.FirstCompletedIndex(day.Items);
            if (openCount <= 0) return;
            var maximumTop = TodoLogic.CardTopAt(openCount - 1, day.Items, _expandedSubItemOwnerId,
                _revealedSubItemInputOwnerId);
            var top = Math.Max(0, Math.Min(point.Y - grabOffset, maximumTop));
            Canvas.SetTop(card, top);

            // Windows reorders the model while dragging and slides the other cards
            // into their new slots, so releasing has nothing left to reorder.
            var desired = TodoLogic.ResolveDropIndex(top, day.Items, openCount, _expandedSubItemOwnerId,
                _revealedSubItemInputOwnerId);
            var current = day.Items.IndexOf(item);
            if (current < 0 || current == desired) return;
            day.Items.RemoveAt(current);
            day.Items.Insert(desired, item);
            AnimateCardsToCurrentOrder(day, card);
            args.Handled = true;
        };

        handle.PointerReleased += (_, args) =>
        {
            if (!dragging) return;
            dragging = false;
            args.Pointer.Capture(null);
            card.Opacity = 1;
            var point = args.GetPosition(_cardCanvas);
            var overTab = IsOverBacklogTab(point);
            _backlogTab?.SetDropHighlight(false);

            if (overTab)
            {
                MoveToBacklog(day, item);
                args.Handled = true;
                return;
            }

            var index = Math.Max(0, day.Items.IndexOf(item));
            card.BoxShadow = TodoTheme.CardShadow(index);
            card.ZIndex = day.Items.Count - index;
            AnimateCardsToCurrentOrder(day, card);
            AnimateCardToIndex(card, index, day.Items);
            _store.Save(_data);
            RenderCompact();
            UpdateFocusIcons();
            args.Handled = true;
        };
    }

    /// <summary>Screen space hit test against the backlog tab, used while dragging.</summary>
    private bool IsOverBacklogTab(Point canvasPoint)
    {
        if (_backlogTab is null) return false;
        try
        {
            var screen = _cardCanvas.PointToScreen(canvasPoint);
            var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
            return _backlogTab.ContainsLogicalPoint(new Point(screen.X / scaling, screen.Y / scaling));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Slides every card except the dragged one to its slot in the new order.</summary>
    private void AnimateCardsToCurrentOrder(TodoDay day, Border dragged)
    {
        for (var index = 0; index < day.Items.Count; index++)
        {
            if (!_cards.TryGetValue(day.Items[index].Id!, out var control) || control is not Border card) continue;
            if (ReferenceEquals(card, dragged)) continue;
            card.ZIndex = day.Items.Count - index;
            card.Background = TodoTheme.CardBackground(index);
            card.BoxShadow = TodoTheme.CardShadow(index);
            AnimateCardToIndex(card, index, day.Items);
        }
    }

    /// <summary>
    /// Slides the remaining cards into their new slots over 335ms, then brings the
    /// moved card back in from 12px below with a 285ms fade, like the Windows
    /// AnimateReflowThenMaterialize.
    /// </summary>
    private void ReflowThenMaterialize(TodoDay day, DailyTodoItem moved)
    {
        for (var index = 0; index < day.Items.Count; index++)
        {
            if (!_cards.TryGetValue(day.Items[index].Id!, out var control) || control is not Border existing) continue;
            existing.ZIndex = day.Items.Count - index;
            existing.Background = TodoTheme.CardBackground(index);
            existing.BoxShadow = TodoTheme.CardShadow(index);
            AnimateCardToIndex(existing, index, day.Items, 335);
        }
        _cardCanvas.Height = TodoLogic.CardCanvasHeight(day.Items, _expandedSubItemOwnerId,
            _revealedSubItemInputOwnerId);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(335) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var index = day.Items.IndexOf(moved);
            if (index < 0)
            {
                FinishRelocation();
                return;
            }
            var movedCard = CreateCard(day, moved, index);
            movedCard.Opacity = 0;
            var targetTop = TodoLogic.CardTopAt(index, day.Items, _expandedSubItemOwnerId,
                _revealedSubItemInputOwnerId);
            Canvas.SetTop(movedCard, targetTop + 12);
            _cardCanvas.Children.Add(movedCard);
            _cards[moved.Id!] = movedCard;
            var local = movedCard;
            TodoAnim.Fade(local, 0, 1, 285);
            TodoAnim.Tween(targetTop + 12, targetTop, 315,
                value => Canvas.SetTop(local, value), FinishRelocation);
        };
        timer.Start();
    }

    private void FinishRelocation()
    {
        _cardAnimating = false;
        _cardCanvas.IsHitTestVisible = true;
        RenderExpanded(false);
    }

    /// <summary>Animates a card to the geometry its deck position implies.</summary>
    private void AnimateCardToIndex(Border card, int index, IReadOnlyList<DailyTodoItem> items,
        int milliseconds = 145)
    {
        var inset = TodoLogic.CardInset(index);
        // Windows AnimateCardPlacement uses CardTopAt, so expanded panels shift the
        // cards below them during a drag or a completion reflow.
        var targetTop = TodoLogic.CardTopAt(index, items, _expandedSubItemOwnerId, _revealedSubItemInputOwnerId);
        var targetLeft = 2 + inset;
        var targetWidth = Math.Max(300, _cardCanvas.Width - 4 - inset * 2);

        var fromTop = Canvas.GetTop(card);
        var fromLeft = Canvas.GetLeft(card);
        var fromWidth = card.Width;
        if (double.IsNaN(fromTop)) fromTop = targetTop;
        if (double.IsNaN(fromLeft)) fromLeft = targetLeft;

        if (Math.Abs(fromTop - targetTop) < 0.5 && Math.Abs(fromLeft - targetLeft) < 0.5
            && Math.Abs(fromWidth - targetWidth) < 0.5)
        {
            Canvas.SetTop(card, targetTop);
            Canvas.SetLeft(card, targetLeft);
            card.Width = targetWidth;
            return;
        }

        var local = card;
        TodoAnim.Tween(0, 1, milliseconds, value =>
        {
            Canvas.SetTop(local, fromTop + (targetTop - fromTop) * value);
            Canvas.SetLeft(local, fromLeft + (targetLeft - fromLeft) * value);
            local.Width = fromWidth + (targetWidth - fromWidth) * value;
        }, easeOut: true);
    }
    private Dictionary<string, double> CaptureCardTops()
    {
        var tops = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (id, card) in _cards)
            if (card is Control control) tops[id] = Canvas.GetTop(control);
        return tops;
    }

    /// <summary>
    /// Animates the deck into its new order: cards that were already on screen
    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Completing from the capsule shows the tick and the strike first, waits
    /// 140ms, then fades out and fades the next item in, matching CompactCheckClick.
    /// </summary>
    private void CompactCheckClick()
    {
        if (_compactCompleting) return;
        var current = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        if (current is null) return;
        var day = TodoLogic.FindDay(_data, _clock.Today, false);
        if (day is null) return;
        _compactCompleting = true;

        _compactCheck.Content = "✓";
        _compactCheck.Foreground = TodoTheme.PrimaryText;
        _compactCheck.IsEnabled = false;
        _compactItem.TextDecorations = TextDecorations.Strikethrough;

        TodoAnim.Fade(_compactItem, 1, 0, 260, () =>
        {
            TodoLogic.Complete(day, current, _data);
            // Windows drops a revealed sub item input when the owner completes.
            if (_revealedSubItemInputOwnerId == current.Id) _revealedSubItemInputOwnerId = null;
            SaveNow();

            _compactItem.TextDecorations = null;
            _compactCheck.Content = string.Empty;
            _compactCheck.Foreground = TodoTheme.SecondaryText;
            _compactCheck.IsEnabled = true;
            _compactCompleting = false;

            RenderAll();
            _compactItem.Opacity = 0;
            TodoAnim.Fade(_compactItem, 0, 1, 220);
        }, delayMilliseconds: 140);
    }

    private void CompactPointerPressed(object? sender, PointerPressedEventArgs args)
    {
        if (!args.GetCurrentPoint(_compactView).Properties.IsLeftButtonPressed) return;
        // The check and focus buttons live inside the draggable capsule.
        if (IsInsideButton(args.Source)) return;
        RevealFromEdge();
        _compactPressPosition = _logicalPosition;
        _compactMoved = false;
        _pendingCompactClick = true;
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
        // The window manager reports the new position after the request returns, so
        // the click/ move decision waits briefly for that notification.
        DispatcherTimer.RunOnce(() =>
        {
            _pendingCompactClick = false;
            var moved = _compactMoved
                || Math.Abs(_logicalPosition.X - _compactPressPosition.X) > 1
                || Math.Abs(_logicalPosition.Y - _compactPressPosition.Y) > 1;
            SnapOrHideAtEdge();
            if (!moved) Expand();
        }, TimeSpan.FromMilliseconds(180));
        args.Handled = true;
    }

    private void ToggleCompleted(TodoDay day, DailyTodoItem item, Control card)
    {
        if (_cardAnimating) return;
        _cardAnimating = true;
        var completing = !item.Completed;
        var content = (Grid)((Border)card).Child!;
        var mainRow = (Grid)content.Children[0];
        var check = (Button)mainRow.Children[0];
        var textHost = (Grid)mainRow.Children[1];
        var text = (TextBox)textHost.Children[0];
        var strike = (Border)textHost.Children[1];

        check.Content = completing ? "✓" : string.Empty;
        check.Foreground = completing ? TodoTheme.PrimaryText : TodoTheme.SecondaryText;
        text.Foreground = completing ? TodoTheme.SecondaryText : TodoTheme.PrimaryText;
        text.FontWeight = completing ? FontWeight.Normal : FontWeight.SemiBold;
        strike.IsVisible = completing;
        SyncStrike(text, strike);
        _cardCanvas.IsHitTestVisible = false;

        TodoAnim.Fade(card, card.Opacity, 0, 235, () =>
        {
            if (completing)
            {
                TodoLogic.Complete(day, item, _data);
                if (_revealedSubItemInputOwnerId == item.Id) _revealedSubItemInputOwnerId = null;
            }
            else
            {
                TodoLogic.Uncomplete(day, item);
            }
            _store.Save(_data);
            RenderCompact();
            UpdateExpandedProgress(day);
            _cardCanvas.Children.Remove(card);
            _cards.Remove(item.Id!);
            ReflowThenMaterialize(day, item);
        });
    }

    private void DeleteItem(TodoDay day, DailyTodoItem item)
    {
        TodoLogic.Delete(day, item, _data);
        if (_expandedSubItemOwnerId == item.Id) _expandedSubItemOwnerId = null;
        if (_revealedSubItemInputOwnerId == item.Id) _revealedSubItemInputOwnerId = null;
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
        if (_expandedSubItemOwnerId == item.Id) _expandedSubItemOwnerId = null;
        if (_revealedSubItemInputOwnerId == item.Id) _revealedSubItemInputOwnerId = null;
        _store.Save(_data);
        RenderAll();
    }

    // ---------------------------------------------------------------- focus

    private void OpenFocusDial()
    {
        var current = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        if (current is null) return;
        _dialogOpen = true;
        // Windows opens the dial at 0 when idle and at the remaining minutes when
        // a countdown is already running.
        var active = FocusTimerMath.IsActiveFor(_data.FocusTimer, current.Id);
        var initialMinutes = active
            ? Math.Max(1, (int)Math.Ceiling(FocusTimerMath.RemainingSeconds(_data.FocusTimer, _clock.UtcNow) / 60.0))
            : 0;
        var window = new FocusDialWindow(
            current.Text ?? string.Empty,
            initialMinutes,
            active,
            _sound,
            () => FocusTimerMath.RemainingSeconds(_data.FocusTimer, _clock.UtcNow),
            minutes =>
            {
                _data.FocusTimer = FocusTimerMath.Create(current.Id!, current.Text ?? string.Empty, minutes, _clock);
                SaveNow();
                RenderAll();
                _focusTimer.Start();
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
            Activate();
        }));
    }

    private void UpdateFocusTimer()
    {
        var timer = _data.FocusTimer;
        if (timer is null)
        {
            _lastFocusSoundSecond = -1;
            _focusTimer.Stop();
            return;
        }

        var remaining = FocusTimerMath.RemainingSeconds(timer, _clock.UtcNow);
        if (remaining <= 0)
        {
            _data.FocusTimer = null;
            _focusTimer.Stop();
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

        var compactItem = TodoLogic.CurrentTodayItem(_data, _clock.Today);
        var compactActive = compactItem is not null && FocusTimerMath.IsActiveFor(_data.FocusTimer, compactItem.Id);
        foreach (var icon in EnumerateFocusIcons(_compactView))
        {
            icon.Active = compactActive;
            icon.Progress = compactActive ? fill : 0;
            icon.Urgent = compactActive && urgent;
        }
        foreach (var icon in EnumerateFocusIcons(_expandedView))
        {
            Button? button = null;
            for (var parent = icon.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
            {
                if (parent is Button found) { button = found; break; }
            }
            var itemId = button?.Tag as string;
            var active = FocusTimerMath.IsActiveFor(_data.FocusTimer, itemId);
            icon.Active = active;
            icon.Progress = active ? fill : 0;
            icon.Urgent = active && urgent;
            if (button is not null)
                ToolTip.SetTip(button, active
                    ? "剩余 " + FocusTimerMath.FormatRemaining(remaining)
                    : "设置专注倒计时");
        }

        foreach (var icon in EnumerateFocusIcons(_compactView))
        {
            Button? button = null;
            for (var parent = icon.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
            {
                if (parent is Button found) { button = found; break; }
            }
            if (button is null) continue;
            ToolTip.SetTip(button, compactActive
                ? "剩余 " + FocusTimerMath.FormatRemaining(remaining)
                : "设置专注倒计时");
        }
    }

    private bool IsInsideDateLabel(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, _dateLabel)) return true;
        return false;
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
        // The drawer replaces the main view instead of floating over it, and fades
        // in over 180ms like the Windows panel.
        _expandedView.IsVisible = false;
        _backlogHost.IsVisible = true;
        _backlogTab?.SetDrawerOpen(true);
        TodoAnim.Fade(_backlogHost, 0, 1, 180);
    }

    private void CloseBacklog()
    {
        if (!_backlogHost.IsVisible) return;
        _multiSelect = false;
        _selectedBacklogIds.Clear();
        _backlogTab?.SetDrawerOpen(false);
        if (_expanded) _expandedView.IsVisible = true;
        // 150ms fade out, then hide, matching the Windows panel.
        TodoAnim.Fade(_backlogHost, _backlogHost.Opacity, 0, 150, () =>
        {
            if (!_backlogHost.IsVisible) return;
            _backlogHost.IsVisible = false;
        });
    }

    private void RenderBacklog()
    {
        _backlogList.Children.Clear();
        _backlogFooter.Children.Clear();
        _backlogTitle.Text = "堆积事项  " + _data.BacklogItems.Count;

        if (_data.BacklogItems.Count == 0)
        {
            var empty = TodoTheme.Label("把暂时不处理的事项放在这里", 10.5, TodoTheme.SecondaryText);
            empty.Margin = new Thickness(4, 18, 0, 0);
            _backlogList.Children.Add(empty);
        }
        else
        {
            foreach (var item in _data.BacklogItems.ToArray()) _backlogList.Children.Add(BuildBacklogRow(item));
        }

        BuildBacklogFooter();
    }

    /// <summary>One backlog row: a 49 tall card with a 23px tick, text, date and move button.</summary>
    private Control BuildBacklogRow(DailyTodoItem item)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions = new ColumnDefinitions("31,*,52,34");

        var check = new Button
        {
            Width = 23,
            Height = 23,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            FontSize = 10,
            FontFamily = TodoTheme.UiFont,
            Foreground = TodoTheme.PrimaryText,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = false
        };
        TodoTheme.ApplyPressFeedback(check);
        if (_multiSelect)
        {
            var selected = _selectedBacklogIds.Contains(item.Id!);
            check.Content = selected ? "✓" : string.Empty;
            check.Background = selected ? TodoTheme.DialRed : Brushes.Transparent;
            check.BorderBrush = selected ? TodoTheme.DialRed : new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
            check.CornerRadius = new CornerRadius(6);
            check.Click += (_, _) =>
            {
                if (!_selectedBacklogIds.Remove(item.Id!)) _selectedBacklogIds.Add(item.Id!);
                RenderBacklog();
            };
        }
        else
        {
            check.Content = item.Completed ? "✓" : string.Empty;
            check.Background = item.Completed ? new SolidColorBrush(Color.FromArgb(95, 255, 255, 255)) : Brushes.Transparent;
            check.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 235, 238, 244));
            check.CornerRadius = new CornerRadius(12);
            check.Click += (_, _) =>
            {
                TodoLogic.ToggleBacklogCompleted(_data, item, _clock.Today);
                if (_revealedSubItemInputOwnerId == item.Id) _revealedSubItemInputOwnerId = null;
                SaveNow();
                RenderAll();
            };
        }
        Grid.SetColumn(check, 0);
        row.Children.Add(check);

        var text = new TextBlock
        {
            Text = item.Text ?? string.Empty,
            FontFamily = TodoTheme.UiFont,
            FontSize = 10.5,
            FontWeight = item.Completed ? FontWeight.Normal : FontWeight.SemiBold,
            Foreground = item.Completed ? TodoTheme.SecondaryText : TodoTheme.PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (item.Completed) text.TextDecorations = TextDecorations.Strikethrough;
        // Windows shows the sub item progress on a backlog row that has children.
        if (item.SubItems.Count > 0)
            ToolTip.SetTip(text, "子事项 " + item.SubItems.Count(subItem => subItem.Completed)
                                 + "/" + item.SubItems.Count);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var date = TodoTheme.Label(FormatSourceDate(item.BacklogSourceDate), 8.8, TodoTheme.SecondaryText);
        date.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(date, 2);
        row.Children.Add(date);

        var move = MoveToTodayButton(28);
        move.Click += (_, _) =>
        {
            TodoLogic.MoveBacklogToToday(_data, [item], _clock.Today);
            SaveNow();
            RenderAll();
        };
        Grid.SetColumn(move, 3);
        row.Children.Add(move);

        return new Border
        {
            Height = 49,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 6, 4),
            Child = row
        };
    }

    /// <summary>Windows InvertedStackActionButton: white, 27 tall, with the stack icon.</summary>
    private Button MoveToTodayButton(double width)
    {
        var icon = new StackedItemsIcon { Width = 17, Height = 17, Inverted = true };
        var button = TodoTheme.IconButton(icon, width);
        button.Height = 27;
        button.Background = Brushes.White;
        button.BorderBrush = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(7);
        ToolTip.SetTip(button, "移入今日");
        button.PointerEntered += (_, _) =>
        {
            button.Background = new SolidColorBrush(Color.FromRgb(224, 227, 232));
            button.BorderBrush = Brushes.White;
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.White;
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));
        };
        return button;
    }

    private void BuildBacklogFooter()
    {
        if (!_multiSelect)
        {
            var multiple = TodoTheme.TextButton("多选", 10, 54);
            multiple.HorizontalAlignment = HorizontalAlignment.Left;
            multiple.IsEnabled = _data.BacklogItems.Count > 0;
            multiple.Opacity = multiple.IsEnabled ? 1 : 0.4;
            multiple.Click += (_, _) => SetMultiSelect(true);
            Grid.SetColumn(multiple, 0);
            _backlogFooter.Children.Add(multiple);

            if (_undoEntries.Count > 0)
            {
                var undo = TodoTheme.TextButton($"已删除 {_undoEntries.Count} 项 · 撤销", 10, 112);
                undo.Click += (_, _) =>
                {
                    _undoTimer.Stop();
                    TodoLogic.UndoBacklogDelete(_data, _undoEntries);
                    _undoEntries.Clear();
                    SaveNow();
                    RenderAll();
                };
                Grid.SetColumn(undo, 2);
                _backlogFooter.Children.Add(undo);
            }
            return;
        }

        var cancel = TodoTheme.TextButton("退出多选", 10, 66);
        cancel.HorizontalAlignment = HorizontalAlignment.Left;
        cancel.Click += (_, _) => SetMultiSelect(false);
        Grid.SetColumn(cancel, 0);
        _backlogFooter.Children.Add(cancel);

        var count = TodoTheme.Label("已选 " + _selectedBacklogIds.Count, 9.5, TodoTheme.SecondaryText);
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(count, 1);
        _backlogFooter.Children.Add(count);

        var selected = _data.BacklogItems.Where(item => _selectedBacklogIds.Contains(item.Id!)).ToList();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };

        var trashIcon = new TrashCanIcon { Width = 19, Height = 19 };
        var delete = TodoTheme.IconButton(trashIcon, 31);
        delete.Height = 27;
        delete.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        delete.BorderBrush = new SolidColorBrush(Color.FromArgb(62, 255, 255, 255));
        delete.BorderThickness = new Thickness(1);
        // Windows ActionButton: hover fills the destructive button red.
        delete.PointerEntered += (_, _) =>
        {
            delete.Background = TodoTheme.DialRed;
            delete.BorderBrush = Brushes.White;
            trashIcon.Stroke = Brushes.White;
        };
        delete.PointerExited += (_, _) =>
        {
            delete.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            delete.BorderBrush = new SolidColorBrush(Color.FromArgb(62, 255, 255, 255));
            trashIcon.Stroke = TodoTheme.DialRed;
        };
        delete.IsEnabled = selected.Count > 0;
        delete.Opacity = delete.IsEnabled ? 1 : 0.4;
        delete.Click += (_, _) =>
        {
            var items = _data.BacklogItems.Where(value => _selectedBacklogIds.Contains(value.Id!)).ToList();
            SetMultiSelect(false);
            DeleteBacklogItems(items);
        };
        actions.Children.Add(delete);

        var move = MoveToTodayButton(31);
        move.Margin = new Thickness(7, 0, 0, 0);
        move.IsEnabled = selected.Count > 0;
        move.Opacity = move.IsEnabled ? 1 : 0.4;
        move.Click += (_, _) =>
        {
            var items = _data.BacklogItems.Where(value => _selectedBacklogIds.Contains(value.Id!)).ToList();
            SetMultiSelect(false);
            TodoLogic.MoveBacklogToToday(_data, items, _clock.Today);
            SaveNow();
            RenderAll();
        };
        actions.Children.Add(move);

        Grid.SetColumn(actions, 2);
        _backlogFooter.Children.Add(actions);
    }

    private void SetMultiSelect(bool enabled)
    {
        _multiSelect = enabled;
        _selectedBacklogIds.Clear();
        RenderBacklog();
    }

    private void DeleteBacklogItems(List<DailyTodoItem> items)
    {
        if (items.Count == 0) return;
        _undoTimer.Stop();
        _undoEntries.Clear();
        _undoEntries.AddRange(TodoLogic.DeleteBacklogItems(_data, items));
        if (_undoEntries.Count == 0) return;
        _selectedBacklogIds.Clear();
        SaveNow();
        RenderBacklog();
        _undoTimer.Start();
    }

    private static string FormatSourceDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date)
            ? date.ToString("M月d日", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"))
            : string.Empty;

    // ---------------------------------------------------------------- dialogs

    private void OpenDateChooser()
    {
        _dialogOpen = true;
        var chooser = new DateChooserWindow(_viewedDate);
        // Windows ChooseDate L861: an owned (not modal) window, so a click on the
        // window itself deactivates the picker and closes it. A modal dialog would
        // disable this window and swallow that click.
        chooser.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _dialogOpen = false;
            if (chooser.SelectedDate is { } selected) ShowDate(selected);
            Activate();
        });
        chooser.Show(this);
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
        var currentDate = _clock.Today;
        var dayChanged = currentDate != _observedToday;
        var recurringChanged = EnsureRecurring(currentDate);

        if (dayChanged)
        {
            var previousToday = _observedToday;
            _observedToday = currentDate;
            // Only a view that was showing the old today follows the rollover.
            if (_viewedDate == previousToday) _viewedDate = currentDate;
            NormalizeFocus();
            RenderAll();
            if (_viewedDate == currentDate) Dispatcher.UIThread.Post(MaybePromptImport, DispatcherPriority.Background);
        }
        else if (recurringChanged)
        {
            RenderAll();
        }
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
        // Windows collapses back to the capsule height its current item needs.
        ResizeAnchored(TodoTheme.CompactWidth,
            TodoLogic.CompactHeightFor(TodoLogic.CurrentTodayItem(_data, _clock.Today), _data.ShowCompactSubItems));
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

    /// <summary>
    /// Keeps the window on the work area. A window that is currently tucked into
    /// an edge is revealed instead of clamped, like the Windows KeepOnScreen.
    /// </summary>
    private void KeepOnScreen()
    {
        var work = WorkingArea();
        var y = Math.Max(work.Top, Math.Min(_logicalPosition.Y, work.Bottom - Height));
        if (_hiddenEdge < 0)
        {
            _revealedLeft = work.Left;
            ApplyPosition(_revealedLeft, y);
        }
        else if (_hiddenEdge > 0)
        {
            _revealedLeft = work.Right - Width;
            ApplyPosition(_revealedLeft, y);
        }
        else
        {
            ApplyPosition(Math.Max(work.Left, Math.Min(_logicalPosition.X, work.Right - Width)), y);
        }
    }

    private void SnapOrHideAtEdge()
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
            if (_expanded) ApplyPosition(_revealedLeft, y);
            else HideToEdge();
        }
        else if (x + Width >= work.Right - snapDistance)
        {
            _hiddenEdge = 1;
            _revealedLeft = work.Right - Width;
            if (_expanded) ApplyPosition(_revealedLeft, y);
            else HideToEdge();
        }
        else
        {
            _hiddenEdge = 0;
            ApplyPosition(Math.Max(work.Left, Math.Min(x, work.Right - Width)), y);
        }
    }

    private void RevealFromEdge()
    {
        if (_hiddenEdge != 0) ApplyPosition(_revealedLeft, _logicalPosition.Y);
    }

    private void HideToEdge()
    {
        if (!_edgeHideEnabled || _expanded) return;
        const double visibleStrip = 10;
        var work = WorkingArea();
        if (_hiddenEdge < 0) ApplyPosition(work.Left - Width + visibleStrip, _logicalPosition.Y);
        else if (_hiddenEdge > 0) ApplyPosition(work.Right - visibleStrip, _logicalPosition.Y);
    }

    private void SetEdgeHideEnabledInternal(bool enabled)
    {
        _edgeHideEnabled = enabled;
        _edgeHideTimer.Stop();
        if (enabled) return;
        RevealFromEdge();
        _hiddenEdge = 0;
    }

    private void ShowBacklogTab()
    {
        // The Windows module always shows the tab while the deck is expanded, even
        // with an empty backlog, so it is a stable drop target.
        _backlogTab ??= new BacklogTabWindow(this);
        if (!_backlogTab.IsVisible) _backlogTab.Show(this);
        _backlogTab.PositionBesideOwner();
    }

    private void HideBacklogTab() => _backlogTab?.Hide();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!_positioned) PositionDefault();
        RenderAll();
    }

}
