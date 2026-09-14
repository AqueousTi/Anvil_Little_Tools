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
            return (AtomicJson.Read<List<Conversation>>(_path) ?? [])
                .OrderByDescending(item => item.UpdatedAt)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(Conversation conversation)
    {
        if (conversation.Messages.Count == 0) return;
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
