using Avalonia;
using SkiaSharp;
using Tmds.DBus.Protocol;

namespace LittleTools.Assistant.Platform;

/// <summary>
/// The flash-free GNOME capture backend:
/// <c>org.gnome.Shell.Screenshot.Screenshot(include_cursor=false, flash=false, path)</c>.
///
/// GNOME Shell does not allow every caller to use that interface. Its
/// <c>DBusSenderChecker</c> only accepts a sender whose unique name currently owns
/// one of four well-known names - <c>org.gnome.SettingsDaemon.MediaKeys</c>, the
/// two portal implementations and <c>org.gnome.Screenshot</c> (gnome-shell 46,
/// <c>js/misc/util.js</c>). A plain call is therefore answered with
/// <c>AccessDenied: Screenshot is not allowed</c>, which is exactly what the
/// previous implementation ran into. Leasing <c>org.gnome.Screenshot</c> for the
/// duration of one capture satisfies the check without touching the shell's
/// configuration, and because this path is the only one that can pass
/// <c>flash=false</c>, it is the only flash-free capture on a GNOME session.
///
/// The name is held for the ~1.5s a capture takes and released in a
/// <see langword="finally"/> on every path, including failures; if the process
/// dies the bus releases it when the connection drops. A name that is already
/// owned (a running <c>gnome-screenshot</c>) is never waited for: the backend
/// answers "unavailable" immediately and the chain falls through.
/// </summary>
internal sealed class GnomeShellScreenshotBackend : IDisposable
{
    /// <summary>
    /// The shell accepts one screenshot operation per sender at a time, and the
    /// leased name is per connection, so two windows capturing at once would make
    /// the second call fail with "There is an ongoing operation for this sender".
    /// Captures are user driven, so serializing them costs nothing.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly SessionBusConnection _bus = new();

    public async Task<(SKBitmap? Bitmap, string? Failure)> CaptureAsync(
        PixelRect desktop, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScreenshotBackendProtocol.ShellTimeout);
        try
        {
            await Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "shell：等待上一次抓图结束超时");
        }

        try
        {
            return await CaptureCoreAsync(desktop, timeout.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (null, "shell：" + Describe(exception));
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<(SKBitmap? Bitmap, string? Failure)> CaptureCoreAsync(
        PixelRect desktop, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        var connection = await _bus.GetAsync(timeoutToken).ConfigureAwait(false);
        if (connection is null) return (null, "shell：会话 D-Bus 不可用");

        bool leased;
        try
        {
            leased = await connection.TryRequestNameAsync(
                ScreenshotBackendProtocol.ShellTrustedName, RequestNameOptions.None)
                .WaitAsync(timeoutToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return (null, "shell：申请 " + ScreenshotBackendProtocol.ShellTrustedName + " 名字失败：" + exception.Message);
        }
        if (!leased)
            return (null, "shell：" + ScreenshotBackendProtocol.ShellTrustedName + " 名字被占用（可能正在运行 gnome-screenshot）");

        var path = Path.Combine(Path.GetTempPath(), "little-tools-shell-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var failure = await CallScreenshotAsync(connection, path, timeoutToken).ConfigureAwait(false);
            if (failure is not null) return (null, "shell：" + failure);
            if (!File.Exists(path)) return (null, "shell：没有写出截图文件");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is not { Width: > 0, Height: > 0 })
            {
                bitmap?.Dispose();
                return (null, "shell：截图无法解码");
            }
            if (!ScreenshotBackendProtocol.IsPlausibleDesktopCapture(new PixelSize(bitmap.Width, bitmap.Height), desktop))
            {
                var size = bitmap.Width + "x" + bitmap.Height;
                bitmap.Dispose();
                return (null, "shell：截图尺寸 " + size + " 与桌面 " + desktop.Width + "x" + desktop.Height + " 不成比例");
            }
            return (bitmap, null);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            // Release immediately after the capture: while we own the name, a
            // PrintScreen that goes through it would reach this process instead.
            try { await connection.ReleaseNameAsync(ScreenshotBackendProtocol.ShellTrustedName).ConfigureAwait(false); }
            catch { }
        }
    }

    /// <summary>
    /// Sends the one call that matters and reads <c>(b success, s filename_used)</c>.
    /// The shell's name watcher learns about our new name owner asynchronously, so
    /// the very first call can still be refused with AccessDenied; that is retried
    /// a couple of times over ~100ms before giving up.
    /// </summary>
    private static async Task<string?> CallScreenshotAsync(
        DBusConnection connection, string path, CancellationToken timeoutToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (success, used) = await connection.CallMethodAsync(
                    CreateScreenshotMessage(connection, path),
                    static (Message reply, object? state) =>
                    {
                        var reader = reply.GetBodyReader();
                        return (Success: reader.ReadBool(), Used: reader.ReadString());
                    },
                    null).WaitAsync(timeoutToken).ConfigureAwait(false);
                if (success) return null;
                return "截图没有成功（文件名 " + used + "）";
            }
            catch (DBusErrorReplyException exception) when (
                attempt < 3 && exception.ErrorName.Contains("AccessDenied", StringComparison.Ordinal))
            {
                // The shell has not seen the name owner change yet.
                await Task.Delay(40, timeoutToken).ConfigureAwait(false);
            }
            catch (DBusErrorReplyException exception)
            {
                return exception.ErrorName + "：" + exception.ErrorMessage;
            }
        }
    }

    private static MessageBuffer CreateScreenshotMessage(DBusConnection connection, string path)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: ScreenshotBackendProtocol.ShellServiceName,
            path: ScreenshotBackendProtocol.ShellObjectPath,
            @interface: ScreenshotBackendProtocol.ShellInterface,
            member: "Screenshot",
            signature: "bbs",
            flags: MessageFlags.None);
        writer.WriteBool(false); // include_cursor
        writer.WriteBool(false); // flash: the reason this backend exists
        writer.WriteString(path);
        return writer.CreateMessage();
    }

    private static string Describe(Exception exception) =>
        exception is DBusErrorReplyException reply ? reply.ErrorName + "：" + reply.ErrorMessage : exception.Message;

    public void Dispose() => _bus.Dispose();
}
