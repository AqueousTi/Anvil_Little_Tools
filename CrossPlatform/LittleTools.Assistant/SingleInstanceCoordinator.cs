using LittleTools.Common;

namespace LittleTools.Assistant;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "LittleTools.Assistant.Singleton.v1";
    private readonly Mutex _mutex;
    private CommandPipe? _server;
    private readonly bool _isolated;

    public SingleInstanceCoordinator(bool isolated = false)
    {
        _isolated = isolated;
        _mutex = new Mutex(true, isolated ? MutexName + ".Test." + Guid.NewGuid().ToString("N") : MutexName, out var created);
        IsPrimary = created;
    }

    public bool IsPrimary { get; }

    /// <summary>
    /// The named mutex is scoped to the login session on Linux, so an instance
    /// started from another session (a launcher using setsid, a second TTY) would
    /// wrongly consider itself primary. The command pipe is machine wide, so it is
    /// the authoritative check. A "ping" is acknowledged without dispatching a
    /// command, so probing never opens a window.
    /// </summary>
    public bool HasLivePeer(string? pipeName = null) =>
        CommandPipe.Send(pipeName ?? CommandPipe.AssistantName, "ping", 800) > 0;

    public Task<bool> SendAsync(AppCommand command, bool managed = false) => Task.Run(() =>
        CommandPipe.Send(CommandPipe.AssistantName, (managed ? "managed:" : "") + command, 3000) > 0);

    public void StartListening(Action<AppCommand, bool> onCommand)
    {
        if (!IsPrimary || _isolated || _server is not null) return;
        _server = new CommandPipe(CommandPipe.AssistantName, text =>
        {
            var managed = text.StartsWith("managed:", StringComparison.Ordinal);
            if (managed) text = text[8..];
            if (Enum.TryParse<AppCommand>(text, true, out var command)) onCommand(command, managed);
        });
    }

    public void Dispose()
    {
        _server?.Dispose();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
