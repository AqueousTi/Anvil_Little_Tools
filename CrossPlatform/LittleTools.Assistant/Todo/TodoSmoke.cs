using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Deterministic visual check for the todo module, mirroring the assistant's
/// existing render and layout smoke hooks. It drives the real windows with a
/// throwaway data directory and writes one PNG per screen, so the Linux UI can be
/// reviewed without a human at the keyboard.
/// </summary>
internal static class TodoSmoke
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var dataDirectory = Path.Combine(directory, "data");
        Directory.CreateDirectory(dataDirectory);

        var clock = new SystemTodoClock();
        var ids = new GuidTodoIdGenerator();
        var store = new TodoStore(dataDirectory);
        var data = BuildSample(clock, ids);
        store.Save(data);

        var window = new TodoWindow(store, data, clock, ids, new NullTodoSoundService(), edgeHideEnabled: false);
        window.Show();
        await Settle();
        Save(window, Path.Combine(directory, "todo-compact.png"));

        // The completion feedback is transient: the tick and the strike are visible
        // before the 140ms delay ends, so it is captured mid animation.
        window.BeginCompactCompletionForSmoke();
        await Task.Delay(90);
        Save(window, Path.Combine(directory, "todo-compact-completing.png"));
        await Task.Delay(900);

        window.ShowToday();
        await Settle();
        Save(window, Path.Combine(directory, "todo-expanded.png"));

        if (window.BacklogTabForSmoke is { } tab)
        {
            await Settle();
            Save(tab, Path.Combine(directory, "todo-tab.png"));
        }

        window.ToggleBacklog();
        await Settle();
        Save(window, Path.Combine(directory, "todo-backlog.png"));
        window.ToggleBacklog();
        await Settle();

        await SaveDialog(new FocusDialWindow("写周报并同步给团队", 45, false, new NullTodoSoundService()),
            Path.Combine(directory, "todo-dial.png"));
        // A running countdown renders the blue ring and the mm:ss value.
        await SaveDialog(new FocusDialWindow("写周报并同步给团队", 25, true, new NullTodoSoundService(),
                () => 18 * 60 + 42, _ => { }, () => { }),
            Path.Combine(directory, "todo-dial-counting.png"));
        await SaveDialog(new RecurringRulesWindow(data.RecurringRules, clock, ids),
            Path.Combine(directory, "todo-rules.png"));
        await SaveDialog(new DateChooserWindow(clock.Today), Path.Combine(directory, "todo-date.png"));
        await SaveDialog(new ImportWindow(TodoLogic.ImportCandidates(data, clock.Today)),
            Path.Combine(directory, "todo-import.png"));

        // With a countdown running the entry ring turns blue and shows progress.
        var current = TodoLogic.CurrentTodayItem(data, clock.Today);
        if (current is not null)
        {
            data.FocusTimer = FocusTimerMath.Create(current.Id!, current.Text ?? string.Empty, 25, clock);
            window.ShowToday();
            await Settle();
            Save(window, Path.Combine(directory, "todo-expanded-focus.png"));
        }

        window.HideWindow();
        window.Close();
    }

    private static async Task SaveDialog(Window dialog, string path)
    {
        dialog.Show();
        await Settle();
        Save(dialog, path);
        dialog.Close();
        await Settle();
    }

    private static async Task Settle()
    {
        await Task.Delay(350);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(150);
    }

    private static void Save(Window window, string path)
    {
        var scale = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    /// <summary>A fixed sample set so repeated runs produce comparable images.</summary>
    private static DailyTodoData BuildSample(ITodoClock clock, ITodoIdGenerator ids)
    {
        var data = new DailyTodoData();
        var today = clock.Today;

        var day = TodoLogic.FindDay(data, today, true)!;
        foreach (var text in new[] { "写周报并同步给团队", "晨会 09:30", "回复客户邮件" })
            TodoLogic.AddItem(day, text, clock, ids);
        var done = TodoLogic.AddItem(day, "提交报销单", clock, ids);
        TodoLogic.Complete(day, done, data);

        data.RecurringRules.Add(new RecurringTodoRule
        {
            Id = "rule-daily", Text = "晨会 09:30", Frequency = "Daily", ScheduleValue = 0,
            Enabled = true, CreatedDate = "2026-01-05"
        });
        data.RecurringRules.Add(new RecurringTodoRule
        {
            Id = "rule-weekly", Text = "周报", Frequency = "Weekly", ScheduleValue = 5,
            Enabled = true, CreatedDate = "2026-01-05"
        });

        var yesterday = TodoLogic.FindDay(data, today.AddDays(-1), true)!;
        TodoLogic.AddItem(yesterday, "整理会议纪要", clock, ids);
        TodoLogic.AddItem(yesterday, "回复供应商报价", clock, ids);

        foreach (var text in new[] { "调研新的行情数据源", "整理上季度的发票" })
        {
            data.BacklogItems.Add(new DailyTodoItem
            {
                Id = ids.NewId(),
                Text = text,
                CreatedAt = clock.Now,
                BacklogSourceDate = TodoLogic.DateKey(today.AddDays(-1))
            });
        }
        return data;
    }
}
