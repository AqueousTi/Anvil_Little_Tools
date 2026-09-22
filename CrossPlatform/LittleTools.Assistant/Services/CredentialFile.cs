using System.Text.Json;

namespace LittleTools.Assistant.Services;

/// <summary>
/// The durable secret store under <c>AppPaths.CredentialsPath</c>.
///
/// Linux only. Windows keeps using the current-user DPAPI blob in
/// <c>assistant-settings.json</c>. The file is written with mode 0600 (and its
/// directory 0700) and is deliberately kept out of the program directory so the
/// one-shot updater's <c>rm -rf</c> cannot take the user's API keys with it.
/// </summary>
internal sealed class CredentialFile(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path = path ?? AppPaths.CredentialsPath;

    public string Path => _path;

    public string? Read(string provider)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), Options);
            return values is not null && values.TryGetValue(provider, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Write(string provider, string secret)
    {
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        Restrict(directory, isDirectory: true);
        var values = ReadAll();
        values[provider] = secret;
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(values, Options));
        Restrict(temporary, isDirectory: false);
        File.Move(temporary, _path, true);
        Restrict(_path, isDirectory: false);
    }

    /// <summary>
    /// True when the config directory can be created and written, i.e. when the
    /// fallback store is usable. Used to decide whether the settings dialog may
    /// accept a key at all.
    /// </summary>
    public bool CanWrite
    {
        get
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path)!;
                Directory.CreateDirectory(directory);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), Options)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void Restrict(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            var mode = isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // A filesystem without POSIX modes (or a race with another writer) only
            // costs the extra protection, never the credential itself.
        }
    }
}
