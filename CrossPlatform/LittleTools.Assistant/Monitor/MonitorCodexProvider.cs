using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LittleTools.Assistant.Monitor;

/// <summary>How the Codex app-server can be reached, for diagnostics and the state line.</summary>
internal enum CodexAvailability
{
    /// <summary>No executable, nothing to query: the Windows "Codex 未运行" branch.</summary>
    None,

    /// <summary>An app-server executable exists but the desktop app is not running (Linux only).</summary>
    CliOnly,

    /// <summary>The ChatGPT/Codex desktop app is running, which is the Windows condition.</summary>
    DesktopRunning
}

/// <summary>The app-server seam, so the render smoke can replay a recorded answer offline.</summary>
internal interface ICodexAppServer
{
    CodexAvailability Availability { get; }

    /// <summary>What was found, for <c>--diagnose</c> and the smoke report.</summary>
    string Describe();

    Task<CodexUsage> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads the official ChatGPT/Codex rate limits through <c>codex app-server</c>,
/// ported from the Windows <c>CodexProvider</c> (AIUsageMonitor/Program.cs
/// L591-L756).
///
/// The handshake is identical: spawn <c>app-server --stdio</c>, send
/// <c>initialize</c>, wait for its reply, send <c>initialized</c> and
/// <c>account/rateLimits/read</c>, then parse <c>result.rateLimits</c> with a 15
/// second deadline and kill the child (L657-L701). Nothing is read from
/// <c>auth.json</c>; authentication stays inside the official process.
///
/// Linux deviation, deliberate: Windows gated the whole query on
/// <c>IsDesktopRunning()</c> because <c>codex.exe</c> only ever shipped inside the
/// ChatGPT app (L593-L608). On Linux the same app-server is also a standalone
/// install (<c>codex app-server</c>), so the gate here asks whether an app-server
/// executable exists at all (<see cref="CodexAvailability.CliOnly"/>); a
/// CLI-only machine therefore gets real data instead of a permanent "Codex 未运行",
/// which is what the Windows branch would produce. The state line still uses the
/// Windows wording when neither is found.
/// </summary>
internal sealed class SystemCodexAppServer : ICodexAppServer
{
    /// <summary>
    /// Linux side addition: point straight at an app-server binary. Needed for
    /// verification and for a desktop bundle that is not on PATH, and it is the
    /// only way to exercise the real protocol from a test.
    /// </summary>
    public const string ExecutableVariable = "LITTLETOOLS_CODEX_BIN";

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    public CodexAvailability Availability => Classify(SystemProcessPaths(), FindExecutable());

    public string Describe()
    {
        var executable = FindExecutable();
        var availability = Availability;
        return availability switch
        {
            CodexAvailability.DesktopRunning => "desktop-running:" + (executable ?? "bundled"),
            CodexAvailability.CliOnly => "cli:" + executable,
            _ => "none"
        };
    }

    /// <summary>Windows IsDesktopRunning (Program.cs L593-L608), plus the Linux install paths.</summary>
    internal static bool IsDesktopRunning(IEnumerable<string> processPaths)
    {
        foreach (var path in processPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (OperatingSystem.IsWindows())
            {
                // Windows compares the packaged app path exactly.
                if (path.Contains("\\WindowsApps\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith("\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                    return true;
                continue;
            }

            // Linux: the desktop app is an Electron bundle at <root>/ChatGPT with
            // its app-server at <root>/resources/codex (the direct counterpart of
            // the Windows path above).
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("codex-launcher", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The availability decision, split out so it can be asserted without processes.</summary>
    internal static CodexAvailability Classify(IEnumerable<string> processPaths, string? executable)
    {
        if (IsDesktopRunning(processPaths)) return CodexAvailability.DesktopRunning;
        return executable is null ? CodexAvailability.None : CodexAvailability.CliOnly;
    }

    /// <summary>Executable paths of the desktop app processes, or nothing when it is not running.</summary>
    internal static IEnumerable<string> SystemProcessPaths()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("ChatGPT");
        }
        catch
        {
            yield break;
        }
        foreach (var process in processes)
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // A process owned by another user has no readable module on Linux.
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
            if (!string.IsNullOrWhiteSpace(path)) yield return path!;
        }
    }

    /// <summary>
    /// Windows FindCodexExecutable (Program.cs L737-L755): the sandbox copy first,
    /// then PATH. On Linux the same search uses the <c>codex</c> name and adds the
    /// known desktop bundle locations and the LITTLETOOLS_CODEX_BIN override.
    /// </summary>
    internal static string? FindExecutable()
    {
        var overridden = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            var candidate = overridden.Trim();
            if (File.Exists(candidate)) return candidate;
        }

        var fileName = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sandboxCopy = Path.Combine(userProfile, ".codex", ".sandbox-bin", fileName);
        if (File.Exists(sandboxCopy)) return sandboxCopy;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            try
            {
                var candidate = Path.Combine(folder.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // An invalid PATH entry is skipped, exactly like Windows.
            }
        }

        foreach (var candidate in BundleCandidates(userProfile))
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>Linux: the ChatGPT desktop bundle ships the same app-server binary.</summary>
    private static IEnumerable<string> BundleCandidates(string userProfile)
    {
        if (OperatingSystem.IsWindows()) yield break;
        yield return "/usr/lib/chatgpt/resources/codex";
        yield return "/usr/share/chatgpt/resources/codex";
        yield return "/opt/ChatGPT/resources/codex";
        if (!string.IsNullOrWhiteSpace(userProfile))
            yield return Path.Combine(userProfile, ".local", "share", "chatgpt", "resources", "codex");
    }

    /// <summary>Windows CodexProvider.ReadAsync / Read (Program.cs L610-L702).</summary>
    public Task<CodexUsage> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    private static CodexUsage Read(CancellationToken cancellationToken)
    {
        var executable = FindExecutable();
        if (executable is null) throw new InvalidOperationException("Codex executable not found");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.BeginErrorReadLine();
        try
        {
            Send(process, new Dictionary<string, object?>
            {
                ["method"] = "initialize",
                ["id"] = 1,
                ["params"] = new Dictionary<string, object?>
                {
                    ["clientInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "ai_usage_monitor",
                        ["title"] = "AI Usage Monitor",
                        ["version"] = "0.1.0"
                    }
                }
            });

            var deadline = DateTime.UtcNow.Add(Deadline);
            var initialized = false;
            var lineTask = process.StandardOutput.ReadLineAsync();
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lineTask.Wait(500)) continue;
                var line = lineTask.Result;
                if (line is null) break;
                var message = MonitorPayload.ParseObject(line);
                var id = MonitorPayload.Int(message, "id", -1);
                if (id == 1 && !initialized)
                {
                    initialized = true;
                    Send(process, new Dictionary<string, object?>
                    {
                        ["method"] = "initialized",
                        ["params"] = new Dictionary<string, object?>()
                    });
                    Send(process, new Dictionary<string, object?>
                    {
                        ["method"] = "account/rateLimits/read",
                        ["id"] = 2
                    });
                }
                else if (id == 2)
                {
                    return ParseMessage(line);
                }
                lineTask = process.StandardOutput.ReadLineAsync();
            }
            throw new TimeoutException("Codex app-server timeout");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch
            {
                // The child may have exited between the check and the kill.
            }
        }
    }

    private static void Send(Process process, Dictionary<string, object?> message)
    {
        process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
        process.StandardInput.Flush();
    }

    /// <summary>
    /// One app-server reply: an <c>error</c> member becomes an exception, a
    /// <c>result</c> member becomes the usage. Shared by the live reader and the
    /// recorded replay so both walk the same branch (Windows Program.cs L682-L688).
    /// </summary>
    internal static CodexUsage ParseMessage(string line)
    {
        var message = MonitorPayload.ParseObject(line);
        var error = MonitorPayload.Object(message, "error");
        if (error is not null)
            throw new InvalidOperationException(
                MonitorPayload.String(error.Value, "message") ?? "Codex query failed");
        return ParseUsage(MonitorPayload.Object(message, "result"));
    }

    /// <summary>Windows CodexProvider.ParseUsage (Program.cs L710-L723).</summary>
    internal static CodexUsage ParseUsage(JsonElement? result)
    {
        if (result is null) throw new InvalidOperationException("Codex returned no data");
        var limits = MonitorPayload.Object(result, "rateLimits");
        if (limits is null) throw new InvalidOperationException("Codex returned no rate limits");
        var usage = new CodexUsage
        {
            Primary = ParseWindow(MonitorPayload.Object(limits, "primary")),
            Secondary = ParseWindow(MonitorPayload.Object(limits, "secondary")),
            PlanType = MonitorPayload.String(limits.Value, "planType")
        };
        var credits = MonitorPayload.Object(limits, "credits");
        if (credits is not null && !MonitorPayload.Bool(credits, "unlimited", false))
            usage.Credits = MonitorPayload.String(credits.Value, "balance");
        return usage;
    }

    /// <summary>Windows CodexProvider.ParseWindow (Program.cs L725-L735).</summary>
    internal static RateWindow? ParseWindow(JsonElement? value)
    {
        if (value is null) return null;
        var window = new RateWindow
        {
            UsedPercent = MonitorPayload.Double(value, "usedPercent", 0),
            WindowDurationMins = MonitorPayload.Int(value, "windowDurationMins", 0)
        };
        var timestamp = MonitorPayload.Long(value, "resetsAt", 0);
        if (timestamp > 0)
            window.ResetsAt = JavaScriptSerializerDateConverter.FromUnixMilliseconds(timestamp * 1000);
        return window;
    }
}

/// <summary>
/// Minimal JSON field access with the same coercions the Windows <c>Json</c>
/// helpers used (AIUsageMonitor/Program.cs L1393-L1432): numbers may arrive as
/// strings, a missing or null member is a fallback rather than an error.
/// </summary>
internal static class MonitorPayload
{
    internal static JsonElement? ParseObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object ? root : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonElement? Object(JsonElement? source, string key)
    {
        if (source is not { } value) return null;
        if (!value.TryGetProperty(key, out var member)) return null;
        return member.ValueKind == JsonValueKind.Object ? member : null;
    }

    /// <summary>The members of an array member, or null when it is absent or not an array.</summary>
    internal static List<JsonElement>? Array(JsonElement? source, string key)
    {
        if (source is not { } value || !value.TryGetProperty(key, out var member)) return null;
        return member.ValueKind == JsonValueKind.Array ? member.EnumerateArray().ToList() : null;
    }

    internal static double? OptionalDouble(JsonElement? source, string key) =>
        TryNumber(source, key, out var value) ? value : null;

    internal static bool? OptionalBool(JsonElement? source, string key) =>
        source is { } value && value.TryGetProperty(key, out var member)
            && member.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? member.GetBoolean()
            : null;

    /// <summary>Windows Json.GetDouble (Program.cs L1424-L1427): a number or a numeric string.</summary>
    private static bool TryNumber(JsonElement? source, string key, out double value)
    {
        value = 0;
        if (source is not { } value0 || !value0.TryGetProperty(key, out var member)) return false;
        return member.ValueKind switch
        {
            JsonValueKind.Number => member.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(member.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    internal static string? String(JsonElement? source, string key)
    {
        if (source is not { } element || !element.TryGetProperty(key, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => null
        };
    }

    internal static int Int(JsonElement? source, string key, int fallback)
    {
        var text = Text(source, key);
        return int.TryParse(text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    internal static long Long(JsonElement? source, string key, long fallback)
    {
        var text = Text(source, key);
        return long.TryParse(text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    internal static double Double(JsonElement? source, string key, double fallback)
    {
        var text = Text(source, key);
        return double.TryParse(text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    internal static bool Bool(JsonElement? source, string key, bool fallback)
    {
        var text = Text(source, key);
        return bool.TryParse(text, out var value) ? value : fallback;
    }

    /// <summary>The raw text of a member, so both a JSON string and a number coerce the same way.</summary>
    private static string? Text(JsonElement? source, string key)
    {
        if (source is not { } value || !value.TryGetProperty(key, out var member)) return null;
        return member.ValueKind switch
        {
            JsonValueKind.String => member.GetString(),
            JsonValueKind.Number => member.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}
