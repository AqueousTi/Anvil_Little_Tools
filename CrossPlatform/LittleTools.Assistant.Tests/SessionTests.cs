using System.Diagnostics;
using LittleTools.Assistant.Platform;

/// <summary>
/// Covers the Linux session layer behind the logout fix: the conservative
/// "the session is running" answer, the test-only force switches, the close rule
/// (hide on a user close, quit when the session is going away) and the
/// SessionOver watch.
/// </summary>
internal static class SessionTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        // The runner only reports failures, so the session checks count themselves
        // and print a summary like the other groups do.
        var count = 0;
        void Check(string name, bool result)
        {
            count++;
            check(name, result);
        }

        await CheckActivityAsync(Check);
        await CheckForceSwitchesAsync(Check);
        await CheckCloseRuleAsync(Check);
        await CheckSessionOverWatchAsync(Check);
        Console.WriteLine($"SESSION: {count} checks");
    }

    private static async Task CheckActivityAsync(Action<string, bool> check)
    {
        using (var active = new GnomeSession(new ScriptedSessionManagerBus(true)))
        {
            check("session manager asked when available", active.IsAvailable);
            check("live session answers active", await active.IsSessionActiveAsync());
            check("live session is not ending", !active.IsEnding);
        }

        var mutable = new MutableSessionManagerBus(isActive: false);
        using (var ending = new GnomeSession(mutable))
        {
            check("a session that is going away answers not active", !await ending.IsSessionActiveAsync());
            check("an ending session is remembered", ending.IsEnding);
            mutable.IsActive = true;
            check("an ending session stays ending", !await ending.IsSessionActiveAsync());
        }

        // A desktop without GNOME SessionManager (or Windows) never quits.
        using (var absent = new GnomeSession(null))
        {
            check("no session manager is not available", !absent.IsAvailable);
            check("no session manager counts as active", await absent.IsSessionActiveAsync());
        }

        // The service exists but cannot be reached: conservative answer.
        using (var failing = new GnomeSession(new FailingSessionManagerBus()))
            check("an unreachable session manager counts as active", await failing.IsSessionActiveAsync());

        // The service is there but never answers: the query must give up fast.
        using (var silent = new GnomeSession(new SilentSessionManagerBus()))
        {
            var watch = Stopwatch.StartNew();
            var isActive = await silent.IsSessionActiveAsync();
            watch.Stop();
            check("a session manager that never answers counts as active", isActive);
            check("the session question is bounded under a second",
                watch.Elapsed < TimeSpan.FromMilliseconds(1500));
            check("a timed out question leaves the query fast",
                await silent.IsSessionActiveAsync() && watch.Elapsed < TimeSpan.FromMilliseconds(2000));
        }
    }

    private static async Task CheckForceSwitchesAsync(Action<string, bool> check)
    {
        static Func<string, string?> Environment(params (string Key, string Value)[] values)
        {
            var map = values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            return name => map.TryGetValue(name, out var value) ? value : null;
        }

        using (var forced = GnomeSession.Create(
            Environment((GnomeSession.ForceEndingVariable, "1")), linux: true, () => null))
        {
            check("the ending switch is honoured", !await forced.IsSessionActiveAsync());
            check("the ending switch reports itself as ending", forced.IsEnding);
        }

        using (var forced = GnomeSession.Create(
            Environment((GnomeSession.ForceActiveVariable, "1")), linux: true,
            () => new ScriptedSessionManagerBus(false)))
            check("the active switch wins over the session manager", await forced.IsSessionActiveAsync());

        using (var off = GnomeSession.Create(
            Environment((GnomeSession.ForceEndingVariable, "0")), linux: true,
            () => new ScriptedSessionManagerBus(true)))
            check("a zero switch is not a switch", await off.IsSessionActiveAsync());

        // Not Linux: the Windows hide-on-close behaviour is untouched even when a
        // test switch is set.
        using (var windows = GnomeSession.Create(
            Environment((GnomeSession.ForceEndingVariable, "1")), linux: false, () => null))
            check("windows keeps the plain hide behaviour", await windows.IsSessionActiveAsync());
    }

    private static async Task CheckCloseRuleAsync(Action<string, bool> check)
    {
        async Task<(bool Hid, bool Quit)> DecideAsync(GnomeSession session)
        {
            var hid = false;
            var quit = false;
            await SessionClose.DecideAsync(() => hid = true, session, () => quit = true);
            return (hid, quit);
        }

        using (var active = new GnomeSession(new ScriptedSessionManagerBus(true)))
        {
            var (hid, quit) = await DecideAsync(active);
            check("a user close hides the window", hid && !quit);
        }

        using (var ending = new GnomeSession(new ScriptedSessionManagerBus(false)))
        {
            var (hid, quit) = await DecideAsync(ending);
            check("a logout close request quits instead of hiding", quit && !hid);
        }

        // Anything that cannot be answered keeps the Windows behaviour.
        using (var failing = new GnomeSession(new FailingSessionManagerBus()))
        {
            var (hid, quit) = await DecideAsync(failing);
            check("an unanswerable session question still hides", hid && !quit);
        }

        // The window that really closes (the todo capsule) only quits when the
        // session is going away.
        var quits = 0;
        using (var active = new GnomeSession(new ScriptedSessionManagerBus(true)))
        {
            await SessionClose.QuitIfSessionEndingAsync(active, () => quits++);
            check("a real close on a live session does not quit", quits == 0);
        }
        using (var ending = new GnomeSession(new ScriptedSessionManagerBus(false)))
        {
            await SessionClose.QuitIfSessionEndingAsync(ending, () => quits++);
            check("a real close while the session ends quits", quits == 1);
        }
    }

    private static async Task CheckSessionOverWatchAsync(Action<string, bool> check)
    {
        var bus = new ScriptedSessionManagerBus(true);
        var fired = 0;
        using var session = new GnomeSession(bus);
        session.WatchSessionOver(() => fired++);
        for (var attempt = 0; attempt < 50 && !bus.WatchInstalled; attempt++)
            await Task.Delay(10);
        check("the SessionOver watch is installed", bus.WatchInstalled);

        bus.EmitSessionOver();
        check("SessionOver quits through the callback", fired == 1);
        check("SessionOver marks the session as ending", session.IsEnding);
        check("SessionOver wins over a live property answer", !await session.IsSessionActiveAsync());

        // Watching a desktop without a session manager must stay silent.
        using var absent = new GnomeSession(null);
        absent.WatchSessionOver(() => fired++);
        check("watching without a session manager does nothing", fired == 1);
    }

    /// <summary>A session manager whose answer can be flipped mid test.</summary>
    private sealed class MutableSessionManagerBus : ISessionManagerBus
    {
        public MutableSessionManagerBus(bool isActive) => IsActive = isActive;

        public bool IsActive { get; set; }

        public Task<bool> ReadSessionIsActiveAsync(CancellationToken cancellationToken)
            => Task.FromResult(IsActive);

        public Task WatchSessionOverAsync(Action onSessionOver, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Dispose() { }
    }

    /// <summary>A session manager that cannot be reached.</summary>
    private sealed class FailingSessionManagerBus : ISessionManagerBus
    {
        public Task<bool> ReadSessionIsActiveAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("no D-Bus session bus");

        public Task WatchSessionOverAsync(Action onSessionOver, CancellationToken cancellationToken)
            => throw new InvalidOperationException("no D-Bus session bus");

        public void Dispose() { }
    }

    /// <summary>A session manager that accepts the question and never answers.</summary>
    private sealed class SilentSessionManagerBus : ISessionManagerBus
    {
        public async Task<bool> ReadSessionIsActiveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        }

        public Task WatchSessionOverAsync(Action onSessionOver, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Dispose() { }
    }
}
