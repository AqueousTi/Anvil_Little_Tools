using Avalonia;
using System.Text;

namespace LittleTools.Assistant.Platform;

/// <summary>
/// The pure part of the Linux capture chain: the D-Bus names/constants, the
/// portal request path arithmetic, the file URI decoding, the response codes and
/// the order in which the backends are tried.
///
/// None of this touches D-Bus, the display or the file system, so every rule the
/// capture chain relies on can be asserted by the unit tests
/// (<c>ScreenshotCaptureTests</c>) instead of only by a real desktop run.
///
/// Capture order and why (measured on GNOME 46, see the Linux screenshot docs):
///
/// 1. <see cref="BackendShell"/> - <c>org.gnome.Shell.Screenshot.Screenshot</c>
///    with <c>flash=false</c>. This is the only flash-free path on a GNOME
///    session. GNOME Shell only lets an allow-listed D-Bus name call it, and the
///    list contains <c>org.gnome.Screenshot</c>, so the backend leases that name
///    for the duration of one capture. It writes straight to our own temporary
///    file, so nothing is written into the user's Pictures directory.
/// 2. <see cref="BackendPortal"/> - the official
///    <c>org.freedesktop.portal.Screenshot</c> portal. It works without the name
///    lease and on Wayland, but on GNOME 46 the portal implementation hard-codes
///    <c>flash=TRUE</c> when it calls the shell, so it flashes exactly like
///    <c>gnome-screenshot</c> does. It is kept because it is the official,
///    portable interface and is still the right answer on desktops whose portal
///    does not flash.
/// 3. <see cref="BackendTools"/> - maim / scrot / import / grim / spectacle /
///    gnome-screenshot / xwd, in the historical silent-first order.
/// </summary>
internal static class ScreenshotBackendProtocol
{
    internal const string BackendShell = "shell";
    internal const string BackendPortal = "portal";
    internal const string BackendTools = "tools";

    /// <summary>
    /// Test-only override for the chain, matching the <c>LITTLETOOLS_FORCE_*</c>
    /// family used elsewhere. <c>shell</c>, <c>portal</c> and <c>tools</c> pin one
    /// backend so a harness can prove which one ran; unset (or <c>auto</c>) uses
    /// the production order.
    /// </summary>
    internal const string ForceBackendVariable = "LITTLETOOLS_FORCE_SCREENSHOT_BACKEND";

    // ---- org.freedesktop.portal.Screenshot -------------------------------

    internal const string PortalServiceName = "org.freedesktop.portal.Desktop";
    internal const string PortalObjectPath = "/org/freedesktop/portal/desktop";
    internal const string PortalScreenshotInterface = "org.freedesktop.portal.Screenshot";
    internal const string PortalRequestInterface = "org.freedesktop.portal.Request";
    internal const string PortalResponseSignal = "Response";
    internal const string PortalRequestRoot = "/org/freedesktop/portal/desktop/request/";

    // ---- org.gnome.Shell.Screenshot --------------------------------------

    internal const string ShellServiceName = "org.gnome.Shell.Screenshot";
    internal const string ShellObjectPath = "/org/gnome/Shell/Screenshot";
    internal const string ShellInterface = "org.gnome.Shell.Screenshot";

    /// <summary>
    /// The name GNOME Shell trusts for the screenshot interface. Its
    /// <c>DBusSenderChecker</c> allow list is
    /// <c>org.gnome.SettingsDaemon.MediaKeys</c>, the two portal implementations
    /// and this name (<c>js/misc/util.js</c> + <c>js/ui/screenshot.js</c> in
    /// gnome-shell 46), and it only checks that the caller's unique name currently
    /// owns one of them. That is why a plain call is denied while leasing this
    /// name is accepted.
    /// </summary>
    internal const string ShellTrustedName = "org.gnome.Screenshot";

    // ---- org.freedesktop.DBus --------------------------------------------

    internal const string BusServiceName = "org.freedesktop.DBus";
    internal const string BusObjectPath = "/org/freedesktop/DBus";
    internal const string BusInterface = "org.freedesktop.DBus";

    /// <summary>
    /// Longest a portal request may take. It has to cover the (possible)
    /// permission dialog and the shell's own capture, which took ~1.6s here.
    /// </summary>
    internal static readonly TimeSpan PortalTimeout = TimeSpan.FromSeconds(9);

    /// <summary>Longest a single shell screenshot call may take.</summary>
    internal static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Builds the request object path a portal request will be exported on, before
    /// the call is made, so the <c>Response</c> signal can be subscribed first.
    ///
    /// The portal replaces the leading colon of the caller's unique name with
    /// nothing and the dots with underscores, then appends the caller supplied
    /// <c>handle_token</c>: caller <c>:1.42</c> + token <c>lta1b2</c> becomes
    /// <c>/org/freedesktop/portal/desktop/request/1_42/lta1b2</c> (measured with
    /// dbus-monitor against xdg-desktop-portal 1.18.4).
    /// </summary>
    internal static string BuildRequestPath(string uniqueName, string handleToken)
    {
        var sender = uniqueName ?? string.Empty;
        if (sender.StartsWith(':')) sender = sender[1..];
        sender = sender.Replace('.', '_');
        return PortalRequestRoot + sender + "/" + handleToken;
    }

    /// <summary>
    /// A portal <c>handle_token</c> is one object path element, so only
    /// <c>[A-Za-z0-9_]</c> is allowed. A GUID's "N" form is 32 hex characters; the
    /// <c>lt</c> prefix makes the token recognisable in a bus log.
    /// </summary>
    internal static string NewHandleToken(Guid value) => "lt" + value.ToString("N");

    /// <summary>
    /// Decodes the <c>uri</c> member of a successful portal response
    /// (<c>file:///home/me/Pictures/Screenshot.png</c>) into a local path.
    /// Rejects anything that is not a local file URI. Percent escapes are decoded
    /// as UTF-8 bytes, so a localized file name such as
    /// <c>%E6%88%AA%E5%9B%BE-1.png</c> comes back as 截图-1.png.
    /// </summary>
    internal static bool TryDecodeFileUri(string? uri, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(uri)) return false;
        const string prefix = "file://";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var remainder = uri[prefix.Length..];
        if (!remainder.StartsWith('/'))
        {
            // file://localhost/home/... carries an authority; anything else is a
            // remote host and is not a local screenshot file.
            var slash = remainder.IndexOf('/');
            if (slash < 0) return false;
            var authority = remainder[..slash];
            if (!authority.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
            remainder = remainder[slash..];
        }

        var decoded = PercentDecode(remainder);
        if (decoded.Length == 0 || decoded[0] != '/') return false;
        path = decoded;
        return true;
    }

    /// <summary>
    /// RFC 3986 percent decoding. Unlike <see cref="Uri.UnescapeDataString"/> the
    /// escape bytes are collected first and decoded together as UTF-8, which is
    /// what makes a multi-byte localized file name come back intact. A lone
    /// <c>+</c> stays a <c>+</c>: URI escaping never means "space".
    /// </summary>
    internal static string PercentDecode(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '%' && index + 2 < value.Length
                && TryHex(value[index + 1], out var high) && TryHex(value[index + 2], out var low))
            {
                bytes.Add((byte)((high << 4) | low));
                index += 2;
                continue;
            }
            bytes.AddRange(Encoding.UTF8.GetBytes(current.ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool TryHex(char value, out int result)
    {
        if (value is >= '0' and <= '9') { result = value - '0'; return true; }
        if (value is >= 'a' and <= 'f') { result = value - 'a' + 10; return true; }
        if (value is >= 'A' and <= 'F') { result = value - 'A' + 10; return true; }
        result = 0;
        return false;
    }

    /// <summary>
    /// Null for the only successful code (0); a human readable failure otherwise.
    /// 1 is the user closing the dialog, anything else is a portal error.
    /// </summary>
    internal static string? DescribeResponse(uint response) => response switch
    {
        0 => null,
        1 => "portal 请求被取消（response=1）",
        _ => "portal 请求失败（response=" + response + "）"
    };

    /// <summary>
    /// Whether a decoded capture can plausibly be the desktop the selection
    /// coordinates are measured against.
    ///
    /// A portal or shell capture may be at a different pixel density than the
    /// reported screen bounds (HiDPI), and <c>MapToImage</c> already scales for
    /// that, so the rule is not "equal size" but "uniformly scaled": the two axis
    /// ratios must agree within 2% and lie between 0.25x and 4x. That accepts a
    /// 1.5x/2x HiDPI frame and rejects a thumbnail, a partial grab or a
    /// non-uniformly scaled picture before it can be cropped and translated.
    /// </summary>
    internal static bool IsPlausibleDesktopCapture(PixelSize image, PixelRect desktop)
    {
        if (image.Width <= 0 || image.Height <= 0 || desktop.Width <= 0 || desktop.Height <= 0) return false;
        var scaleX = image.Width / (double)desktop.Width;
        var scaleY = image.Height / (double)desktop.Height;
        if (scaleX < 0.25 || scaleY < 0.25 || scaleX > 4 || scaleY > 4) return false;
        var reference = Math.Max(scaleX, scaleY);
        return Math.Abs(scaleX - scaleY) / reference <= 0.02;
    }

    /// <summary>
    /// The backends to try, in order. <paramref name="forced"/> is the raw value of
    /// <see cref="ForceBackendVariable"/>; an unknown value falls back to the
    /// production order rather than failing a capture.
    /// </summary>
    internal static string[] SelectBackends(string? forced)
    {
        var value = forced?.Trim().ToLowerInvariant();
        return value switch
        {
            BackendShell => [BackendShell],
            BackendPortal => [BackendPortal],
            BackendTools => [BackendTools],
            _ => [BackendShell, BackendPortal, BackendTools]
        };
    }
}
