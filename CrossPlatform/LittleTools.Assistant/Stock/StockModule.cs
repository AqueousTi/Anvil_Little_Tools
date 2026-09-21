using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Module lifecycle for the stock monitor. The suite host starts it when the
/// module switch is on and stops it when it is turned off, mirroring the Windows
/// tray host's StartStock/StopStock (LittleTools/Program.cs L436-L460): the
/// capsule appears immediately and the settings are only ever written by the
/// store, so stopping never loses anything.
/// </summary>
internal sealed class StockModule : IDisposable
{
    private readonly StockStore _store;
    private readonly StockSettings _settings;
    private readonly INotificationService _notifications;
    private StockWindow? _window;
    private bool _edgeHideEnabled;

    public StockModule(INotificationService notifications, bool edgeHideEnabled)
    {
        _notifications = notifications;
        _edgeHideEnabled = edgeHideEnabled;
        _store = new StockStore();
        _settings = _store.Load();
    }

    /// <summary>Where the settings came from, for the diagnostics snapshot.</summary>
    public string DataPath => _store.DataPath;

    public string? ImportedFrom => _store.ImportedFrom;

    public string? LoadWarning => _store.LoadWarning;

    public bool IsRunning => _window is not null;

    public bool IsVisible => _window is { IsVisible: true };

    /// <summary>Windows StartStock: build the window and show the capsule.</summary>
    public void Start()
    {
        if (_window is null)
        {
            _window = new StockWindow(_store, _settings, service: null, _edgeHideEnabled);
            _window.AlertRaised += OnAlert;
        }
        _window.ShowWindow();
    }

    /// <summary>Toggles the capsule, matching the Windows tray "显示 / 隐藏股票观察".</summary>
    public void ToggleVisibility()
    {
        if (_window is null)
        {
            Start();
            return;
        }
        _window.ToggleVisibility();
    }

    public void Hide() => _window?.ToggleVisibility();

    public void Stop()
    {
        if (_window is null) return;
        _window.AlertRaised -= OnAlert;
        _window.ClosePermanently();
        _window = null;
    }

    public void SetEdgeHideEnabled(bool enabled)
    {
        _edgeHideEnabled = enabled;
        _window?.SetEdgeHideEnabled(enabled);
    }

    /// <summary>Windows ShowStockAlert: a tray balloon, here a desktop notification.</summary>
    private void OnAlert(string title, string message) => _notifications.Show(title, message, 6000);

    public void Dispose() => Stop();
}
