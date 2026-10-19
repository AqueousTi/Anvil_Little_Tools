using Avalonia.Controls;
using Avalonia.Threading;

namespace LittleTools.Assistant;

/// <summary>
/// Click to magnify a finished screenshot translation.
///
/// This lives in its own partial file because <c>MainWindow.axaml.cs</c> is shared
/// with upstream main: the shared file only gains the one
/// <see cref="ScreenshotPreview.MakePreviewable"/> call in
/// <c>AddScreenshotResult</c> and the <c>_previewWindowOpen</c> term in the
/// deactivation guard.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// True while the magnified preview window is open, and briefly after it was
    /// closed by Escape or its close button.
    ///
    /// The assistant hides itself whenever it loses the focus, and opening the
    /// preview takes that focus away, so without this term the window (and the
    /// translation the user just asked to magnify) would vanish the moment the
    /// viewer opened. This is the same guard the settings dialog uses
    /// (<c>_settingsDialogOpen</c>); the preview is deliberately not modal, so the
    /// close-on-focus-loss path of the viewer stays reachable.
    ///
    /// The guard outlives an explicit close on purpose: unmapping the viewer makes
    /// the desktop hand the focus to something else for a moment, and the assistant
    /// must not read that as the user clicking away.
    /// </summary>
    private bool _previewWindowOpen;

    private ScreenshotPreviewWindow? _previewWindow;
    private int _previewActivationVersion;

    private void OpenScreenshotPreview(Control anchor, string path)
    {
        if (_previewWindow is { IsVisible: true })
        {
            _previewWindow.Activate();
            return;
        }
        _previewWindowOpen = true;
        var preview = ScreenshotPreview.Show(anchor, path);
        if (preview is null)
        {
            _previewWindowOpen = false;
            return;
        }
        var version = ++_previewActivationVersion;
        _previewWindow = preview;
        preview.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_previewWindow, preview)) return;
            _previewWindow = null;
            if (preview.ClosedByFocusLoss)
            {
                // The user went to another window, so the suite's own click-away rule
                // applies and the assistant hides itself as usual.
                _previewWindowOpen = false;
                if (!App.SmokeTest && !IsActive && CompanionWindow?.IsActive != true
                    && !_pinned && !_captureInProgress && !_settingsDialogOpen)
                    Hide();
                return;
            }
            // Escape or the close button: bring the assistant back in front once the
            // focus has settled instead of leaving the suite behind another window.
            DispatcherTimer.RunOnce(() =>
            {
                if (version == _previewActivationVersion && _previewWindow is null && IsVisible)
                    Activate();
            }, TimeSpan.FromMilliseconds(250));
            DispatcherTimer.RunOnce(() =>
            {
                if (version == _previewActivationVersion && _previewWindow is null)
                    _previewWindowOpen = false;
            }, TimeSpan.FromMilliseconds(900));
        };
    }

    private void CloseScreenshotPreview()
    {
        _previewActivationVersion++;
        var preview = _previewWindow;
        _previewWindow = null;
        _previewWindowOpen = false;
        preview?.Close();
    }
}
