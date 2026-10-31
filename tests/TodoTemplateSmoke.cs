using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LittleTools.DailyTodo;

internal static class TodoTemplateSmoke
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        checks++;
    }
    private static T Field<T>(object target, string name)
    {
        return (T)target.GetType().GetField(name, Instance).GetValue(target);
    }
    private static object Call(object target, string name, params object[] args)
    {
        return target.GetType().GetMethods(Instance).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);
    }
    private static void Snapshot(FrameworkElement element, string path, double width, double height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var image = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
    private static TodoStore Store(string output)
    {
        var store = new TodoStore();
        string folder = Path.Combine(output, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        typeof(TodoStore).GetField("dataPath", Instance).SetValue(store, Path.Combine(folder, "data.json"));
        typeof(TodoStore).GetField("backupPath", Instance).SetValue(store, Path.Combine(folder, "backup.json"));
        return store;
    }
    private static TodoTemplate TestCopies()
    {
        var original = new DailyTodoItem { Id = "original", Text = "出差准备", Description = "提前核对证件", Completed = true,
            FocusSeconds = 1800, SourceId = "old-source", RecurringRuleId = "old-rule", BacklogSourceDate = "2026-01-01" };
        original.SubItems.Add(new DailyTodoSubItem { Id = "old-sub", Text = "订票", Completed = true });
        original.SubItems.Add(new DailyTodoSubItem { Id = "old-sub-2", Text = "整理行李", Completed = true });
        TodoTemplate template = TodoTemplateEngine.FromItem(original);
        DailyTodoItem first = TodoTemplateEngine.Instantiate(template), second = TodoTemplateEngine.Instantiate(template);
        Check(first.Id != second.Id && first.Id != original.Id, "Every use has a new item ID");
        Check(!first.Completed && first.SubItems.All(sub => !sub.Completed), "All completion states reset");
        Check(first.FocusSeconds == 0 && first.SourceId == null && first.RecurringRuleId == null && first.BacklogSourceDate == null,
            "Focus, recurring, import and backlog state must not carry over");
        Check(first.SubItems[0].Id != second.SubItems[0].Id && first.SubItems[0].Id != "old-sub", "Subitem IDs are unique");
        Check(first.Description == original.Description && first.SubItems[1].Text == "整理行李", "Content and order preserved");
        first.Text = "本次出差"; first.SubItems[0].Text = "已改签"; first.SubItems[0].Completed = true;
        template.SubItems[1] = "带上电脑";
        Check(second.Text == "出差准备" && second.SubItems[0].Text == "订票" && !second.SubItems[0].Completed,
            "Instances are independent");
        Check(first.SubItems[1].Text == "整理行李" && original.SubItems[1].Text == "整理行李", "Template edits do not modify existing items");
        return template;
    }
    private static void TestStore(string output, TodoTemplate template)
    {
        TodoStore store = Store(output);
        File.WriteAllText(Field<string>(store, "dataPath"), "{\"Days\":[{\"Date\":\"2026-01-01\",\"Items\":[{\"Id\":\"old\",\"Text\":\"已有待办\"}]}],\"Templates\":null}");
        DailyTodoData data = store.Load();
        Check(data.Templates != null && data.Templates.Count == 0 && data.Days[0].Items[0].Text == "已有待办", "Old data remains compatible");
        data.Templates.Add(template); store.Save(data);
        DailyTodoData restored = store.Load();
        Check(restored.Templates[0].SubItems[1] == "带上电脑" && restored.Templates[0].Description == "提前核对证件", "Templates persist");
        store.Save(restored); File.WriteAllText(Field<string>(store, "dataPath"), "broken json");
        Check(store.Load().Templates.Count == 1, "Backup recovers templates");
    }
    private static void TestEditor(string output, TodoTemplate template)
    {
        var cancel = new TodoTemplateEditorWindow(template);
        Field<TextBox>(cancel, "subItemsInput").Text = "不保存的修改";
        cancel.Close();
        Check(template.SubItems.Count == 2 && cancel.Result == null, "Cancel leaves original template untouched");
        var editor = new TodoTemplateEditorWindow(null);
        Exception failure = null;
        editor.Dispatcher.BeginInvoke(new Action(delegate
        {
            try
            {
                Call(editor, "SaveTemplate");
                Check(editor.Result == null && editor.IsVisible, "Blank titles are rejected");
                Field<TextBox>(editor, "titleInput").Text = "  出差准备  ";
                Field<TextBox>(editor, "descriptionInput").Text = "提前核对证件";
                Field<TextBox>(editor, "subItemsInput").Text = "  订票  \r\n\r\n  \n订酒店\n整理行李";
                Snapshot((FrameworkElement)editor.Content, Path.Combine(output, "editor.png"), editor.Width, editor.Height);
                Call(editor, "SaveTemplate");
            }
            catch (Exception error) { failure = error; editor.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        Check(editor.ShowDialog() == true, "Editor saves");
        if (failure != null) throw failure;
        Check(editor.Result.Name == "出差准备" && editor.Result.Text == "出差准备" && editor.Result.SubItems.Count == 3,
            "Name defaults to title, multiline children trim and omit blank lines");
        var manager = new TodoTemplateManagerWindow(new System.Collections.Generic.List<TodoTemplate> { editor.Result }, delegate { });
        try { Snapshot((FrameworkElement)manager.Content, Path.Combine(output, "manager.png"), manager.Width, manager.Height); }
        finally { manager.Close(); }
    }
    private static void TestWindow(string output, TodoTemplate template)
    {
        TodoStore store = Store(output);
        var data = new DailyTodoData { LastImportPromptDate = DateTime.Today.ToString("yyyy-MM-dd") };
        data.Templates.Add(template); store.Save(data);
        var window = new DailyTodoWindow(store);
        try
        {
            window.Width = 430; window.Height = 530;
            typeof(DailyTodoWindow).GetField("expanded", Instance).SetValue(window, true);
            Field<Grid>(window, "compactView").Visibility = Visibility.Collapsed;
            Field<Grid>(window, "expandedView").Visibility = Visibility.Visible;
            Call(window, "RenderAll");
            var input = Field<TextBox>(window, "addInput");
            var row = (Grid)input.Parent;
            var add = row.Children.OfType<Button>().Single();
            Check((string)add.ToolTip == "选择预制事项", "Empty add button explains template action");
            input.Text = "普通事项";
            Check((string)add.ToolTip == "添加事项", "Typed input retains add action");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(store.Load().Days[0].Items[0].Text == "普通事项" && input.Text == "", "Ordinary add still works");
            input.Text = "   ";
            Check((string)add.ToolTip == "选择预制事项", "Whitespace counts as empty");
            var menu = (ContextMenu)Call(window, "BuildTemplateMenu");
            Snapshot(menu, Path.Combine(output, "menu.png"), 310, 220);
            ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var live = Field<DailyTodoData>(window, "data");
            Check(live.Days[0].Items.Count == 2 && live.Days[0].Items[1].SubItems.Count == 2, "Menu click inserts entire template directly");
            Check(Field<string>(window, "expandedSubItemOwnerId") == live.Days[0].Items[1].Id, "Inserted children are visible");
            Call(window, "RenderExpanded", false);
            Snapshot((FrameworkElement)window.Content, Path.Combine(output, "inserted.png"), window.Width, window.Height);
            Call(window, "ShowDate", DateTime.Today.AddDays(3));
            Call(window, "AddTemplate", template);
            Check(live.Days.Single(day => day.Date == DateTime.Today.AddDays(3).ToString("yyyy-MM-dd")).Items.Count == 1,
                "Template follows currently viewed day");
            Call(window, "ShowDate", DateTime.Today);
            Call(window, "RenderExpanded", false);
            Snapshot((FrameworkElement)window.Content, Path.Combine(output, "expanded.png"), window.Width, window.Height);
        }
        finally { window.Close(); }
        Check(store.Load().Templates.Count == 1 && store.Load().Days[0].Items.Count == 2, "Inserted tasks and template survive restart");
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            TodoTemplate template = TestCopies();
            TestStore(args[0], template); TestEditor(args[0], template); TestWindow(args[0], template);
            Console.WriteLine("TODO_TEMPLATE_SMOKE_OK checks=" + checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
