namespace LittleTools.Assistant.Todo;

/// <summary>
/// Todo data model. Property names and the JSON shape deliberately match the
/// Windows <c>TodoNotes</c> module (public PascalCase fields written by
/// JavaScriptSerializer) so both platforms can read and write the same file.
/// </summary>
/// <summary>
/// A single level sub item of a todo, matching the Windows DailyTodoSubItem so the
/// same data file round trips between both builds.
/// </summary>
internal sealed class TodoSubItem
{
    public string? Id { get; set; }
    public string? Text { get; set; }
    public bool Completed { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal sealed class DailyTodoItem
{
    public string? Id { get; set; }
    public string? Text { get; set; }
    public bool Completed { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? SourceId { get; set; }
    public string? RecurringRuleId { get; set; }
    public string? BacklogSourceDate { get; set; }

    /// <summary>Position the item had before it was completed, used to restore it.</summary>
    public int PreviousOpenIndex { get; set; } = -1;

    /// <summary>Single level sub items, serialized as "SubItems" like Windows.</summary>
    public List<TodoSubItem> SubItems { get; set; } = [];
}

internal sealed class TodoDay
{
    public string? Date { get; set; }
    public List<DailyTodoItem> Items { get; set; } = [];
    public List<string> ImportedSourceIds { get; set; } = [];
    public List<string> SuppressedRuleIds { get; set; } = [];
}

internal sealed class RecurringTodoRule
{
    public string? Id { get; set; }
    public string? Text { get; set; }

    /// <summary>Daily, Weekly or Monthly.</summary>
    public string? Frequency { get; set; }

    /// <summary>Weekly: <see cref="DayOfWeek"/> value. Monthly: day of month.</summary>
    public int ScheduleValue { get; set; }

    public bool Enabled { get; set; } = true;
    public string? CreatedDate { get; set; }
}

internal sealed class DailyTodoData
{
    public List<TodoDay> Days { get; set; } = [];
    public List<DailyTodoItem> BacklogItems { get; set; } = [];
    public string? LastImportPromptDate { get; set; }
    public List<RecurringTodoRule> RecurringRules { get; set; } = [];
    public FocusTimerData? FocusTimer { get; set; }
}

internal sealed class FocusTimerData
{
    public string? ItemId { get; set; }
    public string? ItemText { get; set; }
    public int DurationMinutes { get; set; }

    /// <summary>Round-trip ("o") timestamp, matching the Windows field.</summary>
    public string? EndsAtUtc { get; set; }
}

/// <summary>Focus minutes can only be 0-100, matching the Windows dial.</summary>
internal static class FocusTimerLimits
{
    public const int MinimumMinutes = 0;
    public const int MaximumMinutes = 100;
}
