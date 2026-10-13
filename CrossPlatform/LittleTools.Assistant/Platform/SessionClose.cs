namespace LittleTools.Assistant.Platform;

/// <summary>
/// The suite's close rule on Linux: a user close hides the window and keeps the
/// suite in the tray (the Windows behaviour), but a desktop logout/shutdown has
/// to make the process quit, because the session cannot finish while a window
/// keeps answering its close request with a hide.
///
/// The caller has already cancelled the close (<c>eventArgs.Cancel = true</c>),
/// so nothing happens until the session question is answered. That answer is
/// bounded by <see cref="GnomeSession.QueryTimeout"/> and any failure counts as
/// "the session is running", so a normal close can only ever hide.
/// </summary>
internal static class SessionClose
{
    /// <summary>Hides the window, unless the desktop session is going away.</summary>
    internal static void HideOrQuit(Action hide)
        => _ = DecideAsync(hide, GnomeSession.Current, App.ExitForSessionEnding);

    /// <summary>
    /// The decision on its own, so the tests can drive both answers without a
    /// window, a D-Bus connection or a desktop session. Never throws.
    /// </summary>
    internal static async Task DecideAsync(Action hide, GnomeSession session, Action quit)
    {
        bool active;
        try
        {
            active = await session.IsSessionActiveAsync().ConfigureAwait(true);
        }
        catch
        {
            active = true;
        }

        try
        {
            if (active) hide();
            else quit();
        }
        catch
        {
            // A close handler must not take the process down through an
            // unobserved task exception.
        }
    }

    /// <summary>
    /// For a window that really closes on a user close (the todo capsule): the
    /// close itself stays the user's action, but a close request that arrives
    /// while the session is going away may be the last one the process sees, so
    /// it quits then.
    /// </summary>
    internal static void QuitIfSessionEnding()
        => _ = QuitIfSessionEndingAsync(GnomeSession.Current, App.ExitForSessionEnding);

    internal static async Task QuitIfSessionEndingAsync(GnomeSession session, Action quit)
    {
        try
        {
            if (!await session.IsSessionActiveAsync().ConfigureAwait(true)) quit();
        }
        catch
        {
            // Conservative: an unanswerable session question never quits.
        }
    }
}
