namespace LittleTools.Assistant.Todo;

/// <summary>
/// The gentle capsule wording shown once today has nothing open, ported from the
/// Windows module. Every string is copied verbatim from
/// <c>DailyTodoWindow.BuildCompactMessages</c> so both builds read the same.
/// </summary>
internal static class TodoCompactMessages
{
    /// <summary>Windows falls back to this when a list is somehow empty.</summary>
    public const string Fallback = "今天想先做点什么？";

    /// <summary>Windows BuildCompactMessages(total, backlogCount).</summary>
    public static List<string> BuildCompactMessages(int total, int backlogCount)
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
}

/// <summary>
/// State behind the Windows <c>UpdateCompactPriority</c> / <c>RotateCompactMessage</c>
/// pair: the message list belongs to a (completed count, backlog count) state, one
/// entry is picked at random and then advanced once per timer tick.
/// </summary>
internal sealed class CompactMessageRotation
{
    private readonly Random _random;
    private string? _state;
    private List<string> _messages = [];

    public CompactMessageRotation(Random? random = null) => _random = random ?? new Random();

    /// <summary>Index of the current message, or -1 while an item is open.</summary>
    public int Index { get; private set; } = -1;

    /// <summary>True while the capsule shows an open item instead of a message.</summary>
    public bool HasPriority { get; private set; }

    public IReadOnlyList<string> Messages => _messages;

    /// <summary>
    /// Windows UpdateCompactPriority: an open item always wins and clears the
    /// rotation, otherwise the message set is rebuilt whenever the counts change.
    /// </summary>
    public string Update(DailyTodoItem? priority, int total, int backlogCount)
    {
        HasPriority = priority is not null;
        if (priority is not null)
        {
            _state = null;
            _messages = [];
            Index = -1;
            return priority.Text ?? string.Empty;
        }

        var state = (total == 0 ? "empty" : "completed") + ":" + total + ":" + backlogCount;
        if (_state != state || _messages.Count == 0)
        {
            _state = state;
            _messages = TodoCompactMessages.BuildCompactMessages(total, backlogCount);
            Index = _messages.Count == 0 ? -1 : _random.Next(_messages.Count);
        }
        return Index < 0 ? TodoCompactMessages.Fallback : _messages[Index];
    }

    /// <summary>
    /// Windows RotateCompactMessage: advances to the next message every five
    /// minutes, but only while nothing is open and there is something to rotate to.
    /// </summary>
    public string? Rotate(DailyTodoItem? current)
    {
        if (_messages.Count < 2 || current is not null) return null;
        Index = (Index + 1) % _messages.Count;
        return _messages[Index];
    }
}
