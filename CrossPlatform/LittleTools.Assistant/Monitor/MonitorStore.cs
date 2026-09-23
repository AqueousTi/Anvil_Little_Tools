using System.Text;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// Persistence for the monitor's four shared files, ported from the Windows
/// module's stores (AIUsageMonitor/Program.cs): <c>providers.json</c>
/// (ProviderSettingsStore L116-L131), <c>snapshot.json</c> (UsageSnapshotCache
/// L176-L209), <c>usage-history.json</c> (DailyUsageTracker L1157-L1265) and
/// <c>glm-usage-history.json</c> (GlmUsageTracker L1276-L1391). All four are
/// written with <c>AtomicFile.WriteUtf8</c>: a temporary file in the same
/// directory, then a replace.
///
/// The Windows folder is <c>%LOCALAPPDATA%\LittleTools\AIUsageMonitor</c>; the
/// Linux folder is <c>XDG_DATA_HOME/little-tools/monitor</c> (AppPaths), and the
/// Windows files are adopted once on first run, like the todo and stock modules.
/// </summary>
internal sealed class MonitorStore
{
    /// <summary>
    /// Linux side addition: an explicit source to import from. It may be the
    /// Windows folder itself, or any one of the four files inside it. This is the
    /// monitor's counterpart to LITTLETOOLS_TODO_DATA and LITTLETOOLS_STOCK_DATA.
    /// </summary>
    public const string DirectoryVariable = "LITTLETOOLS_MONITOR_DATA";

    internal const string ProvidersFile = "providers.json";
    internal const string SnapshotFile = "snapshot.json";
    internal const string UsageHistoryFile = "usage-history.json";
    internal const string GlmHistoryFile = "glm-usage-history.json";

    private static readonly string[] SharedFiles = [ProvidersFile, SnapshotFile, UsageHistoryFile, GlmHistoryFile];
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly string _directory;

    public MonitorStore(string? directory = null) => _directory = directory ?? AppPaths.MonitorDirectory;

    public string Directory => _directory;

    public string ProvidersPath => Path.Combine(_directory, ProvidersFile);

    public string SnapshotPath => Path.Combine(_directory, SnapshotFile);

    public string UsageHistoryPath => Path.Combine(_directory, UsageHistoryFile);

    public string GlmHistoryPath => Path.Combine(_directory, GlmHistoryFile);

    /// <summary>Every source file that was adopted from a Windows installation.</summary>
    public List<string> ImportedFrom { get; } = [];

    /// <summary>Set when a file existed but could not be parsed.</summary>
    public string? LoadWarning { get; private set; }

    public void ImportLegacyFilesIfNeeded()
    {
        var candidates = LegacyCandidates(_directory).ToArray();
        foreach (var name in SharedFiles)
        {
            if (File.Exists(Path.Combine(_directory, name))) continue;
            foreach (var source in CandidatesFor(candidates, name))
            {
                try
                {
                    if (!File.Exists(source)) continue;
                    System.IO.Directory.CreateDirectory(_directory);
                    File.Copy(source, Path.Combine(_directory, name), false);
                    ImportedFrom.Add(source);
                    break;
                }
                catch
                {
                    // Try the next candidate; an unreadable source is not fatal.
                }
            }
        }
    }

    private static IEnumerable<string> CandidatesFor(IEnumerable<string> candidates, string name) =>
        candidates.Select(directory => Path.Combine(directory, name));

    /// <summary>
    /// Directories that may hold a Windows monitor folder copied onto this machine.
    /// The order is: the explicit override, the Windows <c>LittleTools</c> folder
    /// under the XDG data home, the sibling of the Linux module directory (so a
    /// whole <c>~/.local/share/LittleTools</c> copied in works), and the older
    /// Windows <c>%LOCALAPPDATA%\AIUsageMonitor</c> path that the DeepSeek history
    /// migration also honoured (Windows Program.cs L1166). Exposed for tests.
    /// </summary>
    internal static IEnumerable<string> LegacyCandidates(string targetDirectory)
    {
        foreach (var directory in ExplicitDirectories()) yield return directory;

        if (OperatingSystem.IsWindows()) yield break;

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home)) dataHome = Path.Combine(home, ".local", "share");
        }

        if (!string.IsNullOrWhiteSpace(dataHome))
        {
            yield return Path.Combine(dataHome, "LittleTools", "AIUsageMonitor");
            yield return Path.Combine(dataHome, "AIUsageMonitor");
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(targetDirectory));
        if (!string.IsNullOrWhiteSpace(parent))
        {
            yield return Path.Combine(parent, "LittleTools", "AIUsageMonitor");
            yield return Path.Combine(parent, "AIUsageMonitor");
        }
    }

    /// <summary>The directories named by LITTLETOOLS_MONITOR_DATA (a folder or a file inside one).</summary>
    private static IEnumerable<string> ExplicitDirectories()
    {
        var value = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(value)) yield break;
        var trimmed = value.Trim();
        if (System.IO.Directory.Exists(trimmed))
        {
            // A folder that directly contains the files wins; otherwise treat a
            // LittleTools folder as the parent of the AIUsageMonitor folder.
            yield return trimmed;
            yield return Path.Combine(trimmed, "AIUsageMonitor");
            yield break;
        }
        var parent = Path.GetDirectoryName(Path.GetFullPath(trimmed));
        if (!string.IsNullOrWhiteSpace(parent)) yield return parent;
    }

    // ------------------------------------------------------------- providers

    /// <summary>Windows ProviderSettingsStore.Load (Program.cs L118-L126).</summary>
    public ProviderSettings LoadProviders()
    {
        ImportLegacyFilesIfNeeded();
        try
        {
            if (File.Exists(ProvidersPath))
                return MonitorJson.Deserialize<ProviderSettings>(File.ReadAllText(ProvidersPath, Encoding.UTF8))
                    ?? new ProviderSettings();
        }
        catch
        {
            LoadWarning = "providers.json 无法解析，已改用默认供应商设置。";
        }
        return new ProviderSettings();
    }

    /// <summary>Windows ProviderSettingsStore.Save (Program.cs L128-L131).</summary>
    public bool SaveProviders(ProviderSettings settings) =>
        WriteAtomic(ProvidersPath, MonitorJson.Serialize(settings));

    // -------------------------------------------------------------- snapshot

    /// <summary>Windows UsageSnapshotCache.Load (Program.cs L183-L199).</summary>
    public UsageSnapshot? LoadSnapshot()
    {
        ImportLegacyFilesIfNeeded();
        try
        {
            if (!File.Exists(SnapshotPath)) return null;
            var state = MonitorJson.Deserialize<UsageSnapshot>(File.ReadAllText(SnapshotPath, Encoding.UTF8));
            if (state is null) return null;
            state.DeepSeekTodayPoints ??= [];
            state.DeepSeekWeekPoints ??= [];
            state.DeepSeekMonthPoints ??= [];
            state.GlmTodayPoints ??= [];
            state.GlmWeekPoints ??= [];
            state.GlmMonthPoints ??= [];
            return state;
        }
        catch
        {
            LoadWarning = "snapshot.json 无法解析，已改用新的额度状态。";
            return null;
        }
    }

    /// <summary>Windows UsageSnapshotCache.Save (Program.cs L201-L208).</summary>
    public bool SaveSnapshot(UsageSnapshot state) =>
        WriteAtomic(SnapshotPath, MonitorJson.Serialize(state));

    // -------------------------------------------------------------- history

    /// <summary>Windows DailyUsageTracker.Load (Program.cs L1248-L1256).</summary>
    public List<BalanceSample> LoadBalanceSamples()
    {
        ImportLegacyFilesIfNeeded();
        try
        {
            if (!File.Exists(UsageHistoryPath)) return [];
            return MonitorJson.Deserialize<List<BalanceSample>>(File.ReadAllText(UsageHistoryPath, Encoding.UTF8)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Windows DailyUsageTracker.Save (Program.cs L1258-L1265).</summary>
    public bool SaveBalanceSamples(List<BalanceSample> samples) =>
        WriteAtomic(UsageHistoryPath, MonitorJson.Serialize(samples));

    /// <summary>Windows GlmUsageTracker.Load (Program.cs L1376-L1384).</summary>
    public List<GlmSpendSample> LoadGlmSamples()
    {
        ImportLegacyFilesIfNeeded();
        try
        {
            if (!File.Exists(GlmHistoryPath)) return [];
            return MonitorJson.Deserialize<List<GlmSpendSample>>(File.ReadAllText(GlmHistoryPath, Encoding.UTF8)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Windows GlmUsageTracker.Save (Program.cs L1386-L1390).</summary>
    public bool SaveGlmSamples(List<GlmSpendSample> samples) =>
        WriteAtomic(GlmHistoryPath, MonitorJson.Serialize(samples));

    // ---------------------------------------------------------------- atomic

    /// <summary>Windows AtomicFile.WriteUtf8: temp file in the same directory, then replace.</summary>
    private bool WriteAtomic(string path, string content)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, Utf8WithoutBom);
            File.Move(temporary, path, true);
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
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
            catch
            {
                // A leftover .tmp file is harmless; the next save overwrites it.
            }
        }
    }
}
