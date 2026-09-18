namespace LittleTools.Assistant.Services;

/// <summary>
/// Suite-wide module switches. The JSON shape is intentionally identical to the
/// Windows tray host's <c>manager.json</c> (public PascalCase fields written with
/// JavaScriptSerializer) so that the two platforms share one configuration file
/// and a Windows copy can be reused on Linux unchanged.
/// </summary>
internal sealed class SuiteSettings
{
    public bool MonitorEnabled { get; set; } = true;
    public bool TranslateEnabled { get; set; } = true;
    public bool TodoNotesEnabled { get; set; } = true;
    public bool StockEnabled { get; set; } = true;

    public bool EdgeHideMonitor { get; set; }
    public bool EdgeHideTranslate { get; set; }
    public bool EdgeHideTodo { get; set; }
    public bool EdgeHideStock { get; set; }

    public SuiteSettings Clone() => new()
    {
        MonitorEnabled = MonitorEnabled,
        TranslateEnabled = TranslateEnabled,
        TodoNotesEnabled = TodoNotesEnabled,
        StockEnabled = StockEnabled,
        EdgeHideMonitor = EdgeHideMonitor,
        EdgeHideTranslate = EdgeHideTranslate,
        EdgeHideTodo = EdgeHideTodo,
        EdgeHideStock = EdgeHideStock
    };
}

/// <summary>Loads and stores the suite switches, keeping the Windows field names.</summary>
internal sealed class SuiteSettingsStore
{
    private readonly string _path;
    private readonly bool _persist;
    private SuiteSettings _settings;

    /// <param name="path">Defaults to the platform manager.json location.</param>
    /// <param name="persist">
    /// Whether changes may be written back. On Windows the WPF tray host owns the
    /// file, so the assistant only reads it there; a second writer would clobber
    /// the host's in-memory settings.
    /// </param>
    public SuiteSettingsStore(string? path = null, bool? persist = null)
    {
        _path = path ?? AppPaths.ManagerSettingsPath;
        _persist = persist ?? OperatingSystem.IsLinux();
        _settings = AtomicJson.Read<SuiteSettings>(_path) ?? new SuiteSettings();
    }

    public string Path => _path;

    public bool CanPersist => _persist;

    public SuiteSettings Current => _settings;

    public void Set(Func<SuiteSettings, bool> change)
    {
        var next = _settings.Clone();
        if (!change(next)) return;
        _settings = next;
        if (_persist) AtomicJson.Write(_path, next);
    }

    public void Save()
    {
        if (_persist) AtomicJson.Write(_path, _settings);
    }

    /// <summary>
    /// Writes the defaults the first time so the module switches are discoverable
    /// in the documented file instead of only living in memory.
    /// </summary>
    public void EnsureFile()
    {
        if (!_persist || File.Exists(_path)) return;
        AtomicJson.Write(_path, _settings);
    }
}
