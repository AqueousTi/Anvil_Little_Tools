using System.Globalization;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// All todo rules, ported from the Windows module. Every method is pure with
/// respect to the clock and the id generator, so the behaviour can be tested
/// without a UI or a real day rollover.
/// </summary>
internal static class TodoLogic
{
    public const int CardStep = 48;
    public const int CardHeight = 72;

    public static string DateKey(DateTime date) =>
        date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Index of the first completed item, i.e. the end of the open block.</summary>
    public static int FirstCompletedIndex(IReadOnlyList<DailyTodoItem> items)
    {
        for (var index = 0; index < items.Count; index++)
            if (items[index].Completed) return index;
        return items.Count;
    }

    public static TodoDay? FindDay(DailyTodoData data, DateTime date, bool create)
    {
        var key = DateKey(date);
        foreach (var day in data.Days)
            if (day.Date == key) return day;
        if (!create) return null;
        var created = new TodoDay { Date = key };
        data.Days.Add(created);
        return created;
    }

    /// <summary>
    /// The item the compact view shows: the running focus item when it is still an
    /// open item of today, otherwise today's first open item.
    /// </summary>
    public static DailyTodoItem? CurrentTodayItem(DailyTodoData data, DateTime today)
    {
        var day = FindDay(data, today, false);
        if (day is null) return null;
        if (data.FocusTimer is not null)
        {
            foreach (var item in day.Items)
                if (!item.Completed && item.Id == data.FocusTimer.ItemId) return item;
        }
        foreach (var item in day.Items)
            if (!item.Completed) return item;
        return null;
    }

    public static int CountCompleted(TodoDay day)
    {
        var completed = 0;
        foreach (var item in day.Items)
            if (item.Completed) completed++;
        return completed;
    }

    public static DailyTodoItem AddItem(TodoDay day, string text, ITodoClock clock, ITodoIdGenerator ids)
    {
        var item = new DailyTodoItem
        {
            Id = ids.NewId(),
            Text = text,
            CreatedAt = clock.Now
        };
        day.Items.Insert(FirstCompletedIndex(day.Items), item);
        return item;
    }

    /// <summary>Marks an item done and sinks it to the end, remembering its position.</summary>
    /// <summary>Mirrors the Windows NormalizeItem so older files stay loadable.</summary>
    public static void NormalizeItem(DailyTodoItem item)
    {
        if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
        item.Text ??= string.Empty;
        item.SubItems ??= [];
        item.SubItems.RemoveAll(subItem => subItem is null);
        foreach (var subItem in item.SubItems)
        {
            if (string.IsNullOrEmpty(subItem.Id)) subItem.Id = Guid.NewGuid().ToString("N");
            subItem.Text ??= string.Empty;
        }
    }

    /// <summary>Completing an item completes every sub item, and the reverse.</summary>
    public static void SetSubItemsCompleted(DailyTodoItem item, bool completed)
    {
        foreach (var subItem in item.SubItems) subItem.Completed = completed;
    }

    public static void ToggleSubItem(TodoSubItem subItem) => subItem.Completed = !subItem.Completed;

    public static void Complete(TodoDay day, DailyTodoItem item, DailyTodoData data)
    {
        var oldIndex = Math.Max(0, day.Items.IndexOf(item));
        day.Items.Remove(item);
        item.PreviousOpenIndex = oldIndex;
        SetSubItemsCompleted(item, true);
        item.Completed = true;
        day.Items.Add(item);
        CancelFocusFor(data, item.Id);
    }

    /// <summary>Restores a completed item to its remembered position, clamped.</summary>
    public static void Uncomplete(TodoDay day, DailyTodoItem item)
    {
        day.Items.Remove(item);
        item.Completed = false;
        SetSubItemsCompleted(item, false);
        var openCount = FirstCompletedIndex(day.Items);
        var restoreIndex = item.PreviousOpenIndex < 0
            ? openCount
            : Math.Max(0, Math.Min(item.PreviousOpenIndex, openCount));
        item.PreviousOpenIndex = -1;
        day.Items.Insert(restoreIndex, item);
    }

    /// <summary>
    /// Removes an item. A generated item suppresses its rule for the rest of the
    /// day so the scheduler does not immediately recreate it.
    /// </summary>
    public static void Delete(TodoDay day, DailyTodoItem item, DailyTodoData data)
    {
        day.Items.Remove(item);
        CancelFocusFor(data, item.Id);
        if (!string.IsNullOrEmpty(item.RecurringRuleId) && !day.SuppressedRuleIds.Contains(item.RecurringRuleId))
            day.SuppressedRuleIds.Add(item.RecurringRuleId);
    }

    public static bool MoveToBacklog(TodoDay day, DailyTodoItem item, DailyTodoData data)
    {
        if (item.Completed || !day.Items.Remove(item)) return false;
        item.BacklogSourceDate = string.IsNullOrEmpty(day.Date) ? DateKey(DateTime.Today) : day.Date;
        item.Completed = false;
        item.PreviousOpenIndex = -1;
        if (!string.IsNullOrEmpty(item.RecurringRuleId) && !day.SuppressedRuleIds.Contains(item.RecurringRuleId))
            day.SuppressedRuleIds.Add(item.RecurringRuleId);
        CancelFocusFor(data, item.Id);
        data.BacklogItems.Add(item);
        return true;
    }

    /// <summary>Ticking a backlog item moves it to today and marks it done.</summary>
    public static bool ToggleBacklogCompleted(DailyTodoData data, DailyTodoItem item, DateTime today)
    {
        if (!data.BacklogItems.Remove(item)) return false;
        item.Completed = true;
        item.PreviousOpenIndex = -1;
        FindDay(data, today, true)!.Items.Add(item);
        return true;
    }

    public static int MoveBacklogToToday(DailyTodoData data, IEnumerable<DailyTodoItem> items, DateTime today)
    {
        var day = FindDay(data, today, true)!;
        var openInsert = FirstCompletedIndex(day.Items);
        var moved = 0;
        foreach (var item in items.ToArray())
        {
            if (!data.BacklogItems.Remove(item)) continue;
            item.PreviousOpenIndex = -1;
            if (item.Completed) day.Items.Add(item);
            else day.Items.Insert(openInsert++, item);
            moved++;
        }
        return moved;
    }

    /// <summary>Removes backlog items and returns the entries needed to undo it.</summary>
    public static List<BacklogUndoEntry> DeleteBacklogItems(DailyTodoData data, IEnumerable<DailyTodoItem> items)
    {
        var entries = new List<BacklogUndoEntry>();
        foreach (var item in items)
        {
            var index = data.BacklogItems.IndexOf(item);
            if (index < 0) continue;
            entries.Add(new BacklogUndoEntry { Item = item, Index = index });
        }
        foreach (var entry in entries) data.BacklogItems.Remove(entry.Item);
        return entries;
    }

    /// <summary>Reinserts deleted backlog items at their original positions.</summary>
    public static void UndoBacklogDelete(DailyTodoData data, IReadOnlyList<BacklogUndoEntry> entries)
    {
        foreach (var entry in entries.OrderBy(value => value.Index))
        {
            var index = Math.Max(0, Math.Min(entry.Index, data.BacklogItems.Count));
            data.BacklogItems.Insert(index, entry.Item);
        }
    }

    /// <summary>Yesterday's unfinished items that have not been carried over yet.</summary>
    public static List<DailyTodoItem> ImportCandidates(DailyTodoData data, DateTime today)
    {
        var candidates = new List<DailyTodoItem>();
        var yesterday = FindDay(data, today.AddDays(-1), false);
        if (yesterday is null) return candidates;
        var day = FindDay(data, today, false);
        foreach (var item in yesterday.Items)
        {
            if (item.Completed) continue;
            if (day is not null && day.ImportedSourceIds.Contains(item.Id!)) continue;
            candidates.Add(item);
        }
        return candidates;
    }

    /// <summary>Copies chosen items into today; the originals stay where they are.</summary>
    public static int ImportItems(DailyTodoData data, IEnumerable<DailyTodoItem> selected, DateTime today,
        ITodoClock clock, ITodoIdGenerator ids)
    {
        var day = FindDay(data, today, true)!;
        var insert = FirstCompletedIndex(day.Items);
        var imported = 0;
        foreach (var source in selected)
        {
            if (day.ImportedSourceIds.Contains(source.Id!)) continue;
            day.Items.Insert(insert++, new DailyTodoItem
            {
                Id = ids.NewId(),
                Text = source.Text,
                CreatedAt = clock.Now,
                SourceId = source.Id
            });
            day.ImportedSourceIds.Add(source.Id!);
            imported++;
        }
        return imported;
    }

    /// <summary>Copies chosen items into the backlog, dated as yesterday's.</summary>
    public static int ImportItemsToBacklog(DailyTodoData data, IEnumerable<DailyTodoItem> selected, DateTime today,
        ITodoClock clock, ITodoIdGenerator ids)
    {
        var day = FindDay(data, today, true)!;
        var sourceDate = DateKey(today.AddDays(-1));
        var imported = 0;
        foreach (var source in selected)
        {
            if (day.ImportedSourceIds.Contains(source.Id!)) continue;
            data.BacklogItems.Add(new DailyTodoItem
            {
                Id = ids.NewId(),
                Text = source.Text,
                CreatedAt = clock.Now,
                SourceId = source.Id,
                BacklogSourceDate = sourceDate
            });
            day.ImportedSourceIds.Add(source.Id!);
            imported++;
        }
        return imported;
    }

    /// <summary>Drop position while dragging a card; only the open block is reachable.</summary>
    public static int ComputeDropIndex(double top, int openCount, int cardStep = CardStep)
    {
        if (openCount <= 0) return 0;
        var clamped = Math.Max(0, Math.Min(top, (openCount - 1) * (double)cardStep));
        var index = (int)Math.Round(clamped / cardStep);
        return Math.Max(0, Math.Min(index, openCount - 1));
    }

    /// <summary>Canvas height needed for the stacked cards.</summary>
    public static double CardCanvasHeight(int count) =>
        count <= 0 ? 330 : Math.Max(330, (count - 1) * (double)CardStep + CardHeight + 8);

    /// <summary>Per card inset that produces the stacked deck look.</summary>
    public static double CardInset(int index) => Math.Min(index, 4) * 3;

    /// <summary>Card background alpha for the deck depth.</summary>
    public static int CardAlpha(int index) => Math.Max(108, 148 - index * 9);

    public static void CancelFocusFor(DailyTodoData data, string? itemId)
    {
        if (data.FocusTimer is null || string.IsNullOrEmpty(itemId)) return;
        if (data.FocusTimer.ItemId == itemId) data.FocusTimer = null;
    }

    /// <summary>Clears today's suppression so a re-enabled rule can regenerate.</summary>
    public static void RestoreRecurringRulesForToday(DailyTodoData data, IEnumerable<string> ruleIds, DateTime today)
    {
        var day = FindDay(data, today, false);
        if (day is null) return;
        foreach (var ruleId in ruleIds)
            if (!string.IsNullOrEmpty(ruleId)) day.SuppressedRuleIds.Remove(ruleId);
    }
}

internal sealed class BacklogUndoEntry
{
    public required DailyTodoItem Item { get; init; }
    public required int Index { get; init; }
}

internal static class RecurringTodoEngine
{
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";

    /// <summary>
    /// Creates today's instance of every matching rule. Existing items and rules
    /// suppressed for the day are skipped, so this is safe to call repeatedly.
    /// </summary>
    public static bool Ensure(DailyTodoData data, DateTime date, ITodoClock clock, ITodoIdGenerator ids)
    {
        var changed = false;
        TodoDay? day = null;
        foreach (var rule in data.RecurringRules)
        {
            if (!Matches(rule, date)) continue;
            day ??= TodoLogic.FindDay(data, date, true);
            if (day!.SuppressedRuleIds.Contains(rule.Id!)) continue;
            if (day.Items.Any(item => item.RecurringRuleId == rule.Id)) continue;
            day.Items.Insert(TodoLogic.FirstCompletedIndex(day.Items), new DailyTodoItem
            {
                Id = ids.NewId(),
                Text = rule.Text,
                CreatedAt = clock.Now,
                RecurringRuleId = rule.Id
            });
            changed = true;
        }
        return changed;
    }

    public static bool Matches(RecurringTodoRule rule, DateTime date)
    {
        if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Text)) return false;
        if (DateTime.TryParseExact(rule.CreatedDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var created) && date.Date < created.Date)
            return false;
        return rule.Frequency switch
        {
            Daily => true,
            Weekly => (int)date.DayOfWeek == rule.ScheduleValue,
            Monthly => date.Day == rule.ScheduleValue,
            _ => false
        };
    }

    public static string Describe(RecurringTodoRule rule) => rule.Frequency switch
    {
        Daily => "每天",
        Weekly => "每周" + WeekdayName(rule.ScheduleValue),
        Monthly => "每月 " + rule.ScheduleValue + " 日",
        _ => rule.Frequency ?? string.Empty
    };

    public static string WeekdayName(int dayOfWeek) => dayOfWeek switch
    {
        0 => "日",
        1 => "一",
        2 => "二",
        3 => "三",
        4 => "四",
        5 => "五",
        6 => "六",
        _ => "?"
    };
}
