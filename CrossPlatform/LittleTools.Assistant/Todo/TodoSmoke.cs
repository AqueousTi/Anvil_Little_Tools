using System.Globalization;
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

        // The capsule folds its sub item list away and remembers the choice.
        window.SetCompactSubItemsForSmoke(false);
        await Settle();
        Save(window, Path.Combine(directory, "todo-compact-folded.png"));
        window.SetCompactSubItemsForSmoke(true);
        await Settle();

        // The completion feedback is transient: the tick and the strike are visible
        // before the 140ms delay ends, so it is captured mid animation.
        window.BeginCompactCompletionForSmoke();
        await Task.Delay(90);
        Save(window, Path.Combine(directory, "todo-compact-completing.png"));
        await Task.Delay(900);

        window.ShowToday();
        await Settle();
        Save(window, Path.Combine(directory, "todo-expanded.png"));

        // A card with its sub item panel open, with and without the inline input.
        var current = TodoLogic.CurrentTodayItem(data, clock.Today);
        if (current is not null)
        {
            window.ShowSubItemsForSmoke(current.Id, false);
            await Settle();
            // The rendered deck has to match the Windows geometry, not just the
            // pure rules: the open panel grows its card and pushes the next one down.
            var day = TodoLogic.FindDay(data, clock.Today, false)!;
            CheckPanel(window, day, current);
            Save(window, Path.Combine(directory, "todo-expanded-subitems.png"));
            window.ShowSubItemsForSmoke(current.Id, true);
            await Settle();
            Save(window, Path.Combine(directory, "todo-expanded-subitem-input.png"));

            // A completed item keeps its sub items readable but has no add control,
            // so its panel is four pixels shorter than the same open item's.
            var doneOwner = day.Items.FirstOrDefault(candidate =>
                candidate.Completed && candidate.SubItems.Count > 0);
            if (doneOwner is not null)
            {
                window.ShowSubItemsForSmoke(doneOwner.Id, false);
                await Settle();
                CheckPanel(window, day, doneOwner);
                Save(window, Path.Combine(directory, "todo-expanded-subitems-done.png"));
            }
            window.ShowSubItemsForSmoke(null, false);
            await Settle();
        }

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
        var focusItem = TodoLogic.CurrentTodayItem(data, clock.Today);
        if (focusItem is not null)
        {
            data.FocusTimer = FocusTimerMath.Create(focusItem.Id!, focusItem.Text ?? string.Empty, 25, clock);
            window.ShowToday();
            await Settle();
            Save(window, Path.Combine(directory, "todo-expanded-focus.png"));
            data.FocusTimer = null;
        }

        // The gentle capsule lines: nothing open, and everything done.
        var empty = new DailyTodoData();
        await SaveCapsule(empty, clock, ids, store, Path.Combine(directory, "todo-compact-empty.png"));
        var finished = new DailyTodoData();
        var finishedDay = TodoLogic.FindDay(finished, clock.Today, true)!;
        var doneItem = TodoLogic.AddItem(finishedDay, "写周报并同步给团队", clock, ids);
        AddSubItem(doneItem, "整理要点", true, clock, ids);
        var secondItem = TodoLogic.AddItem(finishedDay, "回复客户邮件", clock, ids);
        TodoLogic.Complete(finishedDay, doneItem, finished);
        TodoLogic.Complete(finishedDay, secondItem, finished);
        var backlogItem = new DailyTodoItem
        {
            Id = ids.NewId(), Text = "调研新的行情数据源", CreatedAt = clock.Now,
            BacklogSourceDate = TodoLogic.DateKey(clock.Today.AddDays(-1))
        };
        AddSubItem(backlogItem, "对比三家数据源", false, clock, ids);
        finished.BacklogItems.Add(backlogItem);        await SaveCapsule(finished, clock, ids, store, Path.Combine(directory, "todo-compact-finished.png"));

        // A sub item list longer than the four visible rows makes the capsule
        // scroller realize its bar, which is where the ported Windows
        // MinimalScrollBarStyle has to be visible.
        var scrolling = new DailyTodoData();
        var scrollingDay = TodoLogic.FindDay(scrolling, clock.Today, true)!;
        var longItem = TodoLogic.AddItem(scrollingDay, "整理季度复盘", clock, ids);
        for (var index = 1; index <= 6; index++)
            AddSubItem(longItem, "第 " + index + " 步", index == 1, clock, ids);
        var scrollBars = await SaveCapsule(scrolling, clock, ids, store,
            Path.Combine(directory, "todo-compact-scrolling.png"));

        Console.WriteLine("todo-scrollbars: " + scrollBars);
        File.WriteAllText(Path.Combine(directory, "todo-scrollbars.txt"), scrollBars);
        if (scrollBars == "none"
            || !scrollBars.Contains("width=8", StringComparison.OrdinalIgnoreCase)
            || !scrollBars.Contains("minHeight=26", StringComparison.OrdinalIgnoreCase)
            || !scrollBars.Contains("radius=3", StringComparison.OrdinalIgnoreCase)
            || !scrollBars.Contains("margin=1,2,1,2", StringComparison.OrdinalIgnoreCase)
            || !scrollBars.Contains("#668f949e", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected scroll bar styling: " + scrollBars);

        // A deck taller than the window must realize its vertical bar in the same
        // layout pass that grows the canvas: the old code only refreshed the bar
        // after a later measure, so the overflowing deck looked unscrollable.
        await CheckOverflowingDeck(store, clock, ids, directory);

        window.HideWindow();
        window.Close();
    }

    /// <summary>
    /// Grows a deck from three to fifteen items after the window is laid out and
    /// asserts that the card scroller reports a real extent and realizes its bar
    /// without any further interaction. This is the "items went past the window"
    /// path: the bar has to be there and be full sized as soon as the deck grows.
    /// </summary>
    private static async Task CheckOverflowingDeck(TodoStore store, ITodoClock clock, ITodoIdGenerator ids,
        string directory)
    {
        var data = new DailyTodoData();
        var day = TodoLogic.FindDay(data, clock.Today, true)!;
        for (var index = 1; index <= 3; index++)
            TodoLogic.AddItem(day, "待办事项 " + index, clock, ids);
        // Enough backlog rows that the drawer scroller realizes its own bar too.
        for (var index = 1; index <= 10; index++)
            data.BacklogItems.Add(new DailyTodoItem
            {
                Id = ids.NewId(), Text = "堆积事项 " + index, CreatedAt = clock.Now,
                BacklogSourceDate = TodoLogic.DateKey(clock.Today.AddDays(-1))
            });

        var deck = new TodoWindow(store, data, clock, ids, new NullTodoSoundService(), edgeHideEnabled: false);
        deck.Show();
        deck.ShowToday();
        await Settle();
        var before = deck.DescribeCardScrollForSmoke();
        if (Field(before, "extent") > Field(before, "viewport"))
            throw new InvalidOperationException("Short deck already overflows: " + before);
        if (before.Contains("barVisible=True", StringComparison.Ordinal))
            throw new InvalidOperationException("Short deck shows a bar: " + before);

        // No clicks and no resizes from here on: the render itself has to realize it.
        deck.GrowDeckForSmoke(12, "新增事项 ");
        await Settle();
        Save(deck, Path.Combine(directory, "todo-expanded-overflow.png"));
        var scroll = deck.DescribeCardScrollForSmoke();
        Console.WriteLine("todo-card-scroll: " + scroll);
        File.WriteAllText(Path.Combine(directory, "todo-card-scroll.txt"), scroll);

        if (Field(scroll, "extent") <= Field(scroll, "viewport"))
            throw new InvalidOperationException("Overflowing deck did not grow: " + scroll);
        if (!scroll.Contains("barVisible=True", StringComparison.Ordinal)
            || !scroll.Contains("thumbVisible=True", StringComparison.Ordinal))
            throw new InvalidOperationException("Card scroll bar missing on overflow: " + scroll);
        // The Fluent template renders the idle thumb as a scaled hairline; the
        // Windows bar has to stay a full sized 8px grip.
        if (scroll.Contains("thumbTransform=none", StringComparison.Ordinal) is false)
            throw new InvalidOperationException("Card scroll thumb is scaled down: " + scroll);
        if (Field(scroll, "thumbMinHeight") != 26 || Field(scroll, "thumbWidth") != 8)
            throw new InvalidOperationException("Card scroll thumb is not the Windows size: " + scroll);

        // The Windows template keeps invisible paging arrows; the transparent
        // track still has to page when it is clicked.
        var beforePage = Field(scroll, "offset");
        deck.PageCardScrollForSmoke(down: true);
        await Settle();
        var paged = deck.DescribeCardScrollForSmoke();
        Console.WriteLine("todo-card-paged: " + paged);
        if (Field(paged, "offset") <= beforePage)
            throw new InvalidOperationException("Card scroll paging did not move the deck: " + paged);
        deck.PageCardScrollForSmoke(down: false);
        await Settle();
        if (Field(deck.DescribeCardScrollForSmoke(), "offset") != beforePage)
            throw new InvalidOperationException("Card scroll paging back did not restore the top");

        // The drawer replaces the deck, so this is the third scroller's own bar.
        deck.ToggleBacklog();
        await Settle();
        Save(deck, Path.Combine(directory, "todo-expanded-backlog-overflow.png"));
        var drawer = deck.DescribeScrollBarsForSmoke();
        Console.WriteLine("todo-drawer-scrollbars: " + drawer);
        File.WriteAllText(Path.Combine(directory, "todo-drawer-scrollbars.txt"), drawer);
        if (!drawer.Contains("width=8", StringComparison.OrdinalIgnoreCase)
            || !drawer.Contains("minHeight=26", StringComparison.OrdinalIgnoreCase)
            || !drawer.Contains("radius=3", StringComparison.OrdinalIgnoreCase)
            || !drawer.Contains("margin=1,2,1,2", StringComparison.OrdinalIgnoreCase)
            || !drawer.Contains("#668f949e", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected drawer scroll bar styling: " + drawer);
        deck.ToggleBacklog();
        await Settle();

        deck.HideWindow();
        deck.Close();
        await Settle();
    }

    private static double Field(string report, string name)
    {
        foreach (var part in report.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith(name + "=", StringComparison.Ordinal)
                && double.TryParse(trimmed[(name.Length + 1)..], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return double.NaN;
    }

    /// <summary>
    /// Fails the render run when the deck does not match the Windows geometry for
    /// the open panel: the owner card height and the top of the card below it.
    /// </summary>
    private static void CheckPanel(TodoWindow window, TodoDay day, DailyTodoItem owner)
    {
        var ownerIndex = day.Items.IndexOf(owner);
        var panel = window.DescribeOpenPanelForSmoke();
        var expected = "ownerHeight="
            + TodoLogic.CardHeightAt(owner, owner.Id, null).ToString(CultureInfo.InvariantCulture)
            + ",nextTop="
            + TodoLogic.CardTopAt(ownerIndex + 1, day.Items, owner.Id, null)
                .ToString(CultureInfo.InvariantCulture);
        Console.WriteLine("todo-panel: " + panel);
        if (panel != expected)
            throw new InvalidOperationException(
                "Unexpected card geometry: " + panel + " expected " + expected);
    }

    /// <summary>Renders only the capsule for a throwaway data set and reports its scroll bars.</summary>
    private static async Task<string> SaveCapsule(DailyTodoData data, ITodoClock clock, ITodoIdGenerator ids,
        TodoStore store, string path)
    {
        var capsule = new TodoWindow(store, data, clock, ids, new NullTodoSoundService(), edgeHideEnabled: false);
        capsule.Show();
        await Settle();
        Save(capsule, path);
        var scrollBars = capsule.DescribeScrollBarsForSmoke();
        capsule.HideWindow();
        capsule.Close();
        await Settle();
        return scrollBars;
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

        // Sub items on every card, including the completed one, so each render
        // exercises the panel, the progress text and the tick states.
        AddSubItem(day.Items[0], "整理本周要点", false, clock, ids);
        AddSubItem(day.Items[0], "同步给团队", true, clock, ids);
        AddSubItem(day.Items[0], "归档到知识库", false, clock, ids);
        AddSubItem(day.Items[1], "确认会议室", false, clock, ids);
        AddSubItem(day.Items[1], "准备议程", false, clock, ids);
        AddSubItem(day.Items[2], "整理回复要点", true, clock, ids);
        AddSubItem(day.Items[3], "填写交通费", true, clock, ids);
        AddSubItem(day.Items[3], "上传发票", true, clock, ids);

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
        AddSubItem(data.BacklogItems[0], "对比三家数据源", false, clock, ids);
        AddSubItem(data.BacklogItems[0], "联系销售", true, clock, ids);
        return data;
    }

    private static void AddSubItem(DailyTodoItem owner, string text, bool completed,
        ITodoClock clock, ITodoIdGenerator ids) =>
        owner.SubItems.Add(new TodoSubItem
        {
            Id = ids.NewId(), Text = text, Completed = completed, CreatedAt = clock.Now
        });
}
