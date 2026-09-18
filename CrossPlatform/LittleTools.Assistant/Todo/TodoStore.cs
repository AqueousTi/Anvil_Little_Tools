using System.Globalization;
using System.Text;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Persistence for the todo data. Writes are atomic and keep one generation of
/// backup, mirroring the Windows module's <c>data.json</c> plus
/// <c>data.backup.json</c> pair.
/// </summary>
internal sealed class TodoStore
{
    private const string FileName = "data.json";
    private const string BackupName = "data.backup.json";

    private readonly string _directory;

    public TodoStore(string? directory = null) =>
        _directory = directory ?? AppPaths.TodoDirectory;

    public string DataPath => Path.Combine(_directory, FileName);

    public string BackupPath => Path.Combine(_directory, BackupName);

    /// <summary>Set when the data was adopted from a Windows installation.</summary>
    public string? ImportedFrom { get; private set; }

    /// <summary>Set when the primary file existed but could not be parsed.</summary>
    public string? LoadWarning { get; private set; }

    public DailyTodoData Load()
    {
        ImportLegacyFileIfNeeded();

        var primary = TryLoad(DataPath, out var primaryFailed);
        var data = primary ?? TryLoad(BackupPath, out _);
        if (data is null)
        {
            if (primaryFailed) LoadWarning = "data.json 无法解析，已改用备份或空数据。";
            data = new DailyTodoData();
        }
        else if (primary is null && File.Exists(BackupPath))
        {
            LoadWarning = "data.json 无法解析，已从 data.backup.json 恢复。";
        }

        Repair(data);
        return data;
    }

    public bool Save(DailyTodoData data)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var temporary = DataPath + ".tmp";
            File.WriteAllText(temporary, TodoJson.Serialize(data), new UTF8Encoding(false));

            if (File.Exists(DataPath))
            {
                try
                {
                    File.Replace(temporary, DataPath, BackupPath, true);
                }
                catch (PlatformNotSupportedException)
                {
                    FallbackReplace(temporary);
                }
                catch (IOException)
                {
                    FallbackReplace(temporary);
                }
            }
            else
            {
                File.Move(temporary, DataPath, true);
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            TryDelete(DataPath + ".tmp");
        }
    }

    private void FallbackReplace(string temporary)
    {
        // Keep the same two generation semantics when File.Replace is unavailable.
        File.Copy(DataPath, BackupPath, true);
        File.Move(temporary, DataPath, true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A leftover .tmp file is harmless; the next save overwrites it.
        }
    }

    private static DailyTodoData? TryLoad(string path, out bool failed)
    {
        failed = false;
        try
        {
            if (!File.Exists(path)) return null;
            var data = TodoJson.Deserialize(File.ReadAllText(path, Encoding.UTF8));
            if (data is null) failed = true;
            return data;
        }
        catch
        {
            failed = true;
            return null;
        }
    }

    /// <summary>
    /// Adopts a Windows installation's data file the first time the Linux module
    /// runs. The original file is copied, never modified, and an explicit path can
    /// be supplied through the LITTLETOOLS_TODO_DATA environment variable.
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
    /// Locations that may hold a Windows data folder copied onto this machine.
    /// Exposed for tests so the search order can be asserted.
    /// </summary>
    internal static IEnumerable<string> LegacyCandidates(string targetDirectory)
    {
        var explicitPath = Environment.GetEnvironmentVariable("LITTLETOOLS_TODO_DATA");
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
        yield return Path.Combine(dataHome, "LittleTools", "DailyTodo", FileName);
        var parent = Path.GetDirectoryName(Path.GetFullPath(targetDirectory));
        if (!string.IsNullOrWhiteSpace(parent))
            yield return Path.Combine(parent, "LittleTools", "DailyTodo", FileName);
    }

    /// <summary>
    /// Repairs a loaded file the same way the Windows module does, so partially
    /// written or hand edited data does not break the module.
    /// </summary>
    internal static void Repair(DailyTodoData data)
    {
        data.Days ??= [];
        data.BacklogItems ??= [];
        data.RecurringRules ??= [];

        data.Days.RemoveAll(day => day is null);
        data.RecurringRules.RemoveAll(rule => rule is null);
        data.BacklogItems.RemoveAll(item => item is null);

        foreach (var day in data.Days)
        {
            day.Items ??= [];
            day.ImportedSourceIds ??= [];
            day.SuppressedRuleIds ??= [];
            day.Items.RemoveAll(item => item is null);
            foreach (var item in day.Items)
            {
                if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                item.Text ??= string.Empty;
            }
        }

        foreach (var rule in data.RecurringRules)
        {
            if (string.IsNullOrEmpty(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");
            rule.Text ??= string.Empty;
            if (string.IsNullOrEmpty(rule.CreatedDate))
                rule.CreatedDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        foreach (var item in data.BacklogItems)
        {
            if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
            item.Text ??= string.Empty;
            if (string.IsNullOrEmpty(item.BacklogSourceDate))
                item.BacklogSourceDate = item.CreatedAt == default
                    ? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : item.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }
}
