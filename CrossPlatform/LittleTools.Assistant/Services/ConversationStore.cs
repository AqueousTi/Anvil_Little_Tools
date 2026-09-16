namespace LittleTools.Assistant.Services;

internal sealed class ConversationStore
{
    private readonly string _path = Path.Combine(AppPaths.DataDirectory, "conversations.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<Conversation>> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var all = AtomicJson.Read<List<Conversation>>(_path) ?? [];
            var chatHistory = all
                .Where(item => item.Mode == AssistantMode.Chat)
                .Where(item => !item.Title.StartsWith("📷", StringComparison.Ordinal)
                    && !item.Messages.Any(message => message.Role == "user" && message.Content == "📷 截图翻译"))
                .OrderByDescending(item => item.UpdatedAt)
                .ToList();
            if (chatHistory.Count != all.Count) AtomicJson.Write(_path, chatHistory);
            return chatHistory;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(Conversation conversation)
    {
        if (conversation.Mode != AssistantMode.Chat || conversation.Messages.Count == 0) return;
        await _gate.WaitAsync();
        try
        {
            var all = AtomicJson.Read<List<Conversation>>(_path) ?? [];
            all.RemoveAll(item => item.Id == conversation.Id);
            conversation.UpdatedAt = DateTimeOffset.Now;
            all.Insert(0, conversation);
            if (all.Count > 200) all.RemoveRange(200, all.Count - 200);
            AtomicJson.Write(_path, all);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var all = AtomicJson.Read<List<Conversation>>(_path) ?? [];
            all.RemoveAll(item => item.Id == id);
            AtomicJson.Write(_path, all);
        }
        finally
        {
            _gate.Release();
        }
    }
}
