using Tmds.DBus.Protocol;

namespace LittleTools.Assistant.Platform;

/// <summary>
/// One lazily opened session-bus connection, shared by the calls of a single
/// backend. It mirrors the connection handling in <see cref="SessionManagerBus"/>:
/// a desktop without a session bus never pays for the connection, a failure to
/// connect is remembered instead of retried per capture, and the caller always
/// gets <see langword="null"/> rather than an exception.
/// </summary>
internal sealed class SessionBusConnection : IDisposable
{
    private readonly Lazy<Task<DBusConnection?>> _connection = new(ConnectAsync);

    private static async Task<DBusConnection?> ConnectAsync()
    {
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address)) return null;
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            return null;
        }
    }

    public Task<DBusConnection?> GetAsync(CancellationToken cancellationToken) =>
        _connection.Value.WaitAsync(cancellationToken);

    public void Dispose()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
            _connection.Value.Result?.Dispose();
    }
}
