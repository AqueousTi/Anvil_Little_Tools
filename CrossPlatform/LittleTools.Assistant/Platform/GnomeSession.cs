using Tmds.DBus.Protocol;

namespace LittleTools.Assistant.Platform;

/// <summary>
/// The D-Bus surface the logout handling needs from the desktop session manager:
/// read <c>org.gnome.SessionManager.SessionIsActive</c> and hear
/// <c>SessionOver</c>.
///
/// The real implementation is <see cref="SessionManagerBus"/>. Tests and the
/// test-only <c>LITTLETOOLS_FORCE_SESSION_*</c> switches substitute
/// <see cref="ScriptedSessionManagerBus"/>, so both answers ("the session is
/// running" / "the session is going away") can be exercised without logging the
/// user out.
/// </summary>
internal interface ISessionManagerBus : IDisposable
{
    /// <summary>
    /// Reads the session manager's <c>SessionIsActive</c> property. False means
    /// the session is on its way down (logout/shutdown).
    /// </summary>
    Task<bool> ReadSessionIsActiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Invokes <paramref name="onSessionOver"/> when the session manager emits
    /// <c>SessionOver</c>. The callback must be cheap; it runs on a D-Bus thread.
    /// </summary>
    Task WatchSessionOverAsync(Action onSessionOver, CancellationToken cancellationToken);
}

/// <summary>
/// The Linux session layer behind the suite's close behaviour.
///
/// Windows hides a window on close and keeps running in the tray. A desktop
/// logout/shutdown is not a user close, and an application that answers the
/// session's close request by hiding is what makes "shut down" look like it does
/// nothing. <c>org.gnome.SessionManager.SessionIsActive</c> is false while the
/// session is going down, so a close request can be told apart from a user close.
///
/// Everything here is deliberately conservative: a missing session bus, a
/// desktop without GNOME SessionManager, an unreachable service, a timeout or
/// any other failure answers "the session is running". Quitting the suite during
/// a healthy session would be much worse than a session that has to be asked
/// again to close.
/// </summary>
internal sealed class GnomeSession : IDisposable
{
    internal const string ServiceName = "org.gnome.SessionManager";
    internal const string ObjectPath = "/org/gnome/SessionManager";
    internal const string InterfaceName = "org.gnome.SessionManager";
    internal const string ActiveProperty = "SessionIsActive";
    internal const string OverSignal = "SessionOver";

    /// <summary>
    /// Test-only override: answer "the session is ending" without a logout.
    /// Any non-empty value except "0"/"false" enables it.
    /// </summary>
    internal const string ForceEndingVariable = "LITTLETOOLS_FORCE_SESSION_ENDING";

    /// <summary>Test-only override: force "the session is running".</summary>
    internal const string ForceActiveVariable = "LITTLETOOLS_FORCE_SESSION_ACTIVE";

    /// <summary>
    /// Upper bound for one "is the session running?" answer. GNOME waits for the
    /// session's processes while logging out, so this must stay well under a
    /// second: the close request handler is blocked on it.
    /// </summary>
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(800);

    private static GnomeSession? _current;

    /// <summary>The process wide session layer.</summary>
    internal static GnomeSession Current => _current ??= Create(
        Environment.GetEnvironmentVariable,
        OperatingSystem.IsLinux(),
        static () => new SessionManagerBus());

    /// <summary>
    /// Builds a session layer from its environment, so the platform rules stay
    /// testable: a desktop that is not Linux, or a Linux desktop without GNOME
    /// SessionManager, keeps the plain hide-on-close behaviour.
    /// </summary>
    internal static GnomeSession Create(
        Func<string, string?> environment,
        bool linux,
        Func<ISessionManagerBus?>? busFactory = null)
    {
        if (!linux) return new GnomeSession(null);
        if (IsSet(environment(ForceEndingVariable))) return new GnomeSession(new ScriptedSessionManagerBus(false));
        if (IsSet(environment(ForceActiveVariable))) return new GnomeSession(new ScriptedSessionManagerBus(true));
        return new GnomeSession(busFactory?.Invoke());
    }

    private readonly ISessionManagerBus? _bus;
    private readonly object _gate = new();
    private bool _watching;
    private bool _disposed;
    private int _ending;

    internal GnomeSession(ISessionManagerBus? bus) => _bus = bus;

    /// <summary>The desktop exposes a session manager to ask.</summary>
    internal bool IsAvailable => _bus is not null;

    /// <summary>The session is known to be going away; nothing is hidden after that.</summary>
    internal bool IsEnding => Volatile.Read(ref _ending) == 1;

    /// <summary>
    /// True while the login session is still running. Never throws; anything that
    /// cannot be answered counts as "running" (see the class remarks).
    /// </summary>
    internal async Task<bool> IsSessionActiveAsync(CancellationToken cancellationToken = default)
    {
        if (IsEnding) return false;
        var bus = _bus;
        if (bus is null) return true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(QueryTimeout);
            var active = await bus.ReadSessionIsActiveAsync(timeout.Token)
                .WaitAsync(QueryTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!active) MarkEnding();
            return active;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Starts listening for <c>SessionOver</c>, which the session manager emits
    /// once the session is over. Best effort: a desktop that never emits it is
    /// still covered by <see cref="IsSessionActiveAsync"/> on the close request.
    /// </summary>
    internal void WatchSessionOver(Action onSessionOver)
    {
        var bus = _bus;
        if (bus is null || _disposed) return;
        lock (_gate)
        {
            if (_watching) return;
            _watching = true;
        }
        _ = WatchAsync(bus, onSessionOver);
    }

    private async Task WatchAsync(ISessionManagerBus bus, Action onSessionOver)
    {
        try
        {
            await bus.WatchSessionOverAsync(() =>
            {
                MarkEnding();
                try { onSessionOver(); } catch { /* the watcher thread must survive a bad callback */ }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Watching is the extra safety net; the close request check still runs.
        }
    }

    private void MarkEnding() => Interlocked.Exchange(ref _ending, 1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bus?.Dispose();
    }

    private static bool IsSet(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
           && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The real session manager connection. The bus connection is opened lazily on
/// the first question, so a desktop without D-Bus never pays for it, and the
/// answer is bounded by <see cref="GnomeSession.QueryTimeout"/> at the caller.
/// </summary>
internal sealed class SessionManagerBus : ISessionManagerBus
{
    private readonly Lazy<Task<DBusConnection?>> _connection = new(ConnectAsync);
    private IDisposable? _watcher;

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

    public async Task<bool> ReadSessionIsActiveAsync(CancellationToken cancellationToken)
    {
        var connection = await _connection.Value.WaitAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No D-Bus session bus is available.");
        var message = CreateGetPropertyMessage(connection);
        return await connection.CallMethodAsync(message, static (Message reply, object? state) =>
        {
            var reader = reply.GetBodyReader();
            return reader.ReadVariantValue().GetBool();
        }, null).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WatchSessionOverAsync(Action onSessionOver, CancellationToken cancellationToken)
    {
        var connection = await _connection.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null) return;
        _watcher = await connection.WatchSignalAsync(
            sender: GnomeSession.ServiceName,
            path: GnomeSession.ObjectPath,
            @interface: GnomeSession.InterfaceName,
            signal: GnomeSession.OverSignal,
            reader: static (Message message, object? state) => true,
            handler: (Notification<bool> notification) =>
            {
                if (notification.HasValue) onSessionOver();
            },
            flags: ObserverFlags.None,
            emitOnCapturedContext: false,
            state: null).ConfigureAwait(false);
    }

    private static MessageBuffer CreateGetPropertyMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: GnomeSession.ServiceName,
            path: GnomeSession.ObjectPath,
            @interface: "org.freedesktop.DBus.Properties",
            member: "Get",
            signature: "ss",
            flags: MessageFlags.None);
        writer.WriteString(GnomeSession.InterfaceName);
        writer.WriteString(GnomeSession.ActiveProperty);
        return writer.CreateMessage();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
            _connection.Value.Result?.Dispose();
    }
}

/// <summary>
/// Answers a fixed value and can emit <c>SessionOver</c> on demand. It backs the
/// test-only force switches and the unit tests.
/// </summary>
internal sealed class ScriptedSessionManagerBus : ISessionManagerBus
{
    private readonly bool _isActive;
    private Action? _onSessionOver;

    internal ScriptedSessionManagerBus(bool isActive) => _isActive = isActive;

    /// <summary>True once the SessionOver watch was installed.</summary>
    internal bool WatchInstalled => _onSessionOver is not null;

    /// <summary>Emits SessionOver the way the session manager would.</summary>
    internal void EmitSessionOver() => _onSessionOver?.Invoke();

    public Task<bool> ReadSessionIsActiveAsync(CancellationToken cancellationToken)
        => Task.FromResult(_isActive);

    public Task WatchSessionOverAsync(Action onSessionOver, CancellationToken cancellationToken)
    {
        _onSessionOver = onSessionOver;
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
