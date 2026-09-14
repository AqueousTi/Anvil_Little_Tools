using System.IO.Pipes;
using System.Text;

namespace LittleTools.Assistant;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "LittleTools.Assistant.Singleton.v1";
    private const string PipeName = "LittleTools.Assistant.Command.v1";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(true, MutexName, out var created);
        IsPrimary = created;
    }

    public bool IsPrimary { get; }

    public async Task<bool> SendAsync(AppCommand command)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1800);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync(command.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartListening(Action<AppCommand> onCommand)
    {
        if (!IsPrimary || _serverTask is not null) return;
        _serverTask = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cancellation.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                    var text = await reader.ReadLineAsync(_cancellation.Token);
                    if (Enum.TryParse<AppCommand>(text, true, out var command)) onCommand(command);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(250, _cancellation.Token).ConfigureAwait(false);
                }
            }
        });
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}
