using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Module lifecycle for the daily todo. The suite host starts it when the module
/// switch is on and stops it when it is turned off; data is only ever written by
/// the store, so stopping never loses anything.
/// </summary>
internal sealed class TodoModule : IDisposable
{
    private readonly TodoStore _store;
    private readonly DailyTodoData _data;
    private readonly ITodoClock _clock = new SystemTodoClock();
    private readonly ITodoIdGenerator _ids = new GuidTodoIdGenerator();
    private readonly ITodoSoundService _sound = TodoSoundServiceFactory.Create();
    private readonly INotificationService _notifications;
    private TodoWindow? _window;
    private bool _edgeHideEnabled;

    public TodoModule(INotificationService notifications, bool edgeHideEnabled)
    {
        _notifications = notifications;
        _edgeHideEnabled = edgeHideEnabled;
        _store = new TodoStore();
        _data = _store.Load();
    }

    /// <summary>Where the data came from, for the diagnostics snapshot.</summary>
    public string DataPath => _store.DataPath;

    public string? ImportedFrom => _store.ImportedFrom;

    public string? LoadWarning => _store.LoadWarning;

    public bool IsRunning => _window is not null;

    /// <summary>
    /// Starts the module. Like the Windows host the capsule appears first; the
    /// user expands it by clicking, or the tray entry expands it directly.
    /// </summary>
    public void Start()
    {
        if (_window is null)
        {
            _window = new TodoWindow(_store, _data, _clock, _ids, _sound, _edgeHideEnabled);
            _window.FocusFinished += OnFocusFinished;
        }
        _window.Show();
        _window.EnsurePositioned();
    }

    public void ShowToday()
    {
        if (_window is null)
        {
            Start();
            return;
        }
        _window.ShowToday();
    }

    /// <summary>Hides the window but keeps the module loaded.</summary>
    public void Hide() => _window?.HideWindow();

    public void Stop()
    {
        if (_window is null) return;
        _window.FocusFinished -= OnFocusFinished;
        _window.HideWindow();
        _window.Close();
        _window = null;
    }

    public void SetEdgeHideEnabled(bool enabled)
    {
        _edgeHideEnabled = enabled;
        _window?.SetEdgeHideEnabled(enabled);
    }

    private void OnFocusFinished(string itemText)
    {
        var text = string.IsNullOrWhiteSpace(itemText) ? "专注时间结束。" : $"“{itemText}” 专注时间结束。";
        _notifications.Show("专注结束", text, 4500);
    }

    public void Dispose() => Stop();
}
