using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using LittleTools.Common;

namespace LittleTools.DailyTodo
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "LittleTools.DailyTodo.SingleInstance", out created))
            {
                if (!created) return;
                bool managed = args != null && Array.IndexOf(args, "--managed") >= 0;
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var window = new DailyTodoWindow();
                app.MainWindow = window;
                using (var tray = managed ? null : new TodoTray(window, app)) app.Run(window);
            }
        }
    }

    internal sealed class DailyTodoItem
    {
        public string Id;
        public string Text;
        public bool Completed;
        public DateTime CreatedAt;
        public string SourceId;
        public string RecurringRuleId;
        public string BacklogSourceDate;
        public int PreviousOpenIndex = -1;
        public List<DailyTodoSubItem> SubItems = new List<DailyTodoSubItem>();
    }

    internal sealed class DailyTodoSubItem
    {
        public string Id;
        public string Text;
        public bool Completed;
        public DateTime CreatedAt;
    }

    internal sealed class TodoDay
    {
        public string Date;
        public List<DailyTodoItem> Items = new List<DailyTodoItem>();
        public List<string> ImportedSourceIds = new List<string>();
        public List<string> SuppressedRuleIds = new List<string>();
    }

    internal sealed class RecurringTodoRule
    {
        public string Id;
        public string Text;
        public string Frequency;
        public int ScheduleValue;
        public bool Enabled = true;
        public string CreatedDate;
    }

    internal sealed class DailyTodoData
    {
        public List<TodoDay> Days = new List<TodoDay>();
        public List<DailyTodoItem> BacklogItems = new List<DailyTodoItem>();
        public string LastImportPromptDate;
        public List<RecurringTodoRule> RecurringRules = new List<RecurringTodoRule>();
        public FocusTimerData FocusTimer;
        public bool ShowCompactSubItems = true;
    }

    internal sealed class FocusTimerData
    {
        public string ItemId;
        public string ItemText;
        public int DurationMinutes;
        public string EndsAtUtc;
    }

    internal sealed class FocusIconBinding
    {
        public string ItemId;
        public FocusRingIcon Icon;
        public Button Button;
    }

    internal sealed class BacklogUndoEntry
    {
        public DailyTodoItem Item;
        public int Index;
    }

    internal static class RecurringTodoEngine
    {
        public static bool Ensure(DailyTodoData data, DateTime date)
        {
            bool changed = false;
            TodoDay day = null;
            foreach (RecurringTodoRule rule in data.RecurringRules)
            {
                if (!Matches(rule, date)) continue;
                if (day == null) day = FindDay(data, date, true);
                if (day.SuppressedRuleIds.Contains(rule.Id)) continue;
                bool exists = false;
                foreach (DailyTodoItem item in day.Items)
                {
                    if (item.RecurringRuleId == rule.Id) { exists = true; break; }
                }
                if (exists) continue;
                day.Items.Insert(FirstCompletedIndex(day.Items), new DailyTodoItem
                {
                    Id = Guid.NewGuid().ToString("N"), Text = rule.Text, CreatedAt = DateTime.Now,
                    RecurringRuleId = rule.Id
                });
                changed = true;
            }
            return changed;
        }

        public static bool Matches(RecurringTodoRule rule, DateTime date)
        {
            if (rule == null || !rule.Enabled || string.IsNullOrWhiteSpace(rule.Text)) return false;
            DateTime created;
            if (DateTime.TryParseExact(rule.CreatedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out created) && date.Date < created.Date) return false;
            if (rule.Frequency == "Daily") return true;
            if (rule.Frequency == "Weekly") return (int)date.DayOfWeek == rule.ScheduleValue;
            if (rule.Frequency == "Monthly") return date.Day == rule.ScheduleValue;
            return false;
        }

        private static TodoDay FindDay(DailyTodoData data, DateTime date, bool create)
        {
            string key = date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (TodoDay day in data.Days) if (day.Date == key) return day;
            if (!create) return null;
            var created = new TodoDay { Date = key };
            data.Days.Add(created);
            return created;
        }

        private static int FirstCompletedIndex(List<DailyTodoItem> items)
        {
            for (int index = 0; index < items.Count; index++) if (items[index].Completed) return index;
            return items.Count;
        }

    }

    internal sealed class TodoStore
    {
        private readonly string dataPath;
        private readonly string backupPath;

        public TodoStore()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LittleTools", "DailyTodo");
            dataPath = Path.Combine(folder, "data.json");
            backupPath = Path.Combine(folder, "data.backup.json");
        }

        public DailyTodoData Load()
        {
            DailyTodoData loaded = TryLoad(dataPath) ?? TryLoad(backupPath) ?? new DailyTodoData();
            if (loaded.Days == null) loaded.Days = new List<TodoDay>();
            if (loaded.BacklogItems == null) loaded.BacklogItems = new List<DailyTodoItem>();
            if (loaded.RecurringRules == null) loaded.RecurringRules = new List<RecurringTodoRule>();
            loaded.Days.RemoveAll(delegate(TodoDay day) { return day == null; });
            loaded.RecurringRules.RemoveAll(delegate(RecurringTodoRule rule) { return rule == null; });
            foreach (TodoDay day in loaded.Days)
            {
                if (day.Items == null) day.Items = new List<DailyTodoItem>();
                if (day.ImportedSourceIds == null) day.ImportedSourceIds = new List<string>();
                if (day.SuppressedRuleIds == null) day.SuppressedRuleIds = new List<string>();
                day.Items.RemoveAll(delegate(DailyTodoItem item) { return item == null; });
                foreach (DailyTodoItem item in day.Items)
                {
                    NormalizeItem(item);
                }
            }
            foreach (RecurringTodoRule rule in loaded.RecurringRules)
            {
                if (string.IsNullOrEmpty(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");
                if (rule.Text == null) rule.Text = "";
                if (string.IsNullOrEmpty(rule.CreatedDate))
                    rule.CreatedDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            loaded.BacklogItems.RemoveAll(delegate(DailyTodoItem item) { return item == null; });
            foreach (DailyTodoItem item in loaded.BacklogItems)
            {
                NormalizeItem(item);
                if (string.IsNullOrEmpty(item.BacklogSourceDate))
                    item.BacklogSourceDate = item.CreatedAt == default(DateTime)
                        ? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : item.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return loaded;
        }

        private static void NormalizeItem(DailyTodoItem item)
        {
            if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
            if (item.Text == null) item.Text = "";
            if (item.SubItems == null) item.SubItems = new List<DailyTodoSubItem>();
            item.SubItems.RemoveAll(delegate(DailyTodoSubItem subItem) { return subItem == null; });
            foreach (DailyTodoSubItem subItem in item.SubItems)
            {
                if (string.IsNullOrEmpty(subItem.Id)) subItem.Id = Guid.NewGuid().ToString("N");
                if (subItem.Text == null) subItem.Text = "";
            }
        }

        private static DailyTodoData TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return new JavaScriptSerializer().Deserialize<DailyTodoData>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { return null; }
        }

        public void Save(DailyTodoData data)
        {
            try
            {
                AtomicFile.WriteUtf8(dataPath, new JavaScriptSerializer().Serialize(data), backupPath);
            }
            catch { }
        }
    }

    internal sealed class TodoTray : IDisposable
    {
        private readonly Forms.NotifyIcon tray;

        public TodoTray(DailyTodoWindow window, Application app)
        {
            var menu = new Forms.ContextMenuStrip();
            var show = new Forms.ToolStripMenuItem("显示今日待办");
            show.Click += delegate { app.Dispatcher.BeginInvoke(new Action(window.ShowToday)); };
            menu.Items.Add(show);
            var exit = new Forms.ToolStripMenuItem("退出");
            exit.Click += delegate { app.Dispatcher.BeginInvoke(new Action(app.Shutdown)); };
            menu.Items.Add(exit);
            tray = new Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "Little Tools · 每日待办",
                Visible = true,
                ContextMenuStrip = menu
            };
            tray.DoubleClick += delegate { app.Dispatcher.BeginInvoke(new Action(window.ShowToday)); };
            window.FocusFinished += delegate(string itemText)
            {
                tray.BalloonTipTitle = "专注结束";
                tray.BalloonTipText = string.IsNullOrWhiteSpace(itemText) ? "本轮专注已完成。" : "“" + itemText + "”专注时间结束。";
                tray.BalloonTipIcon = Forms.ToolTipIcon.Info;
                tray.ShowBalloonTip(4500);
            };
        }

        public void Dispose()
        {
            tray.Visible = false;
            tray.Dispose();
        }
    }

    internal sealed class DailyTodoWindow : Window
    {
        private const double CardStep = 48;
        private const double CardHeight = 72;
        private const double CompactBaseHeight = 92;
        private const double CompactSubItemRowHeight = 24;
        private const int CompactSubItemRowsVisible = 4;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x80;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        private readonly TodoStore store = new TodoStore();
        private readonly DailyTodoData data;
        private readonly Border shell;
        private readonly Grid compactView;
        private readonly Grid expandedView;
        private readonly Button compactCheck;
        private readonly Button compactFocusButton;
        private readonly FocusRingIcon compactFocusIcon;
        private readonly TextBlock compactPriority;
        private readonly TextBlock compactProgress;
        private readonly RowDefinition compactSubItemsRow;
        private readonly StackPanel compactSubItemsPanel;
        private readonly TextBlock dateLabel;
        private readonly TextBox addInput;
        private readonly Canvas cardCanvas;
        private readonly TextBlock progressText;
        private readonly Button importButton;
        private readonly DispatcherTimer saveTimer;
        private readonly DispatcherTimer edgeHideTimer;
        private readonly DispatcherTimer recurringTimer;
        private readonly DispatcherTimer compactMessageTimer;
        private readonly DispatcherTimer focusTimer;
        private readonly DispatcherTimer backlogUndoTimer;
        private DispatcherTimer relocationTimer;
        private DateTime viewedDate = DateTime.Today;
        private DateTime observedToday = DateTime.Today;
        private bool expanded;
        private bool dialogOpen;
        private bool positioned;
        private readonly Dictionary<string, Border> renderedCards = new Dictionary<string, Border>();
        private DailyTodoItem dragItem;
        private TodoDay dragDay;
        private Border dragCard;
        private UIElement dragCapture;
        private double dragGrabOffset;
        private bool compactCompleting;
        private bool cardCompletionAnimating;
        private bool movingWindow;
        private bool edgeHideEnabled = true;
        private int hiddenEdge;
        private double revealedLeft;
        private readonly List<FocusIconBinding> focusIconBindings = new List<FocusIconBinding>();
        private readonly List<BacklogUndoEntry> backlogUndoEntries = new List<BacklogUndoEntry>();
        private readonly BacklogSidePanel backlogPanel;
        private string expandedSubItemOwnerId;
        private string revealedSubItemInputOwnerId;
        private TextBox activeSubItemInput;
        private readonly Random compactMessageRandom = new Random();
        private List<string> compactMessages = new List<string>();
        private string compactMessageState;
        private int compactMessageIndex = -1;

        public event Action<string> FocusFinished;

        public DailyTodoWindow()
        {
            data = store.Load();
            EnsureRecurringItems(DateTime.Today);
            Width = 316;
            Height = CompactBaseHeight;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Title = "Little Tools · Daily Todo";
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            shell = new Border
            {
                CornerRadius = new CornerRadius(15),
                Background = NormalBackground(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 11, 14, 10),
                Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Opacity = 0.16, Color = Colors.Black }
            };
            var root = new Grid();
            root.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = MinimalScrollBarStyle();
            shell.Child = root;
            Content = shell;

            compactView = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            compactView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            compactView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(29) });
            compactSubItemsRow = new RowDefinition { Height = new GridLength(0) };
            compactView.RowDefinitions.Add(compactSubItemsRow);
            compactView.RowDefinitions.Add(new RowDefinition());
            compactView.Children.Add(new TextBlock
            {
                Text = "当前事项", FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 10,
                Foreground = SecondaryText(), VerticalAlignment = VerticalAlignment.Top
            });
            compactPriority = new TextBlock
            {
                Text = "暂无待办", FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 14,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(33, 0, 31, 0)
            };
            compactPriority.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                DailyTodoItem current = CurrentTodayItem();
                if (current == null || current.SubItems == null || current.SubItems.Count == 0) return;
                data.ShowCompactSubItems = !data.ShowCompactSubItems;
                store.Save(data);
                RenderCompact();
                args.Handled = true;
            };
            Grid.SetRow(compactPriority, 1); compactView.Children.Add(compactPriority);
            compactCheck = SmallButton("", 25);
            compactCheck.Height = 25;
            compactCheck.HorizontalAlignment = HorizontalAlignment.Left;
            compactCheck.VerticalAlignment = VerticalAlignment.Center;
            compactCheck.Click += CompactCheckClick;
            Grid.SetRow(compactCheck, 1); compactView.Children.Add(compactCheck);
            compactFocusIcon = new FocusRingIcon { Width = 22, Height = 22 };
            compactFocusButton = FocusButton(compactFocusIcon);
            compactFocusButton.HorizontalAlignment = HorizontalAlignment.Right;
            compactFocusButton.VerticalAlignment = VerticalAlignment.Center;
            compactFocusButton.Click += delegate(object sender, RoutedEventArgs args)
            {
                DailyTodoItem item = CurrentTodayItem();
                if (item != null) OpenFocusTimer(item);
                args.Handled = true;
            };
            Grid.SetRow(compactFocusButton, 1); compactView.Children.Add(compactFocusButton);
            compactSubItemsPanel = new StackPanel();
            var compactSubItemsScroll = new ScrollViewer
            {
                Content = compactSubItemsPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(28, 2, 5, 1)
            };
            Grid.SetRow(compactSubItemsScroll, 2); compactView.Children.Add(compactSubItemsScroll);
            compactProgress = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"), FontSize = 9.5, Foreground = SecondaryText(),
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetRow(compactProgress, 3); compactView.Children.Add(compactProgress);
            compactView.MouseLeftButtonDown += CompactMouseLeftButtonDown;

            expandedView = new Grid { Visibility = Visibility.Collapsed };
            expandedView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(39) });
            expandedView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            expandedView.RowDefinitions.Add(new RowDefinition());
            expandedView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });

            var header = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Arrow };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            var previous = SmallButton("", 32);
            previous.Content = new ChevronIcon { Width = 12, Height = 18, PointsRight = false };
            previous.Click += delegate { ShowDate(viewedDate.AddDays(-1)); };
            header.Children.Add(previous);
            var today = SmallButton("今", 30); today.Click += delegate { ShowDate(DateTime.Today); };
            Grid.SetColumn(today, 1); header.Children.Add(today);
            dateLabel = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 11.5, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand, Padding = new Thickness(10, 7, 10, 7)
            };
            dateLabel.MouseLeftButtonUp += delegate { ChooseDate(); };
            Grid.SetColumn(dateLabel, 2); header.Children.Add(dateLabel);
            var next = SmallButton("", 32);
            next.Content = new ChevronIcon { Width = 12, Height = 18, PointsRight = true };
            next.Click += delegate { ShowDate(viewedDate.AddDays(1)); };
            Grid.SetColumn(next, 3); header.Children.Add(next);
            var collapse = SmallButton("—", 30); collapse.Click += delegate { Collapse(); };
            Grid.SetColumn(collapse, 4); header.Children.Add(collapse);
            header.MouseLeftButtonDown += HeaderMouseLeftButtonDown;
            expandedView.Children.Add(header);

            var addRow = new Grid { Margin = new Thickness(0, 4, 0, 7) };
            addRow.ColumnDefinitions.Add(new ColumnDefinition());
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            addInput = InputBox("添加待办事项…");
            addInput.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter) { AddTodo(); args.Handled = true; }
            };
            addRow.Children.Add(addInput);
            var add = SmallButton("＋", 36); add.Margin = new Thickness(6, 0, 0, 0); add.Click += delegate { AddTodo(); };
            Grid.SetColumn(add, 1); addRow.Children.Add(add);
            Grid.SetRow(addRow, 1); expandedView.Children.Add(addRow);

            cardCanvas = new Canvas { ClipToBounds = false };
            var cardScroll = new ScrollViewer
            {
                Content = cardCanvas, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 2, 0, 3)
            };
            Grid.SetRow(cardScroll, 2); expandedView.Children.Add(cardScroll);

            var footer = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            progressText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"), FontSize = 9.5, Foreground = SecondaryText(),
                VerticalAlignment = VerticalAlignment.Center
            };
            footer.Children.Add(progressText);
            importButton = SmallButton("处理昨日事项", 88);
            importButton.Click += delegate { OpenImportDialog(); };
            Grid.SetColumn(importButton, 1); footer.Children.Add(importButton);
            var recurring = SmallButton("周期性任务", 88);
            recurring.Margin = new Thickness(7, 0, 0, 0);
            recurring.Click += delegate { OpenRecurringRules(); };
            Grid.SetColumn(recurring, 2); footer.Children.Add(recurring);
            Grid.SetRow(footer, 3); expandedView.Children.Add(footer);

            root.Children.Add(compactView);
            root.Children.Add(expandedView);

            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            saveTimer.Tick += delegate { saveTimer.Stop(); SaveNow(); };
            edgeHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            edgeHideTimer.Tick += delegate
            {
                edgeHideTimer.Stop();
                if (edgeHideEnabled && !expanded && hiddenEdge != 0 && !IsMouseOver) HideToEdge();
            };
            recurringTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            recurringTimer.Tick += delegate
            {
                DateTime currentDate = DateTime.Today;
                bool dayChanged = currentDate != observedToday;
                bool recurringChanged = EnsureRecurringItems(currentDate);
                if (dayChanged)
                {
                    DateTime previousToday = observedToday;
                    observedToday = currentDate;
                    if (viewedDate == previousToday) viewedDate = currentDate;
                    RenderAll();
                    if (viewedDate == currentDate)
                        Dispatcher.BeginInvoke(new Action(MaybePromptImport), DispatcherPriority.ApplicationIdle);
                }
                else if (recurringChanged) RenderAll();
            };
            recurringTimer.Start();
            compactMessageTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            compactMessageTimer.Tick += delegate { RotateCompactMessage(); };
            focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            focusTimer.Tick += delegate { UpdateFocusTimer(); };
            NormalizeFocusTimer();
            if (data.FocusTimer != null) focusTimer.Start();
            backlogUndoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            backlogUndoTimer.Tick += delegate
            {
                backlogUndoTimer.Stop();
                backlogUndoEntries.Clear();
                backlogPanel.ClearUndoNotice();
            };
            backlogPanel = new BacklogSidePanel(this, root, expandedView, data.BacklogItems,
                ToggleBacklogCompleted, MoveBacklogToToday, DeleteBacklogItems, UndoBacklogDelete);

            SourceInitialized += delegate { ApplyToolWindowStyle(); };
            Loaded += delegate
            {
                if (!positioned) PositionDefault();
                RenderAll();
                Dispatcher.BeginInvoke(new Action(MaybePromptImport), DispatcherPriority.ApplicationIdle);
            };
            LocationChanged += delegate { backlogPanel.PositionBesideOwner(); };
            Deactivated += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (expanded && !dialogOpen && !movingWindow && !IsActive && !backlogPanel.IsInteractionActive)
                        Collapse();
                }), DispatcherPriority.Input);
            };
            Closed += delegate
            {
                saveTimer.Stop();
                edgeHideTimer.Stop();
                recurringTimer.Stop();
                compactMessageTimer.Stop();
                focusTimer.Stop();
                backlogUndoTimer.Stop();
                if (relocationTimer != null) relocationTimer.Stop();
                backlogPanel.Dispose();
                store.Save(data);
            };
            shell.MouseEnter += delegate { edgeHideTimer.Stop(); shell.Background = HoverBackground(); RevealFromEdge(); };
            shell.MouseLeave += delegate
            {
                shell.Background = NormalBackground();
                if (edgeHideEnabled && !expanded && hiddenEdge != 0) edgeHideTimer.Start();
            };
        }

        private static string DateKey(DateTime date) { return date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        private TodoDay FindDay(DateTime date, bool create)
        {
            string key = DateKey(date);
            foreach (TodoDay day in data.Days) if (day.Date == key) return day;
            if (!create) return null;
            var created = new TodoDay { Date = key };
            data.Days.Add(created);
            return created;
        }

        private bool EnsureRecurringItems(DateTime date)
        {
            bool changed = RecurringTodoEngine.Ensure(data, date);
            if (changed) store.Save(data);
            return changed;
        }

        private void PositionDefault()
        {
            Rect work = SystemParameters.WorkArea;
            Left = work.Right - Width - 18;
            Top = work.Bottom - Height - 18;
            positioned = true;
        }

        private void CompactMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
        {
            if (args.LeftButton != MouseButtonState.Pressed) return;
            RevealFromEdge();
            double originalLeft = Left;
            double originalTop = Top;
            movingWindow = true;
            try { DragMove(); } catch { }
            finally { movingWindow = false; }
            bool moved = Math.Abs(Left - originalLeft) > 1 || Math.Abs(Top - originalTop) > 1;
            SnapOrHideAtEdge();
            if (!moved) Expand();
            args.Handled = true;
        }

        private DailyTodoItem CurrentTodayItem()
        {
            TodoDay today = FindDay(DateTime.Today, false);
            if (today == null) return null;
            if (data.FocusTimer != null)
            {
                foreach (DailyTodoItem item in today.Items)
                    if (!item.Completed && item.Id == data.FocusTimer.ItemId) return item;
            }
            foreach (DailyTodoItem item in today.Items) if (!item.Completed) return item;
            return null;
        }

        private void CompactCheckClick(object sender, RoutedEventArgs args)
        {
            if (compactCompleting) return;
            DailyTodoItem item = CurrentTodayItem();
            if (item == null) return;
            TodoDay today = FindDay(DateTime.Today, false);
            compactCompleting = true;
            args.Handled = true;

            compactCheck.Content = "✓";
            compactCheck.IsEnabled = false;
            compactPriority.TextDecorations = TextDecorations.Strikethrough;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(140),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            fadeOut.Completed += delegate
            {
                item.PreviousOpenIndex = Math.Max(0, today.Items.IndexOf(item));
                today.Items.Remove(item);
                SetSubItemsCompleted(item, true);
                item.Completed = true;
                if (revealedSubItemInputOwnerId == item.Id) revealedSubItemInputOwnerId = null;
                today.Items.Add(item);
                if (data.FocusTimer != null && data.FocusTimer.ItemId == item.Id)
                {
                    data.FocusTimer = null;
                    focusTimer.Stop();
                }
                store.Save(data);

                compactPriority.BeginAnimation(OpacityProperty, null);
                compactPriority.Opacity = 1;
                compactPriority.TextDecorations = null;
                compactCheck.Content = "";
                compactCheck.IsEnabled = true;
                compactCompleting = false;
                RenderCompact();
                RenderExpanded();
                compactPriority.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            };
            compactPriority.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void HeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
        {
            if (args.LeftButton != MouseButtonState.Pressed) return;
            if (IsInsideButton(args.OriginalSource as DependencyObject) || IsInsideDateLabel(args.OriginalSource as DependencyObject)) return;
            RevealFromEdge();
            movingWindow = true;
            try { DragMove(); } catch { }
            finally { movingWindow = false; }
            SnapOrHideAtEdge();
            args.Handled = true;
        }

        private static bool IsInsideButton(DependencyObject source)
        {
            DependencyObject current = source;
            while (current != null)
            {
                if (current is Button) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private bool IsInsideDateLabel(DependencyObject source)
        {
            DependencyObject current = source;
            while (current != null)
            {
                if (ReferenceEquals(current, dateLabel)) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void KeepOnScreen()
        {
            Rect work = SystemParameters.WorkArea;
            if (hiddenEdge < 0)
            {
                revealedLeft = work.Left;
                Left = revealedLeft;
            }
            else if (hiddenEdge > 0)
            {
                revealedLeft = work.Right - Width;
                Left = revealedLeft;
            }
            else Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
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
                hiddenEdge = -1;
                revealedLeft = work.Left;
                if (expanded) Left = revealedLeft; else HideToEdge();
            }
            else if (Left + Width >= work.Right - snapDistance)
            {
                hiddenEdge = 1;
                revealedLeft = work.Right - Width;
                if (expanded) Left = revealedLeft; else HideToEdge();
            }
            else
            {
                hiddenEdge = 0;
                Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
            }
        }

        private void RevealFromEdge()
        {
            if (hiddenEdge != 0) Left = revealedLeft;
        }

        private void HideToEdge()
        {
            if (!edgeHideEnabled || expanded) return;
            const double visibleStrip = 10;
            Rect work = SystemParameters.WorkArea;
            if (hiddenEdge < 0) Left = work.Left - Width + visibleStrip;
            else if (hiddenEdge > 0) Left = work.Right - visibleStrip;
        }

        public void SetEdgeHideEnabled(bool enabled)
        {
            edgeHideEnabled = enabled;
            edgeHideTimer.Stop();
            if (!enabled)
            {
                RevealFromEdge();
                hiddenEdge = 0;
            }
        }

        private void Expand()
        {
            if (expanded) return;
            double right = Left + Width;
            double bottom = Top + Height;
            expanded = true;
            compactView.Visibility = Visibility.Collapsed;
            expandedView.Visibility = Visibility.Visible;
            Width = 430;
            Height = 530;
            Left = right - Width;
            Top = bottom - Height;
            KeepOnScreen();
            RenderAll();
            backlogPanel.ShowTab();
            Activate();
            addInput.Focus();
        }

        private void Collapse()
        {
            if (!expanded) return;
            SaveNow();
            double right = Left + Width;
            double bottom = Top + Height;
            expanded = false;
            backlogPanel.HideAll();
            expandedView.Visibility = Visibility.Collapsed;
            compactView.Visibility = Visibility.Visible;
            Width = 316;
            Height = CompactHeightFor(CurrentTodayItem());
            Left = right - Width;
            Top = bottom - Height;
            KeepOnScreen();
            RenderCompact();
            if (edgeHideEnabled && hiddenEdge != 0 && !IsMouseOver) edgeHideTimer.Start();
        }

        private void ShowDate(DateTime date)
        {
            viewedDate = date.Date;
            EnsureRecurringItems(viewedDate);
            RenderAll();
            if (viewedDate == DateTime.Today)
                Dispatcher.BeginInvoke(new Action(MaybePromptImport), DispatcherPriority.ApplicationIdle);
        }

        private void ChooseDate()
        {
            if (dialogOpen) return;
            dialogOpen = true;
            var chooser = new DateChooserWindow(viewedDate) { Owner = this };
            chooser.Closed += delegate
            {
                dialogOpen = false;
                if (chooser.SelectedDate.HasValue)
                {
                    ShowDate(chooser.SelectedDate.Value);
                    Activate();
                    return;
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (expanded && !IsActive && !backlogPanel.IsInteractionActive) Collapse();
                }), DispatcherPriority.ApplicationIdle);
            };
            chooser.Show();
        }

        private void AddTodo()
        {
            string text = (addInput.Text ?? "").Trim();
            if (text.Length == 0) return;
            TodoDay day = FindDay(viewedDate, true);
            int insert = FirstCompletedIndex(day.Items);
            day.Items.Insert(insert, new DailyTodoItem
            {
                Id = Guid.NewGuid().ToString("N"), Text = text, CreatedAt = DateTime.Now
            });
            addInput.Clear();
            SaveNow();
            RenderAll();
        }

        private static int FirstCompletedIndex(List<DailyTodoItem> items)
        {
            for (int index = 0; index < items.Count; index++) if (items[index].Completed) return index;
            return items.Count;
        }

        private static List<DailyTodoSubItem> CloneSubItems(DailyTodoItem source)
        {
            var copies = new List<DailyTodoSubItem>();
            if (source == null || source.SubItems == null) return copies;
            foreach (DailyTodoSubItem subItem in source.SubItems)
            {
                copies.Add(new DailyTodoSubItem
                {
                    Id = Guid.NewGuid().ToString("N"), Text = subItem.Text,
                    Completed = subItem.Completed, CreatedAt = DateTime.Now
                });
            }
            return copies;
        }

        private void ToggleCompleted(TodoDay day, DailyTodoItem item, Border card, Button check, TextBox text)
        {
            if (cardCompletionAnimating) return;
            cardCompletionAnimating = true;
            bool completing = !item.Completed;
            check.Content = completing ? "✓" : "";
            check.IsEnabled = false;
            check.Foreground = completing ? Brushes.White : SecondaryText();
            text.TextDecorations = completing ? TextDecorations.Strikethrough : null;
            text.Foreground = completing ? SecondaryText() : Brushes.White;
            text.FontFamily = new FontFamily(completing ? "Segoe UI" : "Segoe UI Semibold");
            card.IsHitTestVisible = false;
            cardCanvas.IsHitTestVisible = false;

            double startOpacity = card.Opacity;
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = startOpacity;
            var fade = new DoubleAnimation(startOpacity, 0, TimeSpan.FromMilliseconds(235))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            fade.Completed += delegate
            {
                int oldIndex = Math.Max(0, day.Items.IndexOf(item));
                day.Items.Remove(item);
                if (completing)
                {
                    item.PreviousOpenIndex = oldIndex;
                    SetSubItemsCompleted(item, true);
                    item.Completed = true;
                    if (revealedSubItemInputOwnerId == item.Id) revealedSubItemInputOwnerId = null;
                    day.Items.Add(item);
                    if (data.FocusTimer != null && data.FocusTimer.ItemId == item.Id)
                    {
                        data.FocusTimer = null;
                        focusTimer.Stop();
                    }
                }
                else
                {
                    item.Completed = false;
                    SetSubItemsCompleted(item, false);
                    int openCount = FirstCompletedIndex(day.Items);
                    int restoreIndex = item.PreviousOpenIndex < 0 ? openCount
                        : Math.Max(0, Math.Min(item.PreviousOpenIndex, openCount));
                    item.PreviousOpenIndex = -1;
                    day.Items.Insert(restoreIndex, item);
                }
                store.Save(data);
                RenderCompact();
                UpdateExpandedProgress(day);
                card.BeginAnimation(OpacityProperty, null);
                cardCanvas.Children.Remove(card);
                renderedCards.Remove(item.Id);
                AnimateReflowThenMaterialize(day, item);
            };
            card.BeginAnimation(OpacityProperty, fade);
        }

        private static void SetSubItemsCompleted(DailyTodoItem item, bool completed)
        {
            if (item == null || item.SubItems == null) return;
            foreach (DailyTodoSubItem subItem in item.SubItems) subItem.Completed = completed;
        }

        private void UpdateExpandedProgress(TodoDay day)
        {
            int completed = 0;
            foreach (DailyTodoItem candidate in day.Items) if (candidate.Completed) completed++;
            progressText.Text = "已完成 " + completed + " / 全部 " + day.Items.Count;
        }

        private void AnimateReflowThenMaterialize(TodoDay day, DailyTodoItem movedItem)
        {
            if (!ReferenceEquals(FindDay(viewedDate, false), day))
            {
                FinishCardRelocation();
                return;
            }

            for (int index = 0; index < day.Items.Count; index++)
            {
                Border card;
                if (!renderedCards.TryGetValue(day.Items[index].Id, out card)) continue;
                Panel.SetZIndex(card, day.Items.Count - index);
                ApplyCardLayer(card, index, false);
                AnimateCardValueSmooth(card, Canvas.TopProperty, CardTopAt(index, day.Items), 335, 0, null);
                AnimateCardValueSmooth(card, Canvas.LeftProperty, CardLeftAt(index), 335, 0, null);
                AnimateCardValueSmooth(card, WidthProperty, CardWidthAt(index), 335, 0, null);
            }

            cardCanvas.Height = Math.Max(330, day.Items.Count == 0 ? 330
                : CardTopAt(day.Items.Count - 1, day.Items) + CardHeightAt(day.Items[day.Items.Count - 1]) + 8);
            relocationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(335) };
            relocationTimer.Tick += delegate
            {
                relocationTimer.Stop();
                relocationTimer = null;
                if (!ReferenceEquals(FindDay(viewedDate, false), day))
                {
                    FinishCardRelocation();
                    return;
                }
                int index = day.Items.IndexOf(movedItem);
                if (index < 0) { FinishCardRelocation(); return; }
                var newCard = CreateCard(day, movedItem, CardWidthAt(index), index, day.Items.Count);
                newCard.Opacity = 0;
                Canvas.SetLeft(newCard, CardLeftAt(index));
                Canvas.SetTop(newCard, CardTopAt(index, day.Items) + 12);
                Panel.SetZIndex(newCard, day.Items.Count - index);
                cardCanvas.Children.Add(newCard);
                renderedCards[movedItem.Id] = newCard;

                var appear = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(285))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                newCard.BeginAnimation(OpacityProperty, appear);
                AnimateCardValueSmooth(newCard, Canvas.TopProperty, CardTopAt(index, day.Items), 315, 0,
                    new Action(FinishCardRelocation));
            };
            relocationTimer.Start();
        }

        private void FinishCardRelocation()
        {
            cardCompletionAnimating = false;
            cardCanvas.IsHitTestVisible = true;
            if (relocationTimer != null) { relocationTimer.Stop(); relocationTimer = null; }
            RenderExpanded(false);
        }

        private void DeleteItem(TodoDay day, DailyTodoItem item)
        {
            day.Items.Remove(item);
            if (expandedSubItemOwnerId == item.Id) expandedSubItemOwnerId = null;
            if (revealedSubItemInputOwnerId == item.Id) revealedSubItemInputOwnerId = null;
            if (data.FocusTimer != null && data.FocusTimer.ItemId == item.Id)
            {
                data.FocusTimer = null;
                focusTimer.Stop();
            }
            if (!string.IsNullOrEmpty(item.RecurringRuleId) && !day.SuppressedRuleIds.Contains(item.RecurringRuleId))
                day.SuppressedRuleIds.Add(item.RecurringRuleId);
            SaveNow();
            RenderAll();
        }

        private void MoveToBacklog(TodoDay day, DailyTodoItem item)
        {
            if (day == null || item == null || item.Completed || !day.Items.Remove(item)) return;
            if (expandedSubItemOwnerId == item.Id) expandedSubItemOwnerId = null;
            if (revealedSubItemInputOwnerId == item.Id) revealedSubItemInputOwnerId = null;
            item.BacklogSourceDate = string.IsNullOrEmpty(day.Date) ? DateKey(viewedDate) : day.Date;
            item.Completed = false;
            item.PreviousOpenIndex = -1;
            if (!string.IsNullOrEmpty(item.RecurringRuleId) && !day.SuppressedRuleIds.Contains(item.RecurringRuleId))
                day.SuppressedRuleIds.Add(item.RecurringRuleId);
            if (data.FocusTimer != null && data.FocusTimer.ItemId == item.Id)
            {
                data.FocusTimer = null;
                focusTimer.Stop();
            }
            data.BacklogItems.Add(item);
            store.Save(data);
            RenderAll();
            backlogPanel.Refresh();
        }

        private void ToggleBacklogCompleted(DailyTodoItem item)
        {
            if (item == null || !data.BacklogItems.Remove(item)) return;
            SetSubItemsCompleted(item, true);
            item.Completed = true;
            if (revealedSubItemInputOwnerId == item.Id) revealedSubItemInputOwnerId = null;
            item.PreviousOpenIndex = -1;
            TodoDay today = FindDay(DateTime.Today, true);
            today.Items.Add(item);
            store.Save(data);
            RenderAll();
            backlogPanel.Refresh();
        }

        private void MoveBacklogToToday(List<DailyTodoItem> items)
        {
            if (items == null || items.Count == 0) return;
            TodoDay today = FindDay(DateTime.Today, true);
            int openInsert = FirstCompletedIndex(today.Items);
            foreach (DailyTodoItem item in items.ToArray())
            {
                if (!data.BacklogItems.Remove(item)) continue;
                item.PreviousOpenIndex = -1;
                if (item.Completed) today.Items.Add(item);
                else today.Items.Insert(openInsert++, item);
            }
            store.Save(data);
            RenderAll();
            backlogPanel.Refresh();
        }

        private void DeleteBacklogItems(List<DailyTodoItem> items)
        {
            if (items == null || items.Count == 0) return;
            backlogUndoTimer.Stop();
            backlogUndoEntries.Clear();
            foreach (DailyTodoItem item in items)
            {
                int index = data.BacklogItems.IndexOf(item);
                if (index < 0) continue;
                backlogUndoEntries.Add(new BacklogUndoEntry { Item = item, Index = index });
            }
            foreach (BacklogUndoEntry entry in backlogUndoEntries)
                data.BacklogItems.Remove(entry.Item);
            if (backlogUndoEntries.Count == 0) return;
            store.Save(data);
            backlogPanel.Refresh();
            backlogPanel.ShowUndoNotice(backlogUndoEntries.Count);
            backlogUndoTimer.Start();
        }

        private void UndoBacklogDelete()
        {
            backlogUndoTimer.Stop();
            foreach (BacklogUndoEntry entry in backlogUndoEntries.OrderBy(delegate(BacklogUndoEntry value) { return value.Index; }))
            {
                int index = Math.Max(0, Math.Min(entry.Index, data.BacklogItems.Count));
                data.BacklogItems.Insert(index, entry.Item);
            }
            backlogUndoEntries.Clear();
            store.Save(data);
            backlogPanel.ClearUndoNotice();
            backlogPanel.Refresh();
        }

        private void OpenRecurringRules()
        {
            dialogOpen = true;
            try
            {
                var dialog = new RecurringRulesWindow(data.RecurringRules) { Owner = this };
                dialog.ShowDialog();
                if (dialog.Changed)
                {
                    RestoreRecurringRulesForToday(dialog.ApplyTodayRuleIds);
                    EnsureRecurringItems(DateTime.Today);
                    if (viewedDate != DateTime.Today) EnsureRecurringItems(viewedDate);
                    store.Save(data);
                    RenderAll();
                }
            }
            finally { dialogOpen = false; Activate(); }
        }

        private void RestoreRecurringRulesForToday(IEnumerable<string> ruleIds)
        {
            TodoDay today = FindDay(DateTime.Today, false);
            if (today == null || ruleIds == null) return;
            foreach (string ruleId in ruleIds)
            {
                if (!string.IsNullOrEmpty(ruleId)) today.SuppressedRuleIds.Remove(ruleId);
            }
        }

        private void QueueSave() { saveTimer.Stop(); saveTimer.Start(); }

        private void SaveNow()
        {
            saveTimer.Stop();
            store.Save(data);
            RenderCompact();
        }

        private static Button FocusButton(FocusRingIcon icon)
        {
            var button = new Button
            {
                Content = icon, Width = 27, Height = 27, Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                ToolTip = "设置专注倒计时", Cursor = Cursors.Hand, Focusable = false
            };
            ApplyButtonTemplate(button, 14);
            return button;
        }

        private void NormalizeFocusTimer()
        {
            if (data.FocusTimer == null) return;
            DateTime endsAt;
            if (data.FocusTimer.DurationMinutes <= 0 || data.FocusTimer.DurationMinutes > 100 ||
                string.IsNullOrEmpty(data.FocusTimer.ItemId) ||
                !DateTime.TryParse(data.FocusTimer.EndsAtUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out endsAt) || endsAt.ToUniversalTime() <= DateTime.UtcNow)
                data.FocusTimer = null;
        }

        private double FocusRemainingSeconds()
        {
            if (data.FocusTimer == null) return 0;
            DateTime endsAt;
            if (!DateTime.TryParse(data.FocusTimer.EndsAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out endsAt)) return 0;
            return Math.Max(0, (endsAt.ToUniversalTime() - DateTime.UtcNow).TotalSeconds);
        }

        private double FocusFill()
        {
            if (data.FocusTimer == null) return 0;
            return Math.Max(0, Math.Min(1, FocusRemainingSeconds() / (100.0 * 60.0)));
        }

        private void UpdateFocusIcon(FocusRingIcon icon, string itemId)
        {
            bool active = data.FocusTimer != null && !string.IsNullOrEmpty(itemId) && data.FocusTimer.ItemId == itemId;
            icon.Active = active;
            icon.Progress = active ? FocusFill() : 0;
            icon.Urgent = active && FocusRemainingSeconds() <= 60;
            icon.InvalidateVisual();
        }

        private void UpdateFocusTooltip(Button button, string itemId)
        {
            if (button == null) return;
            ToolTip tooltip = button.ToolTip as ToolTip;
            if (tooltip == null)
            {
                tooltip = new ToolTip();
                button.ToolTip = tooltip;
            }
            bool active = data.FocusTimer != null && !string.IsNullOrEmpty(itemId) &&
                data.FocusTimer.ItemId == itemId;
            if (!active)
            {
                tooltip.Content = "设置专注倒计时";
                return;
            }
            int totalSeconds = Math.Max(0, (int)Math.Ceiling(FocusRemainingSeconds()));
            tooltip.Content = "剩余 " + (totalSeconds / 60).ToString("00", CultureInfo.InvariantCulture) +
                ":" + (totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private void UpdateFocusIcons()
        {
            DailyTodoItem compactItem = CurrentTodayItem();
            UpdateFocusIcon(compactFocusIcon, compactItem == null ? null : compactItem.Id);
            UpdateFocusTooltip(compactFocusButton, compactItem == null ? null : compactItem.Id);
            foreach (FocusIconBinding binding in focusIconBindings)
            {
                UpdateFocusIcon(binding.Icon, binding.ItemId);
                UpdateFocusTooltip(binding.Button, binding.ItemId);
                if (binding.Button != null)
                    binding.Button.Visibility = viewedDate == DateTime.Today && compactItem != null &&
                        compactItem.Id == binding.ItemId ? Visibility.Visible : Visibility.Hidden;
            }
        }

        private void UpdateFocusTimer()
        {
            if (data.FocusTimer == null) { focusTimer.Stop(); UpdateFocusIcons(); return; }
            if (FocusRemainingSeconds() > 0) { UpdateFocusIcons(); return; }
            string itemText = data.FocusTimer.ItemText;
            data.FocusTimer = null;
            focusTimer.Stop();
            store.Save(data);
            RenderCompact();
            UpdateFocusIcons();
            System.Media.SystemSounds.Exclamation.Play();
            Action<string> completed = FocusFinished;
            if (completed != null) completed(itemText);
        }

        private void OpenFocusTimer(DailyTodoItem item)
        {
            if (item == null || item.Completed) return;
            dialogOpen = true;
            try
            {
                bool active = data.FocusTimer != null && data.FocusTimer.ItemId == item.Id;
                int initialMinutes = active
                    ? Math.Max(1, (int)Math.Ceiling(FocusRemainingSeconds() / 60.0)) : 0;
                var dialog = new FocusTimerWindow(item.Text, initialMinutes, active,
                    new Func<double>(FocusRemainingSeconds), delegate(int minutes)
                {
                    StartFocusTimer(item, minutes);
                }, delegate
                {
                    StopFocusTimer();
                }) { Owner = this };
                dialog.ShowDialog();
            }
            finally { dialogOpen = false; Activate(); }
        }

        private void StartFocusTimer(DailyTodoItem item, int minutes)
        {
            minutes = Math.Max(1, Math.Min(100, minutes));
            data.FocusTimer = new FocusTimerData
            {
                ItemId = item.Id,
                ItemText = item.Text,
                DurationMinutes = minutes,
                EndsAtUtc = DateTime.UtcNow.AddMinutes(minutes).ToString("o", CultureInfo.InvariantCulture)
            };
            store.Save(data);
            focusTimer.Start();
            RenderCompact();
            UpdateFocusIcons();
        }

        private void StopFocusTimer()
        {
            data.FocusTimer = null;
            focusTimer.Stop();
            store.Save(data);
            RenderCompact();
            UpdateFocusIcons();
        }

        private void RenderAll()
        {
            RenderCompact();
            RenderExpanded();
        }

        private void RenderCompact()
        {
            if (compactCompleting) return;
            TodoDay today = FindDay(DateTime.Today, false);
            int total = today == null ? 0 : today.Items.Count;
            int completed = 0;
            DailyTodoItem priority = CurrentTodayItem();
            if (today != null)
            {
                foreach (DailyTodoItem item in today.Items)
                {
                    if (item.Completed) completed++;
                }
            }
            compactPriority.TextDecorations = null;
            UpdateCompactPriority(priority, total);
            compactCheck.Visibility = priority != null ? Visibility.Visible : Visibility.Hidden;
            compactFocusButton.Visibility = priority != null ? Visibility.Visible : Visibility.Hidden;
            UpdateFocusIcon(compactFocusIcon, priority == null ? null : priority.Id);
            UpdateFocusTooltip(compactFocusButton, priority == null ? null : priority.Id);
            string childProgress = "";
            if (priority != null && priority.SubItems != null && priority.SubItems.Count > 0)
            {
                int childCompleted = priority.SubItems.Count(delegate(DailyTodoSubItem subItem) { return subItem.Completed; });
                childProgress = " · 子事项 " + childCompleted + "/" + priority.SubItems.Count;
            }
            compactProgress.Text = "今日完成 " + completed + " / " + total + childProgress;
            RenderCompactSubItems(priority);
        }

        private void UpdateCompactPriority(DailyTodoItem priority, int total)
        {
            if (priority != null)
            {
                compactMessageTimer.Stop();
                compactMessageState = null;
                compactMessages.Clear();
                compactMessageIndex = -1;
                compactPriority.FontSize = 14;
                compactPriority.Text = priority.Text ?? "";
                return;
            }

            int backlogCount = data.BacklogItems == null ? 0 : data.BacklogItems.Count;
            string state = (total == 0 ? "empty" : "completed") + ":" + total + ":" + backlogCount;
            if (compactMessageState != state || compactMessages.Count == 0)
            {
                compactMessageState = state;
                compactMessages = BuildCompactMessages(total, backlogCount);
                compactMessageIndex = compactMessages.Count == 0 ? -1 : compactMessageRandom.Next(compactMessages.Count);
            }
            compactPriority.FontSize = 13.2;
            compactPriority.Text = compactMessageIndex < 0 ? "今天想先做点什么？"
                : compactMessages[compactMessageIndex];
            if (!compactMessageTimer.IsEnabled) compactMessageTimer.Start();
        }

        private void RotateCompactMessage()
        {
            if (compactMessages == null || compactMessages.Count < 2 || CurrentTodayItem() != null) return;
            compactMessageIndex = (compactMessageIndex + 1) % compactMessages.Count;
            compactPriority.Text = compactMessages[compactMessageIndex];
        }

        private static List<string> BuildCompactMessages(int total, int backlogCount)
        {
            var messages = new List<string>();
            if (total <= 0)
            {
                messages.Add("今天想先做点什么？");
                messages.Add("给今天定个小目标吧");
                messages.Add("慢慢来，从一件小事开始");
                messages.Add("今天也给自己留点从容");
                if (backlogCount > 0)
                {
                    messages.Add("堆积区有 " + backlogCount + " 件，挑一件吗？");
                    messages.Add("还有 " + backlogCount + " 件暂存，今天做一点？");
                }
                return messages;
            }

            messages.Add("今天 " + total + " 件都完成了，辛苦啦");
            messages.Add("今天安排的事情都搞定了");
            messages.Add("完成得很漂亮，安心休息吧");
            messages.Add("清单已完成，还想做点什么？");
            messages.Add("今天做得很好，给自己松口气");
            if (backlogCount > 0)
            {
                messages.Add("堆积区还有 " + backlogCount + " 件，不着急");
                messages.Add("想继续的话，可以再挑一件");
            }
            return messages;
        }

        private void RenderCompactSubItems(DailyTodoItem item)
        {
            compactSubItemsPanel.Children.Clear();
            int count = item == null || item.SubItems == null ? 0 : item.SubItems.Count;
            bool showSubItems = count > 0 && data.ShowCompactSubItems;
            compactPriority.Cursor = count > 0 ? Cursors.Hand : Cursors.Arrow;
            compactPriority.ToolTip = count == 0 ? null
                : (data.ShowCompactSubItems ? "点击隐藏子事项" : "点击显示子事项");
            double subItemsHeight = !showSubItems ? 0
                : Math.Min(count, CompactSubItemRowsVisible) * CompactSubItemRowHeight + 3;
            compactSubItemsRow.Height = new GridLength(subItemsHeight);

            if (showSubItems && item != null && item.SubItems != null)
            {
                foreach (DailyTodoSubItem source in item.SubItems)
                {
                    DailyTodoSubItem subItem = source;
                    var row = new Grid { Height = CompactSubItemRowHeight };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    Button check = SmallButton(subItem.Completed ? "✓" : "", 18);
                    check.Height = 18; check.FontSize = 8; check.Padding = new Thickness(0);
                    check.Background = Brushes.Transparent;
                    check.BorderBrush = new SolidColorBrush(Color.FromArgb(75, 235, 238, 244));
                    ApplyButtonTemplate(check, 9);
                    check.Click += delegate(object sender, RoutedEventArgs args)
                    {
                        ToggleSubItem(subItem);
                        args.Handled = true;
                    };
                    row.Children.Add(check);
                    var text = new TextBlock
                    {
                        Text = subItem.Text ?? "", FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5,
                        Foreground = subItem.Completed ? SecondaryText() : Brushes.White,
                        TextDecorations = subItem.Completed ? TextDecorations.Strikethrough : null,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(text, 1); row.Children.Add(text);
                    compactSubItemsPanel.Children.Add(row);
                }
            }

            if (!expanded)
            {
                double desiredHeight = CompactBaseHeight + subItemsHeight;
                if (Math.Abs(Height - desiredHeight) > 0.5)
                {
                    double bottom = Top + Height;
                    Height = desiredHeight;
                    if (positioned)
                    {
                        Top = bottom - Height;
                        KeepOnScreen();
                    }
                }
            }
        }

        private double CompactHeightFor(DailyTodoItem item)
        {
            int count = item == null || item.SubItems == null ? 0 : item.SubItems.Count;
            return CompactBaseHeight + (count == 0 || !data.ShowCompactSubItems ? 0
                : Math.Min(count, CompactSubItemRowsVisible) * CompactSubItemRowHeight + 3);
        }

        private void ToggleSubItem(DailyTodoSubItem subItem)
        {
            if (subItem == null) return;
            subItem.Completed = !subItem.Completed;
            store.Save(data);
            RenderCompact();
            RenderExpanded(false);
        }

        private void RenderExpanded()
        {
            RenderExpanded(true);
        }

        private void RenderExpanded(bool animateCards)
        {
            TodoDay day = FindDay(viewedDate, false);
            List<DailyTodoItem> items = day == null ? new List<DailyTodoItem>() : day.Items;
            string prefix = viewedDate == DateTime.Today ? "今天 · " : "";
            dateLabel.Text = prefix + viewedDate.ToString("M月d日 dddd", new CultureInfo("zh-CN"));
            int completed = 0;
            foreach (DailyTodoItem item in items) if (item.Completed) completed++;
            progressText.Text = "已完成 " + completed + " / 全部 " + items.Count;
            importButton.Visibility = viewedDate == DateTime.Today && GetImportCandidates().Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            RenderCards(day, items, animateCards);
        }

        private void RenderCards(TodoDay day, List<DailyTodoItem> items, bool animateCards)
        {
            cardCanvas.Children.Clear();
            renderedCards.Clear();
            focusIconBindings.Clear();
            activeSubItemInput = null;
            double cardWidth = Math.Max(330, Width - 30);
            cardCanvas.Width = cardWidth;
            if (!string.IsNullOrEmpty(expandedSubItemOwnerId) &&
                !items.Any(delegate(DailyTodoItem candidate) { return candidate.Id == expandedSubItemOwnerId; }))
                expandedSubItemOwnerId = null;
            cardCanvas.Height = Math.Max(330, items.Count == 0 ? 330
                : CardTopAt(items.Count - 1, items) + CardHeightAt(items[items.Count - 1]) + 8);

            if (items.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = viewedDate == DateTime.Today ? "写下今天最重要的一件事" : "这一天还没有待办",
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Foreground = SecondaryText()
                };
                Canvas.SetLeft(empty, 14); Canvas.SetTop(empty, 28); cardCanvas.Children.Add(empty);
                return;
            }

            for (int index = 0; index < items.Count; index++)
            {
                DailyTodoItem item = items[index];
                var card = CreateCard(day, item, CardWidthAt(index), index, items.Count);
                Canvas.SetLeft(card, CardLeftAt(index));
                Canvas.SetTop(card, CardTopAt(index, items));
                Panel.SetZIndex(card, items.Count - index);
                cardCanvas.Children.Add(card);
                renderedCards[item.Id] = card;
                if (animateCards) AnimateCard(card, index);
            }
        }

        private Border CreateCard(TodoDay day, DailyTodoItem item, double width, int index, int total)
        {
            bool subItemsExpanded = expandedSubItemOwnerId == item.Id;
            var card = new Border
            {
                Width = width, Height = CardHeightAt(item), CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(148, 25, 27, 33)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(52, 255, 255, 255)), BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 6, 8, 8)
            };
            ApplyCardLayer(card, index, false);
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            if (subItemsExpanded) content.RowDefinitions.Add(new RowDefinition());
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(33) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            var check = SmallButton(item.Completed ? "✓" : "", 25);
            check.Height = 25; check.VerticalAlignment = VerticalAlignment.Center;
            check.Foreground = item.Completed ? Brushes.White : SecondaryText();
            row.Children.Add(check);
            var text = new TextBox
            {
                Text = item.Text ?? "", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = item.Completed ? SecondaryText() : Brushes.White,
                FontFamily = new FontFamily(item.Completed ? "Segoe UI" : "Segoe UI Semibold"), FontSize = 12.5,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(2, 1, 4, 1),
                TextWrapping = TextWrapping.NoWrap,
                TextDecorations = item.Completed ? System.Windows.TextDecorations.Strikethrough : null
            };
            text.TextChanged += delegate { item.Text = text.Text; QueueSave(); };
            check.Click += delegate { ToggleCompleted(day, item, card, check, text); };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var focusIcon = new FocusRingIcon { Width = 22, Height = 22 };
            var focus = FocusButton(focusIcon);
            focus.ToolTip = "设置专注倒计时";
            DailyTodoItem currentItem = CurrentTodayItem();
            focus.Visibility = !item.Completed && viewedDate == DateTime.Today &&
                currentItem != null && currentItem.Id == item.Id ? Visibility.Visible : Visibility.Hidden;
            focus.Click += delegate(object sender, RoutedEventArgs args)
            {
                OpenFocusTimer(item);
                args.Handled = true;
            };
            focusIconBindings.Add(new FocusIconBinding { ItemId = item.Id, Icon = focusIcon, Button = focus });
            UpdateFocusIcon(focusIcon, item.Id);
            UpdateFocusTooltip(focus, item.Id);
            Grid.SetColumn(focus, 2); row.Children.Add(focus);
            var stackIcon = new StackedItemsIcon { Width = 18, Height = 18 };
            var stack = new Button
            {
                Content = stackIcon, Width = 26, Height = 25, Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0), ToolTip = "放入堆积事项",
                Visibility = Visibility.Hidden, Focusable = false
            };
            ApplyButtonTemplate(stack, 7);
            stack.Click += delegate(object sender, RoutedEventArgs args)
            {
                MoveToBacklog(day, item);
                args.Handled = true;
            };
            Grid.SetColumn(stack, 3); row.Children.Add(stack);
            var handleText = new TextBlock
            {
                Text = "⋮⋮", FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
                Foreground = item.Completed ? new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)) : SecondaryText(),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            var handle = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Arrow, Child = handleText,
                ToolTip = subItemsExpanded ? "收起子事项后可拖动排序" : "拖动排序"
            };
            handle.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (item.Completed || expandedSubItemOwnerId == item.Id) return;
                BeginCardDrag(day, item, card, handle, args);
            };
            handle.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (dragCard == null) return;
                if (args.LeftButton != MouseButtonState.Pressed) { EndCardDrag(); return; }
                ContinueCardDrag(args.GetPosition(cardCanvas));
                args.Handled = true;
            };
            handle.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (dragCard == null) return;
                EndCardDrag();
                args.Handled = true;
            };
            Grid.SetColumn(handle, 4); row.Children.Add(handle);
            var delete = SmallButton("×", 26); delete.Height = 25; delete.Click += delegate { DeleteItem(day, item); };
            Grid.SetColumn(delete, 5); row.Children.Add(delete);
            content.Children.Add(row);

            int subCompleted = item.SubItems.Count(delegate(DailyTodoSubItem subItem) { return subItem.Completed; });
            var subFooter = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            subFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
            subFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var subToggle = new Button
            {
                Content = subItemsExpanded ? "⌄" : "›", Width = 22, Height = 20, Padding = new Thickness(0, 0, 0, 1),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Foreground = SecondaryText(),
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand, Focusable = false,
                ToolTip = subItemsExpanded ? "收起子事项" : "展开子事项",
                Visibility = item.Completed && item.SubItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible
            };
            ApplyButtonTemplate(subToggle, 6);
            subToggle.Click += delegate(object sender, RoutedEventArgs args)
            {
                expandedSubItemOwnerId = subItemsExpanded ? null : item.Id;
                revealedSubItemInputOwnerId = null;
                RenderExpanded(false);
                args.Handled = true;
            };
            subFooter.Children.Add(subToggle);
            var subProgress = new TextBlock
            {
                Text = item.SubItems.Count == 0 ? "" : subCompleted + "/" + item.SubItems.Count,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 9.5,
                Foreground = item.SubItems.Count > 0 && subCompleted == item.SubItems.Count
                    ? new SolidColorBrush(Color.FromRgb(126, 211, 144)) : SecondaryText(),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = item.SubItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible
            };
            Grid.SetColumn(subProgress, 1); subFooter.Children.Add(subProgress);
            Grid.SetRow(subFooter, 1); content.Children.Add(subFooter);

            if (subItemsExpanded)
            {
                Grid subPanel = BuildSubItemsPanel(item);
                Grid.SetRow(subPanel, 2); Grid.SetColumnSpan(subPanel, 6); content.Children.Add(subPanel);
            }
            card.Child = content;
            card.MouseEnter += delegate { if (!item.Completed) stack.Visibility = Visibility.Visible; };
            card.MouseLeave += delegate { stack.Visibility = Visibility.Hidden; };
            return card;
        }

        private Grid BuildSubItemsPanel(DailyTodoItem item)
        {
            var panel = new Grid { Margin = new Thickness(1, 3, 2, 0) };
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
            var separator = new Border
            {
                Height = 1, Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Top
            };
            panel.Children.Add(separator);

            int rowIndex = 1;
            foreach (DailyTodoSubItem subItem in item.SubItems)
            {
                panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
                Grid row = BuildSubItemRow(item, subItem);
                Grid.SetRow(row, rowIndex++); panel.Children.Add(row);
            }

            if (!item.Completed)
            {
                bool inputVisible = revealedSubItemInputOwnerId == item.Id;
                panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(inputVisible ? 31 : 24) });
                FrameworkElement addControl = inputVisible ? BuildSubItemInput(item) : BuildRevealSubItemInput(item);
                Grid.SetRow(addControl, rowIndex); panel.Children.Add(addControl);
            }
            return panel;
        }

        private FrameworkElement BuildRevealSubItemInput(DailyTodoItem item)
        {
            var reveal = new Button
            {
                Content = "＋", Width = 22, Height = 18, Margin = new Thickness(31, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Foreground = SecondaryText(),
                Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
                Opacity = 0.62, Cursor = Cursors.Hand, Focusable = false, ToolTip = "添加子事项"
            };
            ApplyButtonTemplate(reveal, 8);
            reveal.Click += delegate(object sender, RoutedEventArgs args)
            {
                revealedSubItemInputOwnerId = item.Id;
                RenderExpanded(false);
                FocusSubItemInput(item.Id);
                args.Handled = true;
            };
            return reveal;
        }

        private FrameworkElement BuildSubItemInput(DailyTodoItem item)
        {
            var host = new Border
            {
                Height = 25, Margin = new Thickness(32, 3, 28, 3),
                Background = new SolidColorBrush(Color.FromArgb(11, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(5)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(23) });
            var placeholder = new TextBlock
            {
                Text = "添加一步…", FontFamily = new FontFamily("Segoe UI"), FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromArgb(95, 220, 224, 232)),
                Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            var input = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Brushes.White, CaretBrush = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5,
                Padding = new Thickness(6, 1, 3, 1), VerticalContentAlignment = VerticalAlignment.Center
            };
            activeSubItemInput = input;
            input.TextChanged += delegate { placeholder.Visibility = input.Text.Length == 0 && !input.IsKeyboardFocused
                    ? Visibility.Visible : Visibility.Collapsed; };
            input.GotKeyboardFocus += delegate { placeholder.Visibility = Visibility.Collapsed; };
            input.LostKeyboardFocus += delegate
            {
                placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            Action addSubItem = delegate
            {
                string value = (input.Text ?? "").Trim();
                if (value.Length == 0) return;
                item.SubItems.Add(new DailyTodoSubItem
                {
                    Id = Guid.NewGuid().ToString("N"), Text = value, CreatedAt = DateTime.Now
                });
                revealedSubItemInputOwnerId = item.Id;
                store.Save(data);
                RenderCompact();
                RenderExpanded(false);
                FocusSubItemInput(item.Id);
            };
            input.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter) { addSubItem(); args.Handled = true; }
                else if (args.Key == Key.Escape)
                {
                    revealedSubItemInputOwnerId = null;
                    RenderExpanded(false);
                    args.Handled = true;
                }
            };
            grid.Children.Add(placeholder);
            grid.Children.Add(input);
            var enterHint = new TextBlock
            {
                Text = "↵", FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(75, 220, 224, 232)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            Grid.SetColumn(enterHint, 1); grid.Children.Add(enterHint);
            host.Child = grid;
            return host;
        }

        private void FocusSubItemInput(string itemId)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (expandedSubItemOwnerId != itemId || revealedSubItemInputOwnerId != itemId ||
                    activeSubItemInput == null) return;
                activeSubItemInput.Focus();
                Keyboard.Focus(activeSubItemInput);
                activeSubItemInput.CaretIndex = activeSubItemInput.Text.Length;
            }), DispatcherPriority.Input);
        }

        private Grid BuildSubItemRow(DailyTodoItem owner, DailyTodoSubItem subItem)
        {
            var row = new Grid { Margin = new Thickness(25, 2, 1, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            Button check = SmallButton(subItem.Completed ? "✓" : "", 21);
            check.Height = 21; check.FontSize = 9;
            check.Foreground = subItem.Completed ? Brushes.White : SecondaryText();
            check.Click += delegate
            {
                ToggleSubItem(subItem);
            };
            row.Children.Add(check);
            var text = new TextBox
            {
                Text = subItem.Text ?? "", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = subItem.Completed ? SecondaryText() : Brushes.White,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(1, 0, 4, 0),
                TextDecorations = subItem.Completed ? TextDecorations.Strikethrough : null
            };
            text.TextChanged += delegate { subItem.Text = text.Text; QueueSave(); };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            Button delete = SmallButton("×", 22); delete.Height = 21; delete.FontSize = 9;
            delete.ToolTip = "删除子事项";
            delete.Click += delegate
            {
                owner.SubItems.Remove(subItem);
                if (owner.Completed && owner.SubItems.Count == 0)
                    expandedSubItemOwnerId = null;
                store.Save(data);
                RenderAll();
            };
            Grid.SetColumn(delete, 2); row.Children.Add(delete);
            return row;
        }

        private double CardHeightAt(DailyTodoItem item)
        {
            if (item == null || expandedSubItemOwnerId != item.Id) return CardHeight;
            double addArea = item.Completed ? 0 : (revealedSubItemInputOwnerId == item.Id ? 31 : 24);
            return CardHeight + item.SubItems.Count * 30 + 4 + addArea;
        }

        private double CardTopAt(int index, IList<DailyTodoItem> items)
        {
            double top = index * CardStep;
            for (int previous = 0; previous < index; previous++)
                top += CardHeightAt(items[previous]) - CardHeight;
            return top;
        }

        private static double CardInset(int index)
        {
            return Math.Min(index, 4) * 3;
        }

        private double CardWidthAt(int index)
        {
            return Math.Max(300, cardCanvas.Width - 4 - CardInset(index) * 2);
        }

        private double CardLeftAt(int index)
        {
            return (cardCanvas.Width - CardWidthAt(index)) / 2.0;
        }

        private static void ApplyCardLayer(Border card, int index, bool lifted)
        {
            byte opacity = (byte)Math.Max(108, 148 - index * 9);
            card.Background = new SolidColorBrush(Color.FromArgb(opacity, 25, 27, 33));
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(52, 255, 255, 255));
            card.Effect = lifted
                ? new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Opacity = 0.28 }
                : index == 0
                    ? new DropShadowEffect { BlurRadius = 11, ShadowDepth = 3, Opacity = 0.20 }
                    : new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = 0.08 };
        }

        private void BeginCardDrag(TodoDay day, DailyTodoItem item, Border card, UIElement capture, MouseButtonEventArgs args)
        {
            dragDay = day;
            dragItem = item;
            dragCard = card;
            dragCapture = capture;
            Point mouse = args.GetPosition(cardCanvas);
            double top = Canvas.GetTop(card);
            dragGrabOffset = mouse.Y - (double.IsNaN(top) ? 0 : top);
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = 0.96;
            ApplyCardLayer(card, day.Items.IndexOf(item), true);
            Panel.SetZIndex(card, 10000);
            capture.CaptureMouse();
            args.Handled = true;
        }

        private void ContinueCardDrag(Point mouse)
        {
            if (dragDay == null || dragItem == null || dragCard == null) return;
            backlogPanel.SetDropHighlight(backlogPanel.IsPointerOverTab());
            int openCount = FirstCompletedIndex(dragDay.Items);
            if (openCount <= 0) return;
            double maximumTop = CardTopAt(openCount - 1, dragDay.Items);
            double top = Math.Max(0, Math.Min(mouse.Y - dragGrabOffset, maximumTop));
            dragCard.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetTop(dragCard, top);

            int desiredIndex = 0;
            double nearestDistance = double.MaxValue;
            for (int candidate = 0; candidate < openCount; candidate++)
            {
                double distance = Math.Abs(top - CardTopAt(candidate, dragDay.Items));
                if (distance < nearestDistance) { nearestDistance = distance; desiredIndex = candidate; }
            }
            int currentIndex = dragDay.Items.IndexOf(dragItem);
            if (currentIndex < 0 || currentIndex == desiredIndex) return;
            dragDay.Items.RemoveAt(currentIndex);
            dragDay.Items.Insert(desiredIndex, dragItem);
            AnimateCardsToCurrentOrder();
        }

        private void AnimateCardsToCurrentOrder()
        {
            if (dragDay == null) return;
            for (int index = 0; index < dragDay.Items.Count; index++)
            {
                Border card;
                if (!renderedCards.TryGetValue(dragDay.Items[index].Id, out card) || ReferenceEquals(card, dragCard)) continue;
                Panel.SetZIndex(card, dragDay.Items.Count - index);
                ApplyCardLayer(card, index, false);
                AnimateCardPlacement(card, index, dragDay.Items);
            }
        }

        private void AnimateCardPlacement(Border card, int index, IList<DailyTodoItem> items)
        {
            AnimateCardValue(card, Canvas.TopProperty, CardTopAt(index, items));
            AnimateCardValue(card, Canvas.LeftProperty, CardLeftAt(index));
            AnimateCardValue(card, WidthProperty, CardWidthAt(index));
        }

        private static void AnimateCardValue(Border card, DependencyProperty property, double target)
        {
            double current = (double)card.GetValue(property);
            if (double.IsNaN(current)) current = target;
            card.BeginAnimation(property, null);
            card.SetValue(property, target);
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(145))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            card.BeginAnimation(property, animation);
        }

        private static void AnimateCardValueSmooth(Border card, DependencyProperty property, double target,
            int durationMilliseconds, int beginMilliseconds, Action completed)
        {
            double current = (double)card.GetValue(property);
            if (double.IsNaN(current)) current = target;
            card.BeginAnimation(property, null);
            card.SetValue(property, target);
            var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                BeginTime = TimeSpan.FromMilliseconds(beginMilliseconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop
            };
            if (completed != null) animation.Completed += delegate { completed(); };
            card.BeginAnimation(property, animation);
        }

        private void EndCardDrag()
        {
            if (dragDay == null || dragItem == null || dragCard == null) return;
            TodoDay day = dragDay;
            DailyTodoItem item = dragItem;
            Border card = dragCard;
            UIElement capture = dragCapture;
            int index = Math.Max(0, day.Items.IndexOf(item));
            bool moveToBacklog = !item.Completed && backlogPanel.IsPointerOverTab();

            dragDay = null;
            dragItem = null;
            dragCard = null;
            dragCapture = null;
            if (capture != null && capture.IsMouseCaptured) capture.ReleaseMouseCapture();
            backlogPanel.SetDropHighlight(false);

            if (moveToBacklog)
            {
                card.Opacity = 1;
                MoveToBacklog(day, item);
                return;
            }

            card.Opacity = 1;
            ApplyCardLayer(card, index, false);
            Panel.SetZIndex(card, day.Items.Count - index);
            AnimateCardPlacement(card, index, day.Items);
            store.Save(data);
            RenderCompact();
            UpdateFocusIcons();
        }

        private static void AnimateCard(Border card, int index)
        {
            int delay = Math.Min(index, 8) * 38;
            card.Opacity = 0;
            var translate = new TranslateTransform(0, 13);
            card.RenderTransform = translate;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slide = new DoubleAnimation(13, 0, TimeSpan.FromMilliseconds(240))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            card.BeginAnimation(OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private List<DailyTodoItem> GetImportCandidates()
        {
            var candidates = new List<DailyTodoItem>();
            TodoDay yesterday = FindDay(DateTime.Today.AddDays(-1), false);
            if (yesterday == null) return candidates;
            TodoDay today = FindDay(DateTime.Today, false);
            foreach (DailyTodoItem item in yesterday.Items)
            {
                if (item.Completed) continue;
                bool imported = today != null && today.ImportedSourceIds.Contains(item.Id);
                if (!imported) candidates.Add(item);
            }
            return candidates;
        }

        private void MaybePromptImport()
        {
            if (viewedDate != DateTime.Today || dialogOpen) return;
            string todayKey = DateKey(DateTime.Today);
            if (data.LastImportPromptDate == todayKey) return;
            List<DailyTodoItem> candidates = GetImportCandidates();
            if (candidates.Count == 0) return;
            data.LastImportPromptDate = todayKey;
            store.Save(data);
            OpenImportDialog();
        }

        private void OpenImportDialog()
        {
            if (viewedDate != DateTime.Today) return;
            List<DailyTodoItem> candidates = GetImportCandidates();
            if (candidates.Count == 0) { RenderExpanded(); return; }
            dialogOpen = true;
            try
            {
                var dialog = new ImportWindow(candidates) { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    if (dialog.SelectedAction == ImportAction.AddToToday)
                        ImportItems(dialog.SelectedItems);
                    else if (dialog.SelectedAction == ImportAction.AddToBacklog)
                        ImportItemsToBacklog(dialog.SelectedItems);
                }
            }
            finally { dialogOpen = false; Activate(); }
        }

        private void ImportItems(List<DailyTodoItem> selected)
        {
            if (selected == null || selected.Count == 0) return;
            TodoDay today = FindDay(DateTime.Today, true);
            int insert = FirstCompletedIndex(today.Items);
            foreach (DailyTodoItem source in selected)
            {
                if (today.ImportedSourceIds.Contains(source.Id)) continue;
                today.Items.Insert(insert++, new DailyTodoItem
                {
                    Id = Guid.NewGuid().ToString("N"), Text = source.Text, CreatedAt = DateTime.Now,
                    SourceId = source.Id, SubItems = CloneSubItems(source)
                });
                today.ImportedSourceIds.Add(source.Id);
            }
            SaveNow();
            RenderAll();
        }

        private void ImportItemsToBacklog(List<DailyTodoItem> selected)
        {
            if (selected == null || selected.Count == 0) return;
            TodoDay today = FindDay(DateTime.Today, true);
            string sourceDate = DateKey(DateTime.Today.AddDays(-1));
            foreach (DailyTodoItem source in selected)
            {
                if (today.ImportedSourceIds.Contains(source.Id)) continue;
                data.BacklogItems.Add(new DailyTodoItem
                {
                    Id = Guid.NewGuid().ToString("N"), Text = source.Text, CreatedAt = DateTime.Now,
                    SourceId = source.Id, BacklogSourceDate = sourceDate, SubItems = CloneSubItems(source)
                });
                today.ImportedSourceIds.Add(source.Id);
            }
            SaveNow();
            RenderAll();
            backlogPanel.Refresh();
        }

        public void ShowToday()
        {
            if (!IsVisible) Show();
            ShowDate(DateTime.Today);
            Activate();
            if (!expanded) Expand();
        }

        private void ApplyToolWindowStyle()
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExToolWindow);
        }

        internal static TextBox InputBox(string tooltip)
        {
            return new TextBox
            {
                ToolTip = tooltip, FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                Foreground = Brushes.White, CaretBrush = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7), VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        internal static Button SmallButton(string text, double width)
        {
            var button = new Button
            {
                Content = text, Width = width, Height = 28, FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), BorderThickness = new Thickness(1)
            };
            ApplyButtonTemplate(button, 7);
            return button;
        }

        internal static void ApplyButtonTemplate(Button button, double radius)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ButtonBorder";
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            border.AppendChild(content);
            template.VisualTree = border;
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.68, "ButtonBorder"));
            template.Triggers.Add(pressed);
            button.Template = template;
        }

        private static Style MinimalScrollBarStyle()
        {
            const string xaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type ScrollBar}'>
  <Setter Property='Width' Value='8'/>
  <Setter Property='MinWidth' Value='8'/>
  <Setter Property='Margin' Value='2,0,0,0'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type ScrollBar}'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track'
                 Orientation='Vertical'
                 IsDirectionReversed='True'
                 Focusable='False'
                 Minimum='{TemplateBinding Minimum}'
                 Maximum='{TemplateBinding Maximum}'
                 Value='{TemplateBinding Value}'
                 ViewportSize='{TemplateBinding ViewportSize}'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='{x:Static ScrollBar.PageUpCommand}'
                            Background='Transparent' BorderThickness='0'
                            Focusable='False' Opacity='0'/>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb MinHeight='26' Background='#668F949E'>
                <Thumb.Template>
                  <ControlTemplate TargetType='{x:Type Thumb}'>
                    <Border x:Name='Grip' Margin='1,2' CornerRadius='3'
                            Background='{TemplateBinding Background}'/>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'>
                        <Setter TargetName='Grip' Property='Background' Value='#A6B8BDC7'/>
                      </Trigger>
                      <Trigger Property='IsDragging' Value='True'>
                        <Setter TargetName='Grip' Property='Background' Value='#D6DDE1E8'/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='{x:Static ScrollBar.PageDownCommand}'
                            Background='Transparent' BorderThickness='0'
                            Focusable='False' Opacity='0'/>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        internal static Brush NormalBackground() { return new SolidColorBrush(Color.FromArgb(72, 17, 20, 27)); }
        internal static Brush HoverBackground() { return new SolidColorBrush(Color.FromArgb(112, 17, 20, 27)); }
        internal static Brush SecondaryText() { return new SolidColorBrush(Color.FromArgb(150, 220, 224, 232)); }
    }

    internal sealed class ChevronIcon : FrameworkElement
    {
        public bool PointsRight;
        private Brush stroke = new SolidColorBrush(Color.FromArgb(220, 240, 242, 246));
        public Brush Stroke
        {
            get { return stroke; }
            set { stroke = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double centerX = ActualWidth / 2;
            double centerY = ActualHeight / 2;
            double direction = PointsRight ? 1 : -1;
            var pen = new Pen(stroke, 1.65)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            Point tip = new Point(centerX + direction * 2.4, centerY);
            dc.DrawLine(pen, new Point(centerX - direction * 2.2, centerY - 4.4), tip);
            dc.DrawLine(pen, tip, new Point(centerX - direction * 2.2, centerY + 4.4));
        }
    }

    internal sealed class StackedItemsIcon : FrameworkElement
    {
        private bool inverted;
        public bool Inverted
        {
            get { return inverted; }
            set { inverted = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double width = Math.Max(12, ActualWidth);
            double center = width / 2;
            double top = Math.Max(2, (ActualHeight - 16) / 2);
            Brush primary = inverted ? new SolidColorBrush(Color.FromRgb(28, 31, 37)) : Brushes.White;
            Brush secondary = inverted ? new SolidColorBrush(Color.FromArgb(155, 28, 31, 37))
                : new SolidColorBrush(Color.FromArgb(150, 235, 238, 244));
            Brush tertiary = inverted ? new SolidColorBrush(Color.FromArgb(95, 28, 31, 37))
                : new SolidColorBrush(Color.FromArgb(85, 235, 238, 244));
            DrawLayer(dc, center, top + 8, width - 3, tertiary);
            DrawLayer(dc, center, top + 4, width - 3, secondary);
            DrawLayer(dc, center, top, width - 3, primary);
        }

        private static void DrawLayer(DrawingContext dc, double center, double y, double width, Brush brush)
        {
            double half = width / 2;
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new Point(center, y), false, true);
                context.LineTo(new Point(center + half, y + 3.5), true, false);
                context.LineTo(new Point(center, y + 7), true, false);
                context.LineTo(new Point(center - half, y + 3.5), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(brush, 1.45) { LineJoin = PenLineJoin.Round }, geometry);
        }
    }

    internal sealed class TrashCanIcon : FrameworkElement
    {
        private Brush stroke = new SolidColorBrush(Color.FromRgb(231, 70, 63));
        public Brush Stroke
        {
            get { return stroke; }
            set { stroke = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var pen = new Pen(stroke, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double x = ActualWidth / 2;
            dc.DrawLine(pen, new Point(x - 6, 5), new Point(x + 6, 5));
            dc.DrawLine(pen, new Point(x - 3, 3), new Point(x + 3, 3));
            dc.DrawRoundedRectangle(null, pen, new Rect(x - 5, 7, 10, 10), 1.5, 1.5);
            dc.DrawLine(pen, new Point(x - 2, 9), new Point(x - 2, 15));
            dc.DrawLine(pen, new Point(x + 2, 9), new Point(x + 2, 15));
        }
    }

    internal sealed class BacklogSidePanel : IDisposable
    {
        private const double TabWidth = 46;
        private const double TabHeight = 66;
        private readonly DailyTodoWindow owner;
        private readonly Grid host;
        private readonly UIElement mainView;
        private readonly Border backlogHost;
        private readonly List<DailyTodoItem> items;
        private readonly Action<DailyTodoItem> toggleCompleted;
        private readonly Action<List<DailyTodoItem>> moveToToday;
        private readonly Action<List<DailyTodoItem>> deleteItems;
        private readonly Action undoDelete;
        private readonly Window tabWindow;
        private readonly Button tabButton;
        private readonly StackedItemsIcon tabIcon;
        private readonly HashSet<string> selectedIds = new HashSet<string>();
        private bool drawerOpen;
        private bool multiSelect;
        private bool undoVisible;
        private int undoCount;
        private bool disposed;

        public bool IsInteractionActive
        {
            get
            {
                return tabWindow.IsActive || tabWindow.IsMouseOver || owner.IsActive;
            }
        }

        public BacklogSidePanel(DailyTodoWindow owner, Grid host, UIElement mainView,
            List<DailyTodoItem> items,
            Action<DailyTodoItem> toggleCompleted, Action<List<DailyTodoItem>> moveToToday,
            Action<List<DailyTodoItem>> deleteItems, Action undoDelete)
        {
            this.owner = owner;
            this.host = host;
            this.mainView = mainView;
            this.items = items;
            this.toggleCompleted = toggleCompleted;
            this.moveToToday = moveToToday;
            this.deleteItems = deleteItems;
            this.undoDelete = undoDelete;

            tabWindow = ChromeWindow(TabWidth, TabHeight);
            tabIcon = new StackedItemsIcon { Width = 23, Height = 23 };
            tabButton = new Button
            {
                Content = tabIcon, Background = new SolidColorBrush(Color.FromArgb(225, 18, 20, 25)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(1), Padding = new Thickness(9), Cursor = Cursors.Hand,
                ToolTip = "堆积事项"
            };
            DailyTodoWindow.ApplyButtonTemplate(tabButton, 12);
            tabButton.Click += delegate { ToggleDrawer(); };
            tabWindow.Content = tabButton;

            backlogHost = new Border { Visibility = Visibility.Collapsed, Opacity = 0 };
            Panel.SetZIndex(backlogHost, 20);
            host.Children.Add(backlogHost);
            Refresh();
        }

        private Window ChromeWindow(double width, double height)
        {
            return new Window
            {
                Width = width, Height = height, AllowsTransparency = true,
                Background = Brushes.Transparent, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Topmost = owner.Topmost, ShowActivated = false, UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
        }

        public void ShowTab()
        {
            if (disposed) return;
            AttachOwners();
            PositionBesideOwner();
            if (!tabWindow.IsVisible) tabWindow.Show();
            tabWindow.Topmost = owner.Topmost;
        }

        public void HideAll()
        {
            if (disposed) return;
            drawerOpen = false;
            multiSelect = false;
            selectedIds.Clear();
            backlogHost.BeginAnimation(UIElement.OpacityProperty, null);
            backlogHost.Opacity = 0;
            backlogHost.Visibility = Visibility.Collapsed;
            mainView.Visibility = Visibility.Visible;
            tabWindow.Hide();
            ApplyTabState(false);
        }

        private void AttachOwners()
        {
            if (tabWindow.Owner == null) tabWindow.Owner = owner;
        }

        public void PositionBesideOwner()
        {
            if (disposed || !owner.IsVisible) return;
            tabWindow.Left = owner.Left - TabWidth + 1;
            tabWindow.Top = owner.Top + (owner.Height - TabHeight) / 2;
        }

        private void ToggleDrawer()
        {
            if (drawerOpen) CloseDrawer(); else OpenDrawer();
        }

        private void OpenDrawer()
        {
            drawerOpen = true;
            ApplyTabState(false);
            Refresh();
            mainView.Visibility = Visibility.Collapsed;
            backlogHost.Visibility = Visibility.Visible;
            backlogHost.BeginAnimation(UIElement.OpacityProperty, null);
            backlogHost.Opacity = 1;
            backlogHost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            owner.Activate();
        }

        private void CloseDrawer()
        {
            if (!drawerOpen) return;
            drawerOpen = false;
            multiSelect = false;
            selectedIds.Clear();
            ApplyTabState(false);
            mainView.Visibility = Visibility.Visible;
            backlogHost.Opacity = 0;
            var animation = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            };
            animation.Completed += delegate
            {
                if (!drawerOpen) backlogHost.Visibility = Visibility.Collapsed;
            };
            backlogHost.BeginAnimation(UIElement.OpacityProperty, animation);
            owner.Activate();
        }

        private void ApplyTabState(bool dropHighlight)
        {
            if (dropHighlight)
            {
                tabButton.Background = new SolidColorBrush(Color.FromRgb(231, 70, 63));
                tabButton.BorderBrush = Brushes.White;
                tabIcon.Inverted = true;
                return;
            }
            tabButton.Background = drawerOpen ? Brushes.White : new SolidColorBrush(Color.FromArgb(225, 18, 20, 25));
            tabButton.BorderBrush = drawerOpen ? new SolidColorBrush(Color.FromArgb(210, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            tabIcon.Inverted = drawerOpen;
        }

        public bool IsPointerOverTab()
        {
            if (!tabWindow.IsVisible) return false;
            System.Drawing.Point cursor = Forms.Cursor.Position;
            Point local = tabWindow.PointFromScreen(new Point(cursor.X, cursor.Y));
            return local.X >= 0 && local.X <= tabWindow.ActualWidth &&
                local.Y >= 0 && local.Y <= tabWindow.ActualHeight;
        }

        public void SetDropHighlight(bool highlighted)
        {
            ApplyTabState(highlighted);
        }

        public void Refresh()
        {
            if (disposed) return;
            selectedIds.RemoveWhere(delegate(string id)
            {
                return !items.Any(delegate(DailyTodoItem item) { return item.Id == id; });
            });
            backlogHost.Child = BuildDrawerContent();
            ApplyTabState(false);
        }

        private Border BuildDrawerContent()
        {
            var root = new Grid { Margin = new Thickness(14, 12, 12, 11) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            var title = new TextBlock
            {
                Text = "堆积事项  " + items.Count, FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 12.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center
            };
            root.Children.Add(title);

            var list = new StackPanel();
            if (items.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "把暂时不处理的事项放在这里",
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5,
                    Foreground = DailyTodoWindow.SecondaryText(), Margin = new Thickness(4, 18, 0, 0)
                });
            }
            else foreach (DailyTodoItem item in items) list.Children.Add(BuildItemRow(item));
            var scroll = new ScrollViewer
            {
                Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 1, 0, 5)
            };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);

            Grid footer = BuildFooter();
            Grid.SetRow(footer, 2); root.Children.Add(footer);
            return new Border
            {
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(Color.FromArgb(246, 17, 20, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.24 },
                Child = root
            };
        }

        private Border BuildItemRow(DailyTodoItem item)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(31) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            Button check = DailyTodoWindow.SmallButton("", 23);
            check.Height = 23;
            if (multiSelect)
            {
                bool selected = selectedIds.Contains(item.Id);
                check.Content = selected ? "✓" : "";
                check.Foreground = Brushes.White;
                check.Background = selected ? new SolidColorBrush(Color.FromRgb(231, 70, 63)) : Brushes.Transparent;
                check.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(231, 70, 63))
                    : new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
                DailyTodoWindow.ApplyButtonTemplate(check, 6);
                check.Click += delegate
                {
                    if (!selectedIds.Add(item.Id)) selectedIds.Remove(item.Id);
                    Refresh();
                };
            }
            else
            {
                check.Content = item.Completed ? "✓" : "";
                check.Foreground = Brushes.White;
                check.Background = item.Completed ? new SolidColorBrush(Color.FromArgb(95, 255, 255, 255)) : Brushes.Transparent;
                check.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 235, 238, 244));
                DailyTodoWindow.ApplyButtonTemplate(check, 12);
                check.Click += delegate { toggleCompleted(item); };
            }
            check.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(check);
            var text = new TextBlock
            {
                Text = item.Text ?? "", FontFamily = new FontFamily(item.Completed ? "Segoe UI" : "Segoe UI Semibold"),
                FontSize = 10.5, Foreground = item.Completed ? DailyTodoWindow.SecondaryText() : Brushes.White,
                TextDecorations = item.Completed ? TextDecorations.Strikethrough : null,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = item.SubItems != null && item.SubItems.Count > 0
                    ? "子事项 " + item.SubItems.Count(delegate(DailyTodoSubItem subItem) { return subItem.Completed; }) +
                        "/" + item.SubItems.Count
                    : null
            };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var date = new TextBlock
            {
                Text = FormatSourceDate(item.BacklogSourceDate), FontFamily = new FontFamily("Segoe UI"),
                FontSize = 8.8, Foreground = DailyTodoWindow.SecondaryText(),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(date, 2); row.Children.Add(date);
            Button move = InvertedStackActionButton(28);
            move.Click += delegate { moveToToday(new List<DailyTodoItem> { item }); };
            Grid.SetColumn(move, 3); row.Children.Add(move);
            return new Border
            {
                Height = 49, CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 6, 4), Child = row
            };
        }

        private Grid BuildFooter()
        {
            var footer = new Grid { Margin = new Thickness(1, 6, 1, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (!multiSelect)
            {
                Button multiple = DailyTodoWindow.SmallButton("多选", 54);
                multiple.IsEnabled = items.Count > 0;
                multiple.Opacity = items.Count > 0 ? 1 : 0.4;
                multiple.HorizontalAlignment = HorizontalAlignment.Left;
                multiple.Click += delegate { multiSelect = true; selectedIds.Clear(); Refresh(); };
                footer.Children.Add(multiple);
                if (undoVisible)
                {
                    Button undo = DailyTodoWindow.SmallButton("已删除 " + undoCount + " 项 · 撤销", 112);
                    undo.Click += delegate { undoDelete(); };
                    Grid.SetColumn(undo, 2); footer.Children.Add(undo);
                }
                return footer;
            }

            Button cancel = DailyTodoWindow.SmallButton("退出多选", 66);
            cancel.HorizontalAlignment = HorizontalAlignment.Left;
            cancel.Click += delegate { multiSelect = false; selectedIds.Clear(); Refresh(); };
            footer.Children.Add(cancel);
            var count = new TextBlock
            {
                Text = "已选 " + selectedIds.Count, FontFamily = new FontFamily("Segoe UI"), FontSize = 9.5,
                Foreground = DailyTodoWindow.SecondaryText(), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(count, 1); footer.Children.Add(count);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            Button delete = ActionButton(new TrashCanIcon { Width = 19, Height = 19 }, 31, true);
            delete.IsEnabled = selectedIds.Count > 0;
            delete.Opacity = delete.IsEnabled ? 1 : 0.4;
            delete.Click += delegate
            {
                List<DailyTodoItem> selected = SelectedItems();
                multiSelect = false;
                selectedIds.Clear();
                deleteItems(selected);
            };
            actions.Children.Add(delete);
            Button move = InvertedStackActionButton(31);
            move.Margin = new Thickness(7, 0, 0, 0);
            move.IsEnabled = selectedIds.Count > 0;
            move.Opacity = move.IsEnabled ? 1 : 0.4;
            move.Click += delegate
            {
                List<DailyTodoItem> selected = SelectedItems();
                multiSelect = false;
                selectedIds.Clear();
                moveToToday(selected);
            };
            actions.Children.Add(move);
            Grid.SetColumn(actions, 2); footer.Children.Add(actions);
            return footer;
        }

        private List<DailyTodoItem> SelectedItems()
        {
            return items.Where(delegate(DailyTodoItem item) { return selectedIds.Contains(item.Id); }).ToList();
        }

        private static Button ActionButton(FrameworkElement icon, double width, bool destructive)
        {
            Brush red = new SolidColorBrush(Color.FromRgb(231, 70, 63));
            Brush normalBorder = new SolidColorBrush(Color.FromArgb(62, 255, 255, 255));
            Brush normalIcon = new SolidColorBrush(Color.FromArgb(220, 240, 242, 246));
            SetIconStroke(icon, destructive ? red : normalIcon);
            var button = new Button
            {
                Content = icon, Width = width, Height = 27,
                Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                BorderBrush = normalBorder, BorderThickness = new Thickness(1), Padding = new Thickness(0),
                Cursor = Cursors.Hand
            };
            DailyTodoWindow.ApplyButtonTemplate(button, 7);
            button.MouseEnter += delegate
            {
                button.Background = destructive ? red : new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
                button.BorderBrush = destructive ? Brushes.White : new SolidColorBrush(Color.FromArgb(105, 255, 255, 255));
                SetIconStroke(icon, Brushes.White);
            };
            button.MouseLeave += delegate
            {
                button.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
                button.BorderBrush = normalBorder;
                SetIconStroke(icon, destructive ? red : normalIcon);
            };
            return button;
        }

        private static Button InvertedStackActionButton(double width)
        {
            var icon = new StackedItemsIcon { Width = 17, Height = 17, Inverted = true };
            var button = new Button
            {
                Content = icon, Width = width, Height = 27, Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                BorderThickness = new Thickness(1), Padding = new Thickness(0), Cursor = Cursors.Hand,
                ToolTip = "移入今日"
            };
            DailyTodoWindow.ApplyButtonTemplate(button, 7);
            button.MouseEnter += delegate
            {
                button.Background = new SolidColorBrush(Color.FromRgb(224, 227, 232));
                button.BorderBrush = Brushes.White;
            };
            button.MouseLeave += delegate
            {
                button.Background = Brushes.White;
                button.BorderBrush = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));
            };
            return button;
        }

        private static void SetIconStroke(FrameworkElement icon, Brush brush)
        {
            TrashCanIcon trash = icon as TrashCanIcon;
            if (trash != null) { trash.Stroke = brush; return; }
            ChevronIcon chevron = icon as ChevronIcon;
            if (chevron != null) chevron.Stroke = brush;
        }

        private static string FormatSourceDate(string value)
        {
            DateTime date;
            return DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date) ? date.ToString("M月d日", new CultureInfo("zh-CN")) : "";
        }

        public void ShowUndoNotice(int count)
        {
            undoVisible = true;
            undoCount = count;
            Refresh();
        }

        public void ClearUndoNotice()
        {
            undoVisible = false;
            undoCount = 0;
            if (backlogHost.IsVisible) Refresh();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { tabWindow.Close(); } catch { }
        }
    }

    internal sealed class FocusRingIcon : FrameworkElement
    {
        public bool Active;
        public bool Urgent;
        public double Progress;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double size = Math.Max(8, Math.Min(ActualWidth, ActualHeight));
            Point center = new Point(ActualWidth / 2, ActualHeight / 2);
            double thickness = Math.Max(2.4, size * 0.16);
            double radius = size / 2 - thickness / 2 - 1;
            var white = new SolidColorBrush(Color.FromArgb(238, 248, 248, 246));
            var red = new SolidColorBrush(Color.FromRgb(231, 70, 63));
            var blue = new SolidColorBrush(Color.FromRgb(76, 145, 255));
            dc.DrawEllipse(null, new Pen(white, thickness), center, radius, radius);
            double shown = Active ? Math.Max(0.015, Math.Min(1, Progress)) : 0.75;
            var pen = new Pen(Active && !Urgent ? blue : red, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            if (shown >= 0.999)
                dc.DrawEllipse(null, pen, center, radius, radius);
            else
                DrawArc(dc, center, radius, -90, shown * 360, pen);
        }

        internal static void DrawArc(DrawingContext dc, Point center, double radius,
            double startDegrees, double sweepDegrees, Pen pen)
        {
            if (sweepDegrees <= 0.01) return;
            double startRadians = startDegrees * Math.PI / 180.0;
            double endRadians = (startDegrees + sweepDegrees) * Math.PI / 180.0;
            Point start = new Point(center.X + Math.Cos(startRadians) * radius,
                center.Y + Math.Sin(startRadians) * radius);
            Point end = new Point(center.X + Math.Cos(endRadians) * radius,
                center.Y + Math.Sin(endRadians) * radius);
            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(start, false, false);
                context.ArcTo(end, new Size(radius, radius), 0, sweepDegrees > 180,
                    SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    internal sealed class FocusTimerDial : FrameworkElement
    {
        private static readonly DependencyProperty SecondsRevealProperty = DependencyProperty.Register(
            "SecondsReveal", typeof(double), typeof(FocusTimerDial),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        private static readonly System.Media.SoundPlayer MinuteTickPlayer = CreateMinuteTickPlayer();
        private bool dragging;
        private int minutes;
        private bool countdownActive;
        private double countdownSeconds;
        private bool revealSecondsWhenLoaded;

        private double SecondsReveal
        {
            get { return (double)GetValue(SecondsRevealProperty); }
            set { SetValue(SecondsRevealProperty, value); }
        }

        public event Action<int> MinutesChanged;

        public int Minutes
        {
            get { return minutes; }
            set
            {
                int next = Math.Max(0, Math.Min(100, value));
                if (minutes == next) return;
                minutes = next;
                InvalidateVisual();
                Action<int> changed = MinutesChanged;
                if (changed != null) changed(minutes);
            }
        }

        public bool CountdownActive
        {
            get { return countdownActive; }
            set
            {
                if (countdownActive == value) return;
                countdownActive = value;
                if (!IsLoaded)
                {
                    revealSecondsWhenLoaded = value;
                    SecondsReveal = 0;
                }
                else AnimateSecondsReveal(value);
                InvalidateVisual();
            }
        }

        public double CountdownSeconds
        {
            get { return countdownSeconds; }
            set { countdownSeconds = Math.Max(0, value); InvalidateVisual(); }
        }

        public FocusTimerDial()
        {
            Width = 310;
            Height = 310;
            Cursor = Cursors.Hand;
            Focusable = true;
            MouseLeftButtonDown += BeginDrag;
            MouseMove += ContinueDrag;
            MouseLeftButtonUp += EndDrag;
            LostMouseCapture += delegate { dragging = false; };
            Loaded += delegate
            {
                if (revealSecondsWhenLoaded && countdownActive)
                {
                    revealSecondsWhenLoaded = false;
                    AnimateSecondsReveal(true);
                }
            };
        }

        private void AnimateSecondsReveal(bool show)
        {
            double from = SecondsReveal;
            double target = show ? 1.0 : 0.0;
            BeginAnimation(SecondsRevealProperty, null);
            SecondsReveal = target;
            var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(show ? 280 : 220))
            {
                EasingFunction = new CubicEase
                {
                    EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseInOut
                },
                FillBehavior = FillBehavior.Stop
            };
            BeginAnimation(SecondsRevealProperty, animation);
        }

        private static System.Media.SoundPlayer CreateMinuteTickPlayer()
        {
            const int sampleRate = 44100;
            const int sampleCount = 794;
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + sampleCount * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(sampleCount * 2);
                uint noiseState = 0x8B31u;
                double plasticBody = 0;
                for (int index = 0; index < sampleCount; index++)
                {
                    noiseState = noiseState * 1664525u + 1013904223u;
                    double noise = ((noiseState >> 8) / 16777215.0) * 2.0 - 1.0;
                    plasticBody = plasticBody * 0.54 + noise * 0.46;
                    double time = index / (double)sampleRate;
                    double impactEnvelope = Math.Exp(-time / 0.0017);
                    double cavityEnvelope = Math.Exp(-time / 0.0042);
                    double impact = (plasticBody * 0.55 + Math.Sin(2 * Math.PI * 2350 * time) * 0.32) *
                        impactEnvelope;
                    double cavity = (Math.Sin(2 * Math.PI * 1280 * time) * 0.50 +
                        Math.Sin(2 * Math.PI * 1860 * time) * 0.22) * cavityEnvelope;
                    double latch = 0;
                    if (time > 0.0026)
                    {
                        double latchTime = time - 0.0026;
                        latch = 0.25 * Math.Sin(2 * Math.PI * 1680 * latchTime) *
                            Math.Exp(-latchTime / 0.0018);
                    }
                    double sample = (impact + cavity + latch) * 0.68;
                    writer.Write((short)(Math.Max(-1, Math.Min(1, sample)) * short.MaxValue));
                }
            }
            stream.Position = 0;
            var player = new System.Media.SoundPlayer(stream);
            try { player.Load(); } catch { }
            return player;
        }

        private static void PlayMinuteTick()
        {
            try { MinuteTickPlayer.PlaySync(); }
            catch { System.Media.SystemSounds.Asterisk.Play(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            Point center = new Point(ActualWidth / 2, ActualHeight / 2);
            double tickOuter = Math.Min(ActualWidth, ActualHeight) / 2 - 23;
            var red = new SolidColorBrush(Color.FromRgb(231, 70, 63));
            var minor = new SolidColorBrush(Color.FromArgb(165, 235, 238, 244));
            var major = new SolidColorBrush(Color.FromArgb(235, 247, 220, 220));

            if (minutes == 100)
            {
                dc.DrawEllipse(null, new Pen(red, 8)
                {
                    StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
                }, center, tickOuter + 1, tickOuter + 1);
            }
            else if (minutes > 1)
            {
                const double endpointInsetMinutes = 0.7;
                double start = -90 + endpointInsetMinutes * 3.6;
                double sweep = Math.Max(0, (minutes - endpointInsetMinutes * 2) * 3.6);
                FocusRingIcon.DrawArc(dc, center, tickOuter + 1, start, sweep,
                    new Pen(red, 8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            }

            for (int index = 0; index < 100; index++)
            {
                bool isMajor = index % 5 == 0;
                bool selected = minutes == 100 || index <= minutes;
                double inner = tickOuter - (isMajor ? 17 : 10);
                double angle = (-90 + index * 3.6) * Math.PI / 180.0;
                Point from = new Point(center.X + Math.Cos(angle) * inner, center.Y + Math.Sin(angle) * inner);
                Point to = new Point(center.X + Math.Cos(angle) * tickOuter, center.Y + Math.Sin(angle) * tickOuter);
                dc.DrawLine(new Pen(selected ? red : (isMajor ? major : minor), isMajor ? 2.2 : 1.25), from, to);
            }

            double pointerAngle = (-90 + (minutes == 100 ? 360 : minutes * 3.6)) * Math.PI / 180.0;
            Point pointerFrom = new Point(center.X + Math.Cos(pointerAngle) * (tickOuter + 3),
                center.Y + Math.Sin(pointerAngle) * (tickOuter + 3));
            Point pointerTo = new Point(center.X + Math.Cos(pointerAngle) * (tickOuter + 22),
                center.Y + Math.Sin(pointerAngle) * (tickOuter + 22));
            dc.DrawLine(new Pen(red, 2) { StartLineCap = PenLineCap.Round }, pointerFrom, pointerTo);
            dc.DrawEllipse(red, null, pointerTo, 4.2, 4.2);

            int displayedMinutes = minutes;
            int displayedSeconds = 0;
            double secondsReveal = Math.Max(0, Math.Min(1, SecondsReveal));
            if (countdownActive || secondsReveal > 0.001)
            {
                int totalSeconds = Math.Max(0, (int)Math.Ceiling(countdownSeconds));
                displayedMinutes = totalSeconds / 60;
                displayedSeconds = totalSeconds % 60;
            }
            var value = new FormattedText(displayedMinutes.ToString(CultureInfo.InvariantCulture),
                CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Segoe UI Light"), 58, Brushes.White, 1.0);
            FormattedText seconds = null;
            if (countdownActive || secondsReveal > 0.001)
            {
                seconds = new FormattedText(":" + displayedSeconds.ToString("00", CultureInfo.InvariantCulture),
                    CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Light"), 29, Brushes.White, 1.0);
            }
            double groupShift = seconds == null ? 0 : (seconds.Width + 5) * 0.5 * secondsReveal;
            Point valuePosition = new Point(center.X - value.Width / 2 - groupShift,
                center.Y - value.Height / 2 - 7);
            dc.DrawText(value, valuePosition);
            if (seconds != null)
            {
                double secondsY = valuePosition.Y + value.Baseline - seconds.Baseline;
                dc.PushOpacity(secondsReveal);
                dc.DrawText(seconds, new Point(valuePosition.X + value.Width + 5, secondsY));
                dc.Pop();
            }
            var unit = new FormattedText("分钟", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 15, DailyTodoWindow.SecondaryText(), 1.0);
            dc.DrawText(unit, new Point(center.X - unit.Width / 2, center.Y + 43));
        }

        private void BeginDrag(object sender, MouseButtonEventArgs args)
        {
            dragging = true;
            CaptureMouse();
            UpdateFromPoint(args.GetPosition(this));
            args.Handled = true;
        }

        private void ContinueDrag(object sender, MouseEventArgs args)
        {
            if (!dragging || args.LeftButton != MouseButtonState.Pressed) return;
            UpdateFromPoint(args.GetPosition(this));
            args.Handled = true;
        }

        private void EndDrag(object sender, MouseButtonEventArgs args)
        {
            if (!dragging) return;
            UpdateFromPoint(args.GetPosition(this));
            dragging = false;
            ReleaseMouseCapture();
            args.Handled = true;
        }

        private void UpdateFromPoint(Point point)
        {
            double dx = point.X - ActualWidth / 2;
            double dy = point.Y - ActualHeight / 2;
            if (Math.Sqrt(dx * dx + dy * dy) < 35) return;
            double angle = Math.Atan2(dx, -dy);
            if (angle < 0) angle += Math.PI * 2;
            double raw = angle / (Math.PI * 2) * 100;
            if (minutes >= 75 && raw < 10) raw = 100;
            else if (minutes <= 25 && raw > 90) raw = 0;
            double detent = Math.Round(raw / 5.0) * 5;
            int next = (int)Math.Round(Math.Abs(raw - detent) <= 0.7 ? detent : raw);
            next = Math.Max(0, Math.Min(100, next));
            if (next == minutes) return;
            minutes = next;
            InvalidateVisual();
            PlayMinuteTick();
            Action<int> changed = MinutesChanged;
            if (changed != null) changed(minutes);
        }
    }

    internal sealed class FocusTimerWindow : Window
    {
        private readonly FocusTimerDial dial;
        private readonly Button start;
        private readonly Button stop;
        private readonly DispatcherTimer countdownTimer;
        private readonly Func<double> remainingSeconds;
        public int SelectedMinutes { get { return dial.Minutes; } }

        public FocusTimerWindow(string itemText, int initialMinutes, bool active,
            Func<double> remainingSeconds, Action<int> startTimer, Action stopTimer)
        {
            this.remainingSeconds = remainingSeconds;
            Width = 390;
            Height = 475;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape) { DialogResult = false; args.Handled = true; }
            };

            var shell = new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.FromArgb(244, 17, 20, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(65, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 15, 18, 16),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Opacity = 0.28 }
            };
            Content = shell;
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
            shell.Child = root;

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            string displayText = string.IsNullOrWhiteSpace(itemText) ? "当前事项" : itemText.Trim();
            var title = new TextBlock
            {
                Text = "专注于：" + displayText,
                FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 13,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 10, 0)
            };
            header.Children.Add(title);
            var close = DailyTodoWindow.SmallButton("×", 29);
            close.Height = 29;
            close.Click += delegate { DialogResult = false; };
            Grid.SetColumn(close, 1); header.Children.Add(close);
            root.Children.Add(header);

            var dialArea = new Grid();
            dial = new FocusTimerDial { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            dial.Minutes = initialMinutes;
            dial.CountdownActive = active && remainingSeconds != null;
            if (dial.CountdownActive) dial.CountdownSeconds = remainingSeconds();
            dialArea.Children.Add(dial);
            var hint = new TextBlock
            {
                Text = active ? "倒计时进行中 · 拨动刻度可重新设置" : "顺时针拨动刻度，选择本次专注时长",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 9.5,
                Foreground = DailyTodoWindow.SecondaryText(),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 1), IsHitTestVisible = false
            };
            dialArea.Children.Add(hint);
            Grid.SetRow(dialArea, 1); root.Children.Add(dialArea);

            var actions = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            start = DailyTodoWindow.SmallButton(active ? "重新开始" : "开始专注", 104);
            start.Height = 34;
            start.IsEnabled = initialMinutes > 0;
            start.Click += delegate
            {
                if (dial.Minutes <= 0 || startTimer == null) return;
                startTimer(dial.Minutes);
                dial.CountdownSeconds = this.remainingSeconds == null ? dial.Minutes * 60.0 : this.remainingSeconds();
                dial.CountdownActive = true;
                hint.Text = "倒计时进行中 · 拨动刻度可重新设置";
                start.Content = "重新开始";
                stop.Visibility = Visibility.Visible;
                countdownTimer.Start();
            };
            dial.MinutesChanged += delegate(int value)
            {
                if (dial.CountdownActive)
                {
                    dial.CountdownActive = false;
                    hint.Text = "顺时针拨动刻度，选择新的专注时长";
                    if (countdownTimer != null) countdownTimer.Stop();
                }
                start.IsEnabled = value > 0;
            };
            actions.Children.Add(start);
            stop = DailyTodoWindow.SmallButton("结束当前计时", 104);
            stop.Margin = new Thickness(0, 6, 0, 0);
            stop.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            stop.Click += delegate
            {
                if (stopTimer != null) stopTimer();
                countdownTimer.Stop();
                dial.CountdownActive = false;
                hint.Text = "顺时针拨动刻度，选择本次专注时长";
                start.Content = "开始专注";
                stop.Visibility = Visibility.Collapsed;
            };
            actions.Children.Add(stop);
            Grid.SetRow(actions, 2); root.Children.Add(actions);

            countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            countdownTimer.Tick += delegate
            {
                if (!dial.CountdownActive || this.remainingSeconds == null) { countdownTimer.Stop(); return; }
                double seconds = this.remainingSeconds();
                dial.CountdownSeconds = seconds;
                if (seconds <= 0) { countdownTimer.Stop(); DialogResult = false; }
            };
            Loaded += delegate { if (dial.CountdownActive) countdownTimer.Start(); };
            Closed += delegate { countdownTimer.Stop(); };
        }
    }

    internal sealed class ScheduleOption
    {
        public string Label;
        public int Value;
        public override string ToString() { return Label; }
    }

    internal sealed class RecurringRulesWindow : Window
    {
        private readonly List<RecurringTodoRule> rules;
        private readonly HashSet<string> applyTodayRuleIds = new HashSet<string>();
        private readonly TextBox input;
        private readonly ComboBox frequency;
        private readonly ComboBox schedule;
        private readonly StackPanel rows;
        public bool Changed { get; private set; }
        public IEnumerable<string> ApplyTodayRuleIds { get { return applyTodayRuleIds; } }

        public RecurringRulesWindow(List<RecurringTodoRule> rules)
        {
            this.rules = rules;
            Width = 450; Height = 500; AllowsTransparency = true; Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var shell = new Border
            {
                CornerRadius = new CornerRadius(15), Background = new SolidColorBrush(Color.FromArgb(246, 17, 20, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), BorderThickness = new Thickness(1),
                Padding = new Thickness(17), Effect = new DropShadowEffect { BlurRadius = 16, Opacity = 0.22 }
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(57) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(84) });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(39) });

            var title = new StackPanel();
            title.Children.Add(new TextBlock
            {
                Text = "周期性任务", FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 14, Foreground = Brushes.White
            });
            title.Children.Add(new TextBlock
            {
                Text = "每天、每周或每月自动放入当天待办 · 修改只影响以后生成的事项",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 9.5,
                Foreground = DailyTodoWindow.SecondaryText(), Margin = new Thickness(0, 4, 0, 0)
            });
            root.Children.Add(title);

            var form = new Grid();
            form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            form.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            input = DailyTodoWindow.InputBox("固定事项内容");
            input.ToolTip = "固定事项内容";
            form.Children.Add(input);
            var scheduleRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            scheduleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(114) });
            scheduleRow.ColumnDefinitions.Add(new ColumnDefinition());
            scheduleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            frequency = DarkComboBox();
            frequency.Items.Add("每天"); frequency.Items.Add("每周"); frequency.Items.Add("每月");
            frequency.SelectedIndex = 0;
            frequency.SelectionChanged += delegate { RebuildScheduleOptions(); };
            scheduleRow.Children.Add(frequency);
            schedule = DarkComboBox(); schedule.Margin = new Thickness(7, 0, 7, 0);
            Grid.SetColumn(schedule, 1); scheduleRow.Children.Add(schedule);
            var add = DailyTodoWindow.SmallButton("添加", 66); add.Click += delegate { AddRule(); };
            Grid.SetColumn(add, 2); scheduleRow.Children.Add(add);
            Grid.SetRow(scheduleRow, 1); form.Children.Add(scheduleRow);
            Grid.SetRow(form, 1); root.Children.Add(form);

            rows = new StackPanel { Margin = new Thickness(0, 7, 0, 4) };
            var scroll = new ScrollViewer
            {
                Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 2); root.Children.Add(scroll);

            var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            footer.Children.Add(new TextBlock
            {
                Text = "关闭规则后，已经生成的事项会保留", FontSize = 9.5,
                Foreground = DailyTodoWindow.SecondaryText(), VerticalAlignment = VerticalAlignment.Center
            });
            var close = DailyTodoWindow.SmallButton("完成", 68); close.Click += delegate { DialogResult = true; };
            Grid.SetColumn(close, 1); footer.Children.Add(close);
            Grid.SetRow(footer, 3); root.Children.Add(footer);

            shell.Child = root; Content = shell;
            RebuildScheduleOptions();
            RenderRules();
            input.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter) { AddRule(); args.Handled = true; }
            };
        }

        private static ComboBox DarkComboBox()
        {
            var combo = new ComboBox
            {
                Height = 30, FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5,
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(252, 27, 30, 37)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), BorderThickness = new Thickness(1),
                Padding = new Thickness(9, 2, 30, 2), VerticalContentAlignment = VerticalAlignment.Center
            };
            combo.ItemContainerStyle = DarkComboItemStyle();

            var template = new ControlTemplate(typeof(ComboBox));
            var root = new FrameworkElementFactory(typeof(Grid));

            var surface = new FrameworkElementFactory(typeof(Border));
            surface.Name = "Surface";
            surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            surface.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            surface.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            root.AppendChild(surface);

            var selected = new FrameworkElementFactory(typeof(ContentPresenter));
            selected.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("SelectionBoxItem")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            selected.SetBinding(ContentPresenter.ContentTemplateProperty, new System.Windows.Data.Binding("SelectionBoxItemTemplate")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            selected.SetValue(ContentPresenter.MarginProperty, new Thickness(10, 0, 29, 0));
            selected.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            selected.SetValue(ContentPresenter.IsHitTestVisibleProperty, false);
            root.AppendChild(selected);

            var arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.SetValue(TextBlock.TextProperty, "⌄");
            arrow.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe UI Semibold"));
            arrow.SetValue(TextBlock.FontSizeProperty, 13.0);
            arrow.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(185, 225, 228, 235)));
            arrow.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 10, 3));
            arrow.SetValue(TextBlock.IsHitTestVisibleProperty, false);
            root.AppendChild(arrow);

            var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton));
            toggle.SetValue(System.Windows.Controls.Primitives.ToggleButton.FocusableProperty, false);
            toggle.SetValue(System.Windows.Controls.Primitives.ToggleButton.BackgroundProperty, Brushes.Transparent);
            toggle.SetValue(System.Windows.Controls.Primitives.ToggleButton.BorderThicknessProperty, new Thickness(0));
            toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding("IsDropDownOpen")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
                    Mode = System.Windows.Data.BindingMode.TwoWay
                });
            var toggleTemplate = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton));
            var toggleSurface = new FrameworkElementFactory(typeof(Border));
            toggleSurface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            toggleTemplate.VisualTree = toggleSurface;
            toggle.SetValue(System.Windows.Controls.Primitives.ToggleButton.TemplateProperty, toggleTemplate);
            root.AppendChild(toggle);

            var popup = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(System.Windows.Controls.Primitives.Popup.PlacementProperty,
                System.Windows.Controls.Primitives.PlacementMode.Bottom);
            popup.SetValue(System.Windows.Controls.Primitives.Popup.AllowsTransparencyProperty, true);
            popup.SetValue(System.Windows.Controls.Primitives.Popup.PopupAnimationProperty,
                System.Windows.Controls.Primitives.PopupAnimation.Fade);
            popup.SetBinding(System.Windows.Controls.Primitives.Popup.IsOpenProperty,
                new System.Windows.Data.Binding("IsDropDownOpen")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
                    Mode = System.Windows.Data.BindingMode.TwoWay
                });
            var popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(255, 24, 27, 34)));
            popupBorder.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)));
            popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            popupBorder.SetValue(Border.PaddingProperty, new Thickness(4));
            popupBorder.SetValue(Border.MarginProperty, new Thickness(0, 4, 0, 0));
            popupBorder.SetValue(Border.EffectProperty,
                new DropShadowEffect { BlurRadius = 16, ShadowDepth = 5, Opacity = 0.32, Color = Colors.Black });
            popupBorder.SetBinding(FrameworkElement.MinWidthProperty, new System.Windows.Data.Binding("ActualWidth")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.MaxHeightProperty, 245.0);
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            var presenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            scroll.AppendChild(presenter);
            popupBorder.AppendChild(scroll);
            popup.AppendChild(popupBorder);
            root.AppendChild(popup);

            template.VisualTree = root;
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.52, "Surface"));
            template.Triggers.Add(disabled);
            combo.Template = template;
            return combo;
        }

        private static Style DarkComboItemStyle()
        {
            var style = new Style(typeof(ComboBoxItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 7, 9, 7)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            var template = new ControlTemplate(typeof(ComboBoxItem));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ItemSurface";
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.MarginProperty, new Thickness(9, 6, 9, 6));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            border.AppendChild(content);
            template.VisualTree = border;
            var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(62, 255, 255, 255)), "ItemSurface"));
            template.Triggers.Add(hover);
            var selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), "ItemSurface"));
            template.Triggers.Add(selected);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private void RebuildScheduleOptions()
        {
            if (schedule == null) return;
            schedule.Items.Clear();
            if (frequency.SelectedIndex == 0)
            {
                schedule.Items.Add(new ScheduleOption { Label = "每天自动生成", Value = 0 });
                schedule.IsEnabled = false;
            }
            else if (frequency.SelectedIndex == 1)
            {
                schedule.IsEnabled = true;
                string[] labels = { "每周一", "每周二", "每周三", "每周四", "每周五", "每周六", "每周日" };
                int[] values = { 1, 2, 3, 4, 5, 6, 0 };
                for (int index = 0; index < labels.Length; index++)
                    schedule.Items.Add(new ScheduleOption { Label = labels[index], Value = values[index] });
            }
            else
            {
                schedule.IsEnabled = true;
                for (int day = 1; day <= 31; day++)
                    schedule.Items.Add(new ScheduleOption { Label = "每月 " + day + " 日", Value = day });
            }
            schedule.SelectedIndex = 0;
        }

        private void AddRule()
        {
            string text = (input.Text ?? "").Trim();
            if (text.Length == 0) { input.Focus(); return; }
            var option = schedule.SelectedItem as ScheduleOption;
            var rule = new RecurringTodoRule
            {
                Id = Guid.NewGuid().ToString("N"), Text = text,
                Frequency = frequency.SelectedIndex == 0 ? "Daily" : frequency.SelectedIndex == 1 ? "Weekly" : "Monthly",
                ScheduleValue = option == null ? 0 : option.Value,
                Enabled = true, CreatedDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
            rules.Add(rule);
            applyTodayRuleIds.Add(rule.Id);
            Changed = true;
            input.Clear();
            RenderRules();
            input.Focus();
        }

        private void RenderRules()
        {
            rows.Children.Clear();
            if (rules.Count == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "还没有周期性任务", Margin = new Thickness(8, 18, 0, 0),
                    FontSize = 11, Foreground = DailyTodoWindow.SecondaryText()
                });
                return;
            }
            foreach (RecurringTodoRule source in rules)
            {
                RecurringTodoRule rule = source;
                var card = new Border
                {
                    CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(100, 29, 32, 40)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1),
                    Padding = new Thickness(9, 7, 7, 7), Margin = new Thickness(0, 0, 0, 7)
                };
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(29) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                var enabled = new CheckBox { IsChecked = rule.Enabled, VerticalAlignment = VerticalAlignment.Center };
                enabled.Checked += delegate
                {
                    rule.Enabled = true;
                    applyTodayRuleIds.Add(rule.Id);
                    Changed = true;
                };
                enabled.Unchecked += delegate { rule.Enabled = false; Changed = true; };
                row.Children.Add(enabled);
                var ruleText = DailyTodoWindow.InputBox("修改固定事项文字");
                ruleText.Text = rule.Text ?? ""; ruleText.Height = 30; ruleText.Padding = new Thickness(7, 3, 7, 3);
                ruleText.TextChanged += delegate
                {
                    rule.Text = ruleText.Text.Trim();
                    applyTodayRuleIds.Add(rule.Id);
                    Changed = true;
                };
                Grid.SetColumn(ruleText, 1); row.Children.Add(ruleText);
                var description = new TextBlock
                {
                    Text = DescribeRule(rule), FontSize = 9.5, Foreground = DailyTodoWindow.SecondaryText(),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(description, 2); row.Children.Add(description);
                var delete = DailyTodoWindow.SmallButton("×", 25); delete.Height = 25;
                delete.Click += delegate { rules.Remove(rule); Changed = true; RenderRules(); };
                Grid.SetColumn(delete, 3); row.Children.Add(delete);
                card.Child = row; rows.Children.Add(card);
            }
        }

        private static string DescribeRule(RecurringTodoRule rule)
        {
            if (rule.Frequency == "Daily") return "每天";
            if (rule.Frequency == "Weekly")
            {
                string[] names = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };
                int value = Math.Max(0, Math.Min(6, rule.ScheduleValue));
                return "每" + names[value];
            }
            return "每月 " + Math.Max(1, Math.Min(31, rule.ScheduleValue)) + " 日";
        }
    }

    internal sealed class DateChooserWindow : Window
    {
        public DateTime? SelectedDate { get; private set; }

        public DateChooserWindow(DateTime selected)
        {
            Width = 306; Height = 268; AllowsTransparency = true; Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape) { Close(); args.Handled = true; }
            };
            var shell = new Border
            {
                CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(245, 17, 20, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), BorderThickness = new Thickness(1),
                Padding = new Thickness(7), Effect = new DropShadowEffect { BlurRadius = 14, Opacity = 0.2 }
            };
            var calendar = new System.Windows.Controls.Calendar
            {
                SelectedDate = selected, DisplayDate = selected, SelectionMode = CalendarSelectionMode.SingleDate,
                Background = Brushes.Transparent, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                LayoutTransform = new ScaleTransform(1.4, 1.4)
            };
            calendar.SelectedDatesChanged += delegate
            {
                if (!calendar.SelectedDate.HasValue) return;
                SelectedDate = calendar.SelectedDate.Value;
                Close();
            };
            shell.Child = calendar; Content = shell;
            Deactivated += delegate { if (IsVisible && !SelectedDate.HasValue) Close(); };
        }
    }

    internal enum ImportAction
    {
        Ignore,
        AddToToday,
        AddToBacklog
    }

    internal sealed class ImportWindow : Window
    {
        private readonly List<DailyTodoItem> candidates;
        private readonly List<CheckBox> checks = new List<CheckBox>();
        public List<DailyTodoItem> SelectedItems { get; private set; }
        public ImportAction SelectedAction { get; private set; }

        public ImportWindow(List<DailyTodoItem> candidates)
        {
            this.candidates = candidates;
            Width = 400; Height = Math.Min(480, 185 + candidates.Count * 43);
            AllowsTransparency = true; Background = Brushes.Transparent; WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var shell = new Border
            {
                CornerRadius = new CornerRadius(15), Background = new SolidColorBrush(Color.FromArgb(246, 17, 20, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), BorderThickness = new Thickness(1),
                Padding = new Thickness(17), Effect = new DropShadowEffect { BlurRadius = 16, Opacity = 0.22 }
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(39) });
            var title = new StackPanel();
            title.Children.Add(new TextBlock { Text = "处理昨日未完成事项", FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 14, Foreground = Brushes.White });
            title.Children.Add(new TextBlock { Text = "勾选事项，再选择加入今日、堆积或忽略", FontFamily = new FontFamily("Segoe UI"),
                FontSize = 9.5, Foreground = DailyTodoWindow.SecondaryText(), Margin = new Thickness(0, 3, 0, 0) });
            root.Children.Add(title);
            var rows = new StackPanel();
            foreach (DailyTodoItem item in candidates)
            {
                var check = new CheckBox
                {
                    Content = item.Text, IsChecked = true, Height = 38, FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11.5, Foreground = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center
                };
                checks.Add(check); rows.Children.Add(check);
            }
            var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            var buttons = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            var selectAll = DailyTodoWindow.SmallButton("全选", 58); selectAll.HorizontalAlignment = HorizontalAlignment.Left;
            selectAll.Click += delegate { foreach (CheckBox check in checks) check.IsChecked = true; };
            buttons.Children.Add(selectAll);
            var ignore = DailyTodoWindow.SmallButton("忽略", 62);
            ignore.Click += delegate { Complete(ImportAction.Ignore); };
            Grid.SetColumn(ignore, 1); buttons.Children.Add(ignore);
            var backlog = DailyTodoWindow.SmallButton("堆积", 78);
            backlog.Click += delegate { Complete(ImportAction.AddToBacklog); };
            Grid.SetColumn(backlog, 2); buttons.Children.Add(backlog);
            var import = DailyTodoWindow.SmallButton("加入今日", 68);
            import.Click += delegate { Complete(ImportAction.AddToToday); };
            Grid.SetColumn(import, 3); buttons.Children.Add(import);
            Grid.SetRow(buttons, 2); root.Children.Add(buttons);
            shell.Child = root; Content = shell;
        }

        private void Complete(ImportAction action)
        {
            SelectedItems = new List<DailyTodoItem>();
            for (int index = 0; index < checks.Count; index++)
                if (checks[index].IsChecked == true) SelectedItems.Add(candidates[index]);
            SelectedAction = action;
            DialogResult = true;
        }
    }
}
