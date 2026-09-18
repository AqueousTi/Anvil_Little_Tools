namespace LittleTools.Assistant.Todo;

/// <summary>Time source, so date rollover and timer expiry can be tested.</summary>
internal interface ITodoClock
{
    DateTime Now { get; }
    DateTime Today { get; }
    DateTime UtcNow { get; }
}

internal sealed class SystemTodoClock : ITodoClock
{
    public DateTime Now => DateTime.Now;
    public DateTime Today => DateTime.Today;
    public DateTime UtcNow => DateTime.UtcNow;
}

internal interface ITodoIdGenerator
{
    string NewId();
}

internal sealed class GuidTodoIdGenerator : ITodoIdGenerator
{
    public string NewId() => Guid.NewGuid().ToString("N");
}
