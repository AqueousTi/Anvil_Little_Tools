using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LittleTools.DailyTodo;

internal static class TodoFocusSmoke
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("TestChina", TimeSpan.FromHours(8), "TestChina", "TestChina");
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }
    private static void Near(double actual, double expected, string message)
    {
        Check(Math.Abs(actual - expected) < 0.01, message + ": " + actual + " != " + expected);
    }
    private static DateTime Utc(string local)
    {
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(DateTime.Parse(local), DateTimeKind.Unspecified), Zone);
    }
    private static DailyTodoData Data(out DailyTodoItem item)
    {
        item = new DailyTodoItem { Id = Guid.NewGuid().ToString("N"), Text = "阅读半小时", CreatedAt = DateTime.Now };
        var day = new TodoDay { Date = "2026-10-19" };
        day.Items.Add(item);
        var data = new DailyTodoData(); data.Days.Add(day);
        return data;
    }
    private static double DaySeconds(DailyTodoData data, string date)
    {
        TodoDay day = data.Days.Find(delegate(TodoDay value) { return value.Date == date; });
        return day == null ? 0 : day.FocusSeconds;
    }
    private static T RoundTrip<T>(T value)
    {
        var serializer = new JavaScriptSerializer();
        return serializer.Deserialize<T>(serializer.Serialize(value));
    }

    private static void TestFocus()
    {
        DailyTodoItem item;
        DailyTodoData data = Data(out item);
        DateTime start = Utc("2026-10-19 09:00");
        data.FocusTimer = FocusTracking.Start(item, 25, start);
        Near(DaySeconds(data, "2026-10-19"), 0, "Starting must not credit pending time");
        Near(FocusTracking.Finish(data, start.AddMinutes(8).AddSeconds(37), Zone), 517, "Interrupted actual time");
        Near(item.FocusSeconds, 517, "Per-item interrupted time");
        Near(DaySeconds(data, "2026-10-19"), 517, "Daily interrupted time");
        Near(FocusTracking.Finish(data, start.AddHours(1), Zone), 0, "Repeated finish must be idempotent");
        data.FocusTimer = FocusTracking.Start(item, 25, start.AddHours(1));
        FocusTracking.Finish(data, start.AddHours(3), Zone);
        Near(item.FocusSeconds, 2017, "Completion caps at deadline and accumulates sessions");
        Near(DaySeconds(data, "2026-10-19"), 2017, "Daily totals accumulate");

        data = Data(out item);
        start = Utc("2026-10-19 23:50");
        data.FocusTimer = FocusTracking.Start(item, 25, start);
        FocusTracking.Finish(data, start.AddMinutes(25), Zone);
        Near(DaySeconds(data, "2026-10-19"), 600, "Midnight previous day");
        Near(DaySeconds(data, "2026-10-20"), 900, "Midnight next day");
        Near(item.FocusSeconds, 1500, "Cross-day item total");

        data = Data(out item);
        data.FocusTimer = FocusTracking.Start(item, 25, start);
        FocusTracking.TogglePause(data.FocusTimer, start.AddMinutes(5));
        Near(FocusTracking.Remaining(data.FocusTimer, start.AddHours(2)), 1200, "Pause freezes countdown");
        Near(DaySeconds(data, "2026-10-19"), 0, "Pause does not settle totals");
        data = RoundTrip(data);
        item = data.Days[0].Items[0];
        Check(data.FocusTimer.Paused, "Pause survives restart");
        FocusTracking.TogglePause(data.FocusTimer, start.AddHours(2));
        FocusTracking.TogglePause(data.FocusTimer, start.AddHours(2).AddMinutes(10));
        Near(FocusTracking.Finish(data, start.AddHours(5), Zone), 900, "End while paused excludes pause gaps");
        Near(item.FocusSeconds, 900, "Multiple intervals item total");
        Near(DaySeconds(data, "2026-10-19"), 300, "Paused before midnight total");
        Near(DaySeconds(data, "2026-10-20"), 600, "Resumed after midnight total");

        data = Data(out item);
        data.FocusTimer = FocusTracking.Start(item, 25, start);
        FocusTracking.TogglePause(data.FocusTimer, start.AddMinutes(5));
        FocusTracking.TogglePause(data.FocusTimer, start.AddMinutes(15));
        FocusTracking.Finish(data, start.AddHours(2), Zone);
        Near(item.FocusSeconds, 1500, "Natural completion after resume");
        Near(DaySeconds(data, "2026-10-19"), 300, "Before-midnight active span");
        Near(DaySeconds(data, "2026-10-20"), 1200, "After-midnight active span");

        data = Data(out item);
        data.FocusTimer = new FocusTimerData { ItemId = item.Id, DurationMinutes = 25, EndsAtUtc = start.AddMinutes(25).ToString("o") };
        Check(FocusTracking.Normalize(data.FocusTimer), "Legacy countdown migration");
        FocusTracking.Finish(data, start.AddHours(2), Zone);
        Near(item.FocusSeconds, 1500, "Expired legacy timer credited on recovery");
        data = RoundTrip(data);
        Near(data.Days[0].Items[0].FocusSeconds, 1500, "Item statistics survive restart");
        Near(DaySeconds(data, "2026-10-20"), 900, "Daily statistics survive restart");
        Check(FocusTracking.Describe(0) == "0 分钟", "Zero display");
        Check(FocusTracking.Describe(30) == "不足 1 分钟", "Short session display");
        Check(FocusTracking.Describe(5100) == "1 小时 25 分钟", "Hours display");
    }

    private static void TestOrdering()
    {
        DailyTodoItem normal;
        DailyTodoData data = Data(out normal);
        TodoDay day = data.Days[0];
        data.RecurringRules.Add(new RecurringTodoRule { Id = "bedtime", Text = "睡前吃药", Frequency = "Daily", Placement = 1, CreatedDate = day.Date });
        data.RecurringRules.Add(new RecurringTodoRule { Id = "morning", Text = "晨间计划", Frequency = "Daily", Placement = -1, CreatedDate = day.Date });
        day.Items.Add(new DailyTodoItem { Id = "done", Text = "已完成", Completed = true });
        Check(RecurringTodoEngine.Ensure(data, DateTime.Parse(day.Date)), "Generate ranked recurring tasks");
        Check(day.Items[0].RecurringRuleId == "morning" && day.Items[1] == normal && day.Items[2].RecurringRuleId == "bedtime" && day.Items[3].Completed, "Top normal bottom completed ordering");
        Check(!RecurringTodoEngine.Ensure(data, DateTime.Parse(day.Date)), "Ensure remains idempotent");
        DailyTodoItem last = day.Items[2];
        last.Completed = true;
        day.Items.Remove(last); day.Items.Add(last);
        RecurringTodoEngine.NormalizeOrder(data, day);
        Check(day.Items[1] == normal && day.Items[3] == last, "Bottom task can complete while other tasks remain");
        last.Completed = false;
        RecurringTodoEngine.NormalizeOrder(data, day);
        Check(day.Items[2] == last, "Reopen returns to its tier");
        var normal2 = new DailyTodoItem { Id = "normal2", Text = "新增事项" };
        day.Items.Insert(3, normal2);
        RecurringTodoEngine.NormalizeOrder(data, day);
        Check(day.Items[1] == normal && day.Items[2] == normal2 && day.Items[3] == last, "New ordinary task stays before bottom");
        day.Items.Remove(normal2); day.Items.Insert(1, normal2);
        RecurringTodoEngine.NormalizeOrder(data, day);
        Check(day.Items[1] == normal2 && day.Items[2] == normal, "Manual order within tier remains stable");
        data.RecurringRules[0].Placement = -1;
        RecurringTodoEngine.NormalizeOrder(data, day);
        Check(day.Items[1] == last, "Changing placement reorders existing task");
        data = RoundTrip(data);
        RecurringTodoEngine.Ensure(data, DateTime.Parse("2026-10-20"));
        Check(data.Days[1].Items.All(delegate(DailyTodoItem value) { return RecurringTodoEngine.PlacementOf(data, value) == -1; }), "Placement persists for next day");
        Check(new JavaScriptSerializer().Deserialize<RecurringTodoRule>("{}").Placement == 0, "Old rules default to ordinary");
    }

    private static object Call(object target, string method, params object[] args)
    {
        return target.GetType().GetMethods(Instance).Single(delegate(MethodInfo value)
            { return value.Name == method && value.GetParameters().Length == args.Length; }).Invoke(target, args);
    }
    private static void Snapshot(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) png.Save(stream);
    }
    private static T Field<T>(object value, string name)
    {
        return (T)value.GetType().GetField(name, Instance).GetValue(value);
    }
    private static void TestWindows(string output)
    {
        var store = new TodoStore();
        string folder = Path.Combine(output, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        typeof(TodoStore).GetField("dataPath", Instance).SetValue(store, Path.Combine(folder, "data.json"));
        typeof(TodoStore).GetField("backupPath", Instance).SetValue(store, Path.Combine(folder, "backup.json"));
        DailyTodoItem item;
        DailyTodoData saved = Data(out item);
        saved.Days[0].Date = DateTime.Today.ToString("yyyy-MM-dd");
        saved.LastImportPromptDate = saved.Days[0].Date;
        saved.Days[0].FocusSeconds = 5100; item.FocusSeconds = 2100;
        saved.Days[0].Items.Add(new DailyTodoItem { Id = "no-focus", Text = "整理桌面" });
        saved.RecurringRules.Add(new RecurringTodoRule { Id = "medicine", Text = "睡前吃药", Frequency = "Daily", Placement = 1, CreatedDate = saved.Days[0].Date });
        saved.RecurringRules.Add(new RecurringTodoRule { Id = "plan", Text = "晨间计划", Frequency = "Daily", Placement = -1, CreatedDate = saved.Days[0].Date });
        store.Save(saved);
        var window = new DailyTodoWindow(store);
        try
        {
            Call(window, "RenderAll");
            Check(Field<TextBlock>(window, "compactFocusTotal").Text == "今日已专注 1 小时 25 分钟", "Compact total text");
            Snapshot(window, Path.Combine(output, "compact.png"));
            window.GetType().GetField("expanded", Instance).SetValue(window, true);
            Field<Grid>(window, "compactView").Visibility = Visibility.Collapsed;
            Field<Grid>(window, "expandedView").Visibility = Visibility.Visible;
            window.Width = 430; window.Height = 530;
            Call(window, "RenderAll");
            Call(window, "RenderExpanded", false);
            Snapshot(window, Path.Combine(output, "expanded.png"));
            Check(Field<TextBlock>(window, "focusTotalText").Text == "今日已专注 1 小时 25 分钟", "Expanded total text");
            var rules = new RecurringRulesWindow(saved.RecurringRules);
            try { Snapshot(rules, Path.Combine(output, "recurring.png")); } finally { rules.Close(); }
            bool paused = false;
            var focus = new FocusTimerWindow("阅读半小时", 25, true, false, delegate { paused = !paused; }, delegate { return 1500; }, delegate(int value) { }, delegate { });
            try
            {
                Snapshot(focus, Path.Combine(output, "focus.png"));
                var mainActions = ((StackPanel)((StackPanel)((Grid)((Border)focus.Content).Child).Children[2]).Children[0]);
                var pause = (Button)mainActions.Children[1];
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(paused && (string)pause.Content == "继续专注", "Pause button toggles");
                Snapshot(focus, Path.Combine(output, "paused.png"));
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!paused && (string)pause.Content == "暂停", "Resume button toggles");
            }
            finally { focus.Close(); }
            DailyTodoData live = Field<DailyTodoData>(window, "data");
            TodoDay today = live.Days.Find(delegate(TodoDay value) { return value.Date == DateTime.Today.ToString("yyyy-MM-dd"); });
            DailyTodoItem liveItem = today.Items.Find(delegate(DailyTodoItem value) { return value.Id == item.Id; });
            live.FocusTimer = FocusTracking.Start(liveItem, 25, DateTime.UtcNow.AddMinutes(-8));
            Call(window, "DeleteItem", today, liveItem);
            Check(today.FocusSeconds >= 5580 && today.FocusSeconds < 5581, "Deleting active item credits actual focus");
            Check(!today.Items.Contains(liveItem) && live.FocusTimer == null, "Deleting active item ends timer");
        }
        finally { window.Close(); }
        DailyTodoData restored = store.Load();
        Check(restored.Days[0].FocusSeconds >= 5580, "Totals persist after deleting an item");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            TestFocus(); TestOrdering(); TestWindows(args[0]);
            Console.WriteLine("TODO_FOCUS_SMOKE_OK checks=" + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
