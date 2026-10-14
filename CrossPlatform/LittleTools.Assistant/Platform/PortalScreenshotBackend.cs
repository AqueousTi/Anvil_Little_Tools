using Avalonia;
using SkiaSharp;
using Tmds.DBus.Protocol;

namespace LittleTools.Assistant.Platform;

/// <summary>
/// The <c>org.freedesktop.portal.Screenshot</c> backend.
///
/// The race this class exists to avoid: the portal answers a request by emitting
/// <c>org.freedesktop.portal.Request.Response</c> on a request object path, and on
/// a fast desktop that signal can arrive before the <c>Screenshot</c> call even
/// returns. A backend that calls first and subscribes afterwards loses the
/// response and reports a timeout. The fix is the standard portal handshake: pass
/// our own <c>handle_token</c>, compute the request path up front
/// (<see cref="ScreenshotBackendProtocol.BuildRequestPath"/>), and
/// <see cref="DBusConnection.WatchSignalAsync">subscribe</see> to that exact path
/// before sending the request.
///
/// Note on flashing: on GNOME 46 the portal implementation
/// (<c>xdg-desktop-portal-gnome</c> <c>src/screenshotdialog.c</c>) hard-codes
/// <c>flash=TRUE</c> when it forwards to <c>org.gnome.Shell.Screenshot</c>, so this
/// backend flashes like <c>gnome-screenshot</c> does. It stays in the chain as the
/// official, portable fallback, and because a desktop whose portal does not flash
/// gets a silent capture from it. The flash-free path on GNOME is
/// <see cref="GnomeShellScreenshotBackend"/>, which runs first.
/// </summary>
internal sealed class PortalScreenshotBackend : IDisposable
{
    private readonly SessionBusConnection _bus = new();

    /// <summary>The response signal body: <c>(ua{sv})</c>.</summary>
    private readonly record struct PortalResponse(uint Code, string? Uri);

    /// <summary>
    /// Takes a whole-desktop capture through the portal and decodes it. The file
    /// the portal writes into the user's Pictures directory is removed again once
    /// its bytes have been read; the suite only needs the pixels, and leaving one
    /// numbered screenshot per translation in the user's folder is pollution.
    /// </summary>
    public async Task<(SKBitmap? Bitmap, string? Failure)> CaptureAsync(
        PixelRect desktop, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScreenshotBackendProtocol.PortalTimeout);
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
            return (null, "portal：" + Describe(exception));
        }
    }

    private async Task<(SKBitmap? Bitmap, string? Failure)> CaptureCoreAsync(
        PixelRect desktop, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        var connection = await _bus.GetAsync(timeoutToken).ConfigureAwait(false);
        if (connection is null) return (null, "portal：会话 D-Bus 不可用");

        var uniqueName = connection.UniqueName;
        if (string.IsNullOrEmpty(uniqueName)) return (null, "portal：D-Bus 连接没有唯一名");

        var token = ScreenshotBackendProtocol.NewHandleToken(Guid.NewGuid());
        var requestPath = ScreenshotBackendProtocol.BuildRequestPath(uniqueName, token);
        var completion = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new List<IDisposable>();
        try
        {
            // Subscribe first: the response must never beat the subscription.
            subscriptions.Add(await WatchAsync(connection, requestPath, completion).ConfigureAwait(false));

            var handle = await connection.CallMethodAsync(
                CreateScreenshotMessage(connection, token),
                static (Message reply, object? state) => reply.GetBodyReader().ReadObjectPathAsString(),
                null).WaitAsync(timeoutToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(handle) && !string.Equals(handle, requestPath, StringComparison.Ordinal))
            {
                // The portal chose a different request path than the one derived
                // from our unique name. The pre-computed path is authoritative when
                // handle_token is honoured, but a slow answer on the reported path
                // is still worth catching.
                subscriptions.Add(await WatchAsync(connection, handle, completion).ConfigureAwait(false));
            }

            PortalResponse response;
            try
            {
                response = await completion.Task.WaitAsync(timeoutToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, $"portal：{ScreenshotBackendProtocol.PortalTimeout.TotalSeconds:0} 秒内没有收到 Response（可能被权限对话框挡住）");
            }

            var described = ScreenshotBackendProtocol.DescribeResponse(response.Code);
            if (described is not null) return (null, "portal：" + described);
            if (!ScreenshotBackendProtocol.TryDecodeFileUri(response.Uri, out var path))
                return (null, "portal：响应里没有可用的文件 URI");

            return await ReadCaptureAsync(path, desktop, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                try { subscription.Dispose(); } catch { }
            }
        }
    }

    private static async Task<(SKBitmap? Bitmap, string? Failure)> ReadCaptureAsync(
        string path, PixelRect desktop, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return (null, "portal：结果文件不存在（" + path + "）");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is not { Width: > 0, Height: > 0 })
            {
                bitmap?.Dispose();
                return (null, "portal：结果不是可解码的位图");
            }
            if (!ScreenshotBackendProtocol.IsPlausibleDesktopCapture(new PixelSize(bitmap.Width, bitmap.Height), desktop))
            {
                var size = bitmap.Width + "x" + bitmap.Height;
                bitmap.Dispose();
                return (null, "portal：截图尺寸 " + size + " 与桌面 " + desktop.Width + "x" + desktop.Height + " 不成比例");
            }
            return (bitmap, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return (null, "portal：读取结果失败：" + exception.Message);
        }
        finally
        {
            // The portal creates a fresh numbered file per request and the suite
            // has its own copy in memory now; remove it so the user's Pictures
            // directory does not collect one screenshot per translation.
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static async Task<IDisposable> WatchAsync(
        DBusConnection connection, string path, TaskCompletionSource<PortalResponse> completion)
    {
        return await connection.WatchSignalAsync(
            sender: ScreenshotBackendProtocol.PortalServiceName,
            path: path,
            @interface: ScreenshotBackendProtocol.PortalRequestInterface,
            signal: ScreenshotBackendProtocol.PortalResponseSignal,
            reader: static (Message message, object? state) => ReadResponse(message),
            handler: (Notification<PortalResponse> notification) =>
            {
                if (notification.HasValue) completion.TrySetResult(notification.Value);
            },
            flags: ObserverFlags.None,
            emitOnCapturedContext: false,
            state: null).ConfigureAwait(false);
    }

    private static PortalResponse ReadResponse(Message message)
    {
        var reader = message.GetBodyReader();
        var code = reader.ReadUInt32();
        string? uri = null;
        var results = reader.ReadDictionaryOfStringToVariantValue();
        if (results is not null && results.TryGetValue("uri", out var value))
        {
            try { uri = value.GetString(); } catch { uri = null; }
        }
        return new PortalResponse(code, uri);
    }

    private static MessageBuffer CreateScreenshotMessage(DBusConnection connection, string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: ScreenshotBackendProtocol.PortalServiceName,
            path: ScreenshotBackendProtocol.PortalObjectPath,
            @interface: ScreenshotBackendProtocol.PortalScreenshotInterface,
            member: "Screenshot",
            signature: "sa{sv}",
            flags: MessageFlags.None);
        // An empty parent window means "not a sandboxed application"; measured to
        // be accepted by xdg-desktop-portal 1.18.4 without a permission dialog.
        writer.WriteString(string.Empty);
        var dictionary = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("handle_token");
        writer.WriteVariantString(token);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("interactive");
        writer.WriteVariantBool(false);
        writer.WriteDictionaryEnd(dictionary);
        return writer.CreateMessage();
    }

    private static string Describe(Exception exception) =>
        exception is DBusErrorReplyException reply ? reply.ErrorName + "：" + reply.ErrorMessage : exception.Message;

    public void Dispose() => _bus.Dispose();
}
