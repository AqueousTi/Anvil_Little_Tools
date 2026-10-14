using LittleTools.Assistant.Services;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tmds.DBus.Protocol;

namespace LittleTools.Assistant.Platform;

/// <summary>
/// The last capture result, written next to the cache so <c>--diagnose</c> can
/// answer "which backend really ran?". A diagnose run is usually a separate
/// process from the one that captured (the suite is single-instance, and a
/// diagnose against a running copy is answered headlessly), so the answer has to
/// survive the capture process instead of living in a static field.
/// </summary>
internal static class ScreenshotCaptureLog
{
    private const string FileName = "last-screenshot.json";

    internal static string Path => System.IO.Path.Combine(AppPaths.CacheDirectory, FileName);

    internal sealed record Entry(
        [property: JsonPropertyName("backend")] string Backend,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc);

    public static void Record(string backend, string? detail)
    {
        try
        {
            var entry = new Entry(backend, detail, DateTimeOffset.UtcNow);
            File.WriteAllText(Path, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Diagnostics must never take a capture down.
        }
    }

    public static Entry? Read()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            return JsonSerializer.Deserialize<Entry>(File.ReadAllText(Path));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// The screenshot block of <c>--diagnose</c>: which backend the chain would try,
/// which one ran last, and whether the two D-Bus services the silent paths need
/// are reachable. Every probe is best effort and bounded, so a diagnose run on a
/// desktop without D-Bus still produces a file.
/// </summary>
internal static class ScreenshotDiagnostics
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public static object Describe()
    {
        var forced = Environment.GetEnvironmentVariable(ScreenshotBackendProtocol.ForceBackendVariable);
        var last = ScreenshotCaptureLog.Read();
        var bus = Probe();
        return new
        {
            forcedBackend = string.IsNullOrWhiteSpace(forced) ? null : forced,
            order = ScreenshotBackendProtocol.SelectBackends(forced),
            lastBackend = last?.Backend,
            lastBackendDetail = last?.Detail,
            lastBackendAtUtc = last?.AtUtc,
            lastBackendLog = ScreenshotCaptureLog.Path,
            sessionBus = bus.SessionBus,
            portalServiceOwned = bus.PortalOwned,
            portalServiceActivatable = bus.PortalActivatable,
            shellScreenshotServiceOwned = bus.ShellOwned,
            shellScreenshotServiceActivatable = bus.ShellActivatable,
            // False while a gnome-screenshot instance owns the name, in which case
            // the flash-free backend steps aside for that capture.
            trustedNameFree = bus.TrustedNameFree,
            portalFlashesOnGnome = true,
            tools = LinuxScreenshotService.CaptureToolOrder.ToDictionary(
                name => name, name => ExecutableLocator.Find(name) is not null)
        };
    }

    private readonly record struct BusProbe(
        bool SessionBus,
        bool PortalOwned,
        bool PortalActivatable,
        bool ShellOwned,
        bool ShellActivatable,
        bool TrustedNameFree);

    private static BusProbe Probe()
    {
        try
        {
            var task = Task.Run(ProbeAsync);
            if (!task.Wait(ProbeTimeout)) return default;
            return task.Result;
        }
        catch
        {
            return default;
        }
    }

    private static async Task<BusProbe> ProbeAsync()
    {
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address)) return default;
        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            var activatable = await connection.CallMethodAsync(
                CreateBusMessage(connection, "ListActivatableNames", null),
                static (Message reply, object? state) => reply.GetBodyReader().ReadArrayOfString(),
                null).ConfigureAwait(false) ?? [];
            var portalOwned = await NameHasOwnerAsync(connection, ScreenshotBackendProtocol.PortalServiceName).ConfigureAwait(false);
            var shellOwned = await NameHasOwnerAsync(connection, ScreenshotBackendProtocol.ShellServiceName).ConfigureAwait(false);
            var trustedOwned = await NameHasOwnerAsync(connection, ScreenshotBackendProtocol.ShellTrustedName).ConfigureAwait(false);
            return new BusProbe(
                SessionBus: true,
                PortalOwned: portalOwned,
                PortalActivatable: activatable.Contains(ScreenshotBackendProtocol.PortalServiceName),
                ShellOwned: shellOwned,
                ShellActivatable: activatable.Contains(ScreenshotBackendProtocol.ShellServiceName),
                TrustedNameFree: !trustedOwned);
        }
        catch
        {
            return default;
        }
        finally
        {
            connection.Dispose();
        }
    }

    private static Task<bool> NameHasOwnerAsync(DBusConnection connection, string name) =>
        connection.CallMethodAsync(
            CreateBusMessage(connection, "NameHasOwner", name),
            static (Message reply, object? state) => reply.GetBodyReader().ReadBool(),
            null);

    private static MessageBuffer CreateBusMessage(DBusConnection connection, string member, string? argument)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: ScreenshotBackendProtocol.BusServiceName,
            path: ScreenshotBackendProtocol.BusObjectPath,
            @interface: ScreenshotBackendProtocol.BusInterface,
            member: member,
            signature: argument is null ? string.Empty : "s",
            flags: MessageFlags.None);
        if (argument is not null) writer.WriteString(argument);
        return writer.CreateMessage();
    }
}
