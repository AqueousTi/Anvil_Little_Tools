using Avalonia.Controls;
using Avalonia.Threading;

namespace LittleTools.Assistant;

/// <summary>
/// Linux side deviation (no Windows counterpart, Windows does not need it).
///
/// <para>
/// The window manager deletes <c>_NET_WM_STATE</c> when the assistant window is
/// unmapped (hidden by the click-away handler, Escape or the tray toggle), and
/// Avalonia pushes <c>Window.Topmost</c> and <c>Window.ShowInTaskbar</c> to the
/// backend only while the property <i>changes</i>. The XAML values
/// (<c>Topmost="True" ShowInTaskbar="False"</c>) therefore survive the first
/// show but not the show after a hide: the re-mapped window is managed without
/// <c>_NET_WM_STATE_SKIP_TASKBAR</c> and without <c>_NET_WM_STATE_ABOVE</c>, so
/// it reappears in the dock/taskbar and stops floating.
/// </para>
///
/// <para>
/// This file is a separate partial class on purpose: <c>MainWindow.axaml</c> and
/// <c>MainWindow.axaml.cs</c> are shared with the upstream Windows branch and are
/// edited there too, so the Linux fix lives here and hooks the window through the
/// virtual <see cref="OnOpened"/> override instead of adding a call site to the
/// shared constructor or to every show path. Both entry points (translation and
/// chat), the screenshot path and the tray/pipe toggle all end in
/// <c>IsVisible = true</c>, which is what is observed here.
/// </para>
///
/// <para>
/// The flag round trip itself is <see cref="Stock.StockWindow.ReapplyWindowFlags"/>,
/// the implementation already used by the stock capsule for the same X11
/// behaviour; it is reused so the two cannot drift.
/// </para>
/// </summary>
public sealed partial class MainWindow
{
    private bool _linuxWindowFlagsHooked;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!OperatingSystem.IsLinux()) return;

        if (!_linuxWindowFlagsHooked)
        {
            _linuxWindowFlagsHooked = true;
            // Driven by IsVisible, not by Opened: whether Opened is raised once
            // or on every show differs between Avalonia backends, while every
            // show path sets IsVisible. The subscription is installed on the
            // first open, and the first show is covered by the call below.
            PropertyChanged += (_, args) =>
            {
                if (args.Property == IsVisibleProperty) ReapplyLinuxWindowFlags();
            };
        }

        ReapplyLinuxWindowFlags();
    }

    /// <summary>
    /// Re-asserts skip-taskbar and always-on-top after a show. Posted at
    /// background priority so it runs after <c>Show()</c>/<c>Activate()</c> and
    /// the window is already mapped, which is when the window manager accepts
    /// the state change.
    /// </summary>
    private void ReapplyLinuxWindowFlags() => Dispatcher.UIThread.Post(() =>
    {
        if (!IsVisible) return;
        Stock.StockWindow.ReapplyWindowFlags(this, Topmost);
    }, DispatcherPriority.Background);
}
