using System.Text;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Persistence for the stock watch list. Ported from the Windows
/// <c>StockStore</c> (StockMonitor/StockData.cs L69-L114): one
/// <c>settings.json</c> written atomically, no backup generation, and a load path
/// that repairs anything a hand edited or half written file may contain.
/// </summary>
internal sealed class StockStore
{
    private const string FileName = "settings.json";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly string _directory;

    public StockStore(string? directory = null) => _directory = directory ?? AppPaths.StockDirectory;

    public string DataPath => Path.Combine(_directory, FileName);

    /// <summary>Set when the settings were adopted from a Windows installation.</summary>
    public string? ImportedFrom { get; private set; }

    /// <summary>Set when the settings file existed but could not be parsed.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Windows StockData.cs L79-L108.</summary>
    public StockSettings Load()
    {
        ImportLegacyFileIfNeeded();

        StockSettings? settings = null;
        try
        {
            if (File.Exists(DataPath))
                settings = StockJson.Deserialize(File.ReadAllText(DataPath, Encoding.UTF8));
        }
        catch
        {
            LoadWarning = "settings.json 无法解析，已改用默认自选股。";
        }

        settings ??= new StockSettings();
        Normalize(settings);
        return settings;
    }

    /// <summary>Windows AtomicFile.WriteUtf8: temp file in the same directory, then replace.</summary>
    public bool Save(StockSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var temporary = DataPath + ".tmp";
            File.WriteAllText(temporary, StockJson.Serialize(settings), Utf8WithoutBom);
            File.Move(temporary, DataPath, true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(DataPath + ".tmp")) File.Delete(DataPath + ".tmp");
            }
            catch
            {
                // A leftover .tmp file is harmless; the next save overwrites it.
            }
        }
    }

    /// <summary>
    /// Windows StockData.cs L88-L107, kept exactly: drop invalid codes, repair an
    /// impossible threshold, seed the two default funds and fall back to the first
    /// watched code when the selection is invalid.
    /// </summary>
    internal static void Normalize(StockSettings settings)
    {
        settings.Watched ??= [];
        settings.Watched.RemoveAll(item => item is null || !StockMath.IsValidCode(item.Code));
        foreach (var item in settings.Watched)
        {
            item.Code = item.Code!.Trim();
            if (item.PremiumThreshold <= -100 || item.PremiumThreshold > 100) item.PremiumThreshold = 2.0;
        }

        if (settings.Watched.Count == 0)
        {
            settings.Watched.Add(new StockWatchEntry { Code = "510300", AlertEnabled = false });
            settings.Watched.Add(new StockWatchEntry { Code = "513500", AlertEnabled = true, PremiumThreshold = 2.0 });
        }

        if (!StockMath.IsValidCode(settings.SelectedCode)) settings.SelectedCode = settings.Watched[0].Code!;
        if (settings.RangeYears != 0 && settings.RangeYears != 1 && settings.RangeYears != 3 && settings.RangeYears != 5)
            settings.RangeYears = 1;
    }

    /// <summary>
    /// Adopts a Windows installation's settings the first time the Linux module
    /// runs. The original file is only ever copied, and an explicit path can be
    /// supplied through the LITTLETOOLS_STOCK_DATA environment variable, matching
    /// the todo module's LITTLETOOLS_TODO_DATA escape hatch.
    /// </summary>
    private void ImportLegacyFileIfNeeded()
    {
        if (File.Exists(DataPath)) return;
        foreach (var candidate in LegacyCandidates(_directory))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                Directory.CreateDirectory(_directory);
                File.Copy(candidate, DataPath, false);
                ImportedFrom = candidate;
                return;
            }
            catch
            {
                // Try the next candidate; an unreadable source is not fatal.
            }
        }
    }

    /// <summary>
    /// Locations that may hold a Windows stock settings folder copied onto this
    /// machine. Exposed for tests so the search order can be asserted.
    /// </summary>
    internal static IEnumerable<string> LegacyCandidates(string targetDirectory)
    {
        var explicitPath = Environment.GetEnvironmentVariable("LITTLETOOLS_STOCK_DATA");
        if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath;

        if (OperatingSystem.IsWindows()) yield break;

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                dataHome = Path.Combine(home, ".local", "share");
        }

        if (string.IsNullOrWhiteSpace(dataHome)) yield break;
        yield return Path.Combine(dataHome, "LittleTools", "StockMonitor", FileName);
        var parent = Path.GetDirectoryName(Path.GetFullPath(targetDirectory));
        if (!string.IsNullOrWhiteSpace(parent))
            yield return Path.Combine(parent, "LittleTools", "StockMonitor", FileName);
    }
}
