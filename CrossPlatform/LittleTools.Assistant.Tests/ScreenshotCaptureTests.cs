using Avalonia;
using LittleTools.Assistant.Platform;
using SkiaSharp;

/// <summary>
/// Covers the Linux screen capture's pure parts: mapping the overlay's selection
/// onto the captured desktop and decoding the X11 dump used as a fallback. The
/// overlay itself is <see cref="SelectionWindow"/>, shared with Windows, and its
/// clipping is covered by <c>BaiduTests</c>.
/// </summary>
internal static class ScreenshotCaptureTests
{
    public static void Run(Action<string, bool> check)
    {
        var count = 0;
        void Check(string name, bool result)
        {
            count++;
            check(name, result);
        }

        CheckCaptureToolOrder(Check);
        CheckBackendOrder(Check);
        CheckPortalRequestPath(Check);
        CheckFileUriDecoding(Check);
        CheckPortalResponse(Check);
        CheckCapturePlausibility(Check);
        CheckRegionMapping(Check);
        CheckCrop(Check);
        CheckXwdDecode(Check);
        CheckBlankDetection(Check);
        Console.WriteLine($"SCREEN CAPTURE: {count} checks");
    }

    /// <summary>
    /// The backend chain is ordered silent-first: the flash-free GNOME shell call,
    /// then the official portal (which on GNOME 46 hard-codes flash=true and
    /// therefore flashes), then the tools. The force switch exists so a harness can
    /// pin one backend; an unknown value must never leave the chain empty.
    /// </summary>
    private static void CheckBackendOrder(Action<string, bool> check)
    {
        check("the backend chain is flash-free shell, then portal, then tools",
            ScreenshotBackendProtocol.SelectBackends(null)
                .SequenceEqual(["shell", "portal", "tools"]));
        check("the force switch pins the shell backend",
            ScreenshotBackendProtocol.SelectBackends("shell").SequenceEqual(["shell"]));
        check("the force switch pins the portal backend",
            ScreenshotBackendProtocol.SelectBackends("portal").SequenceEqual(["portal"]));
        check("the force switch pins the tool chain",
            ScreenshotBackendProtocol.SelectBackends("tools").SequenceEqual(["tools"]));
        check("the force switch is case and space insensitive",
            ScreenshotBackendProtocol.SelectBackends(" PORTAL ").SequenceEqual(["portal"]));
        check("an unknown force value falls back to the full chain",
            ScreenshotBackendProtocol.SelectBackends("nonsense")
                .SequenceEqual(["shell", "portal", "tools"]));
    }

    /// <summary>
    /// The request path must be computable before the call, otherwise the
    /// Response signal can be emitted before the subscription exists and the
    /// backend would time out on a capture that actually succeeded.
    /// </summary>
    private static void CheckPortalRequestPath(Action<string, bool> check)
    {
        check("the request path strips the colon and underscores the dots",
            ScreenshotBackendProtocol.BuildRequestPath(":1.42", "lta1b2")
            == "/org/freedesktop/portal/desktop/request/1_42/lta1b2");
        check("a unique name without a colon still works",
            ScreenshotBackendProtocol.BuildRequestPath("1.7", "tok")
            == "/org/freedesktop/portal/desktop/request/1_7/tok");

        var token = ScreenshotBackendProtocol.NewHandleToken(new Guid("0123456789abcdef0123456789abcdef"));
        check("the handle token is lt plus the 32 hex digits", token == "lt0123456789abcdef0123456789abcdef");
        check("the handle token is one valid object path element",
            token.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));
    }

    /// <summary>
    /// The portal returns the picture as a file URI; a localized file name arrives
    /// percent-encoded as UTF-8 bytes, which a naive per-character unescape turns
    /// into mojibake and which then reads the wrong (missing) file.
    /// </summary>
    private static void CheckFileUriDecoding(Action<string, bool> check)
    {
        check("a plain file URI decodes to its path",
            ScreenshotBackendProtocol.TryDecodeFileUri("file:///home/me/Pictures/Screenshot-4.png", out var plain)
            && plain == "/home/me/Pictures/Screenshot-4.png");
        check("percent escapes decode",
            ScreenshotBackendProtocol.TryDecodeFileUri("file:///home/me/My%20Pictures/shot.png", out var spaced)
            && spaced == "/home/me/My Pictures/shot.png");
        check("a UTF-8 percent-encoded name decodes to the real characters",
            ScreenshotBackendProtocol.TryDecodeFileUri("file:///home/me/%E6%88%AA%E5%9B%BE-1.png", out var localized)
            && localized == "/home/me/截图-1.png");
        check("file://localhost is a local file",
            ScreenshotBackendProtocol.TryDecodeFileUri("file://localhost/tmp/a.png", out var localhost)
            && localhost == "/tmp/a.png");
        check("a remote file URI is refused",
            !ScreenshotBackendProtocol.TryDecodeFileUri("file://server/share/a.png", out _));
        check("a non-file URI is refused",
            !ScreenshotBackendProtocol.TryDecodeFileUri("https://example.com/a.png", out _));
        check("a relative file URI is refused",
            !ScreenshotBackendProtocol.TryDecodeFileUri("file://a.png", out _));
        check("null and empty URIs are refused",
            !ScreenshotBackendProtocol.TryDecodeFileUri(null, out _)
            && !ScreenshotBackendProtocol.TryDecodeFileUri("", out _));
        check("a plus in a URI is not a space",
            ScreenshotBackendProtocol.PercentDecode("a+b") == "a+b");
        check("a broken escape is kept literally",
            ScreenshotBackendProtocol.PercentDecode("100%zz") == "100%zz"
            && ScreenshotBackendProtocol.PercentDecode("50%") == "50%");
    }

    /// <summary>
    /// response=0 is the only success. 1 is the user cancelling; anything else is a
    /// portal error. Both must be a failure so the chain falls through instead of
    /// cropping a response that has no picture.
    /// </summary>
    private static void CheckPortalResponse(Action<string, bool> check)
    {
        check("response 0 is success", ScreenshotBackendProtocol.DescribeResponse(0) is null);
        check("response 1 (cancelled) is a failure",
            ScreenshotBackendProtocol.DescribeResponse(1) is { Length: > 0 });
        check("response 2 (other error) is a failure",
            ScreenshotBackendProtocol.DescribeResponse(2) is { Length: > 0 });
        check("an unknown response code is a failure",
            ScreenshotBackendProtocol.DescribeResponse(99) is { Length: > 0 });
    }

    /// <summary>
    /// A decoded capture has to be the desktop at some uniform scale. A thumbnail,
    /// a partial grab or a non-uniformly scaled picture must be refused before it
    /// is cropped, and a HiDPI frame at a different density must be accepted
    /// because MapToImage scales it.
    /// </summary>
    private static void CheckCapturePlausibility(Action<string, bool> check)
    {
        var desktop = new PixelRect(0, 0, 4480, 1440);
        check("an exact size capture is plausible",
            ScreenshotBackendProtocol.IsPlausibleDesktopCapture(new PixelSize(4480, 1440), desktop));
        check("a 2x HiDPI capture is plausible",
            ScreenshotBackendProtocol.IsPlausibleDesktopCapture(new PixelSize(8960, 2880), desktop));
        check("a 1.5x capture is plausible",
            ScreenshotBackendProtocol.IsPlausibleDesktopCapture(new PixelSize(6720, 2160), desktop));
        check("a 64x64 thumbnail is not plausible",
            !ScreenshotBackendProtocol.IsPlausibleDesktopCapture(new PixelSize(64, 64), desktop));
        check("a half-width capture is not plausible",
            !ScreenshotBackendProtocol.IsPlausibleDesktopCapture(new PixelSize(2240, 1440), desktop));
        check("an empty capture is not plausible",
            !ScreenshotBackendProtocol.IsPlausibleDesktopCapture(new PixelSize(0, 0), desktop));
        check("a capture of an empty desktop is not plausible",
            !ScreenshotBackendProtocol.IsPlausibleDesktopCapture(new PixelSize(100, 100), default));
    }

    /// <summary>
    /// The full-desktop capture chain must try the tools that do not flash first:
    /// gnome-screenshot always flashes on a GNOME session (GNOME Shell's Flashspot
    /// through its D-Bus backend, or its own CheeseFlash on the X11 fallback), so
    /// it may only be reached once every silent tool is missing.
    /// </summary>
    private static void CheckCaptureToolOrder(Action<string, bool> check)
    {
        var order = LinuxScreenshotService.CaptureToolOrder;
        check("the capture chain is silent tools first",
            order.SequenceEqual(["maim", "scrot", "import", "grim", "spectacle", "gnome-screenshot", "xwd"]));
        check("maim is part of the capture chain", order.Contains("maim"));
        check("gnome-screenshot is only tried before the raw xwd dump",
            order.Count == 7 && order[^1] == "xwd" && order[^2] == "gnome-screenshot");
    }

    private static void CheckRegionMapping(Action<string, bool> check)
    {
        var desktop = new PixelRect(0, 0, 4480, 1440);
        var image = new PixelSize(4480, 1440);

        check("selection maps one to one",
            LinuxScreenshotService.MapToImage(new PixelRect(100, 200, 300, 400), desktop, image)
            == new PixelRect(100, 200, 300, 400));

        // The second monitor of this workstation lives at x=2560, y=360.
        check("selection maps on a second monitor",
            LinuxScreenshotService.MapToImage(new PixelRect(2600, 500, 200, 100), desktop, image)
            == new PixelRect(2600, 500, 200, 100));

        // A layout whose leftmost monitor has a negative origin (allowed on
        // Windows-style virtual desktops) must not shift the crop.
        var shifted = new PixelRect(-1000, 100, 4480, 1440);
        check("selection maps from a negative desktop origin",
            LinuxScreenshotService.MapToImage(new PixelRect(-800, 300, 200, 200), shifted, image)
            == new PixelRect(200, 200, 200, 200));

        // A HiDPI capture whose pixel size differs from the reported bounds.
        check("selection scales with the capture",
            LinuxScreenshotService.MapToImage(new PixelRect(20, 40, 100, 100), desktop, new PixelSize(2240, 720))
            == new PixelRect(10, 20, 50, 50));

        check("selection clamps inside the capture",
            LinuxScreenshotService.MapToImage(new PixelRect(4400, 1400, 400, 400), desktop, image)
            == new PixelRect(4400, 1400, 80, 40));

        check("selection never leaves a negative capture corner",
            LinuxScreenshotService.MapToImage(new PixelRect(-50, -50, 100, 100), desktop, image)
            == new PixelRect(0, 0, 50, 50));

        check("an empty capture yields no region",
            LinuxScreenshotService.MapToImage(new PixelRect(0, 0, 10, 10), desktop, new PixelSize(0, 0))
            == default);
    }

    private static void CheckCrop(Action<string, bool> check)
    {
        using var desktop = new SKBitmap(new SKImageInfo(4, 3, SKColorType.Bgra8888, SKAlphaType.Opaque));
        desktop.SetPixel(1, 1, new SKColor(255, 0, 0));
        desktop.SetPixel(2, 1, new SKColor(0, 255, 0));
        desktop.SetPixel(0, 0, new SKColor(0, 0, 255));

        var bytes = LinuxScreenshotService.Crop(desktop, new PixelRect(1, 1, 2, 1), new PixelRect(0, 0, 4, 3));
        check("crop returns an encoded image", bytes is { Length: > 0 });
        using var cropped = SKBitmap.Decode(bytes);
        check("crop keeps the selected size", cropped is { Width: 2, Height: 1 });
        check("crop keeps the selected pixels",
            cropped.GetPixel(0, 0) == new SKColor(255, 0, 0) && cropped.GetPixel(1, 0) == new SKColor(0, 255, 0));

        check("a selection outside the capture crops to nothing",
            LinuxScreenshotService.Crop(desktop, new PixelRect(400, 400, 10, 10), new PixelRect(0, 0, 4, 3)) is null);
    }

    private static void CheckXwdDecode(Action<string, bool> check)
    {
        // A dump shaped like the one xwd 7.7 writes on this workstation: a
        // big-endian header that does NOT include the colour table (header_size
        // 107 while the payload starts at 3179), 24 bits per pixel padded to a
        // 4-byte stride, DirectColor with the standard channel masks.
        var pixels = new uint[]
        {
            0x000000FF, // blue
            0x0000FF00, // green
            0x00FF0000, // red
            0x00123456  // r=0x12 g=0x34 b=0x56
        };
        var dump = BuildXwd(4, 1, pixels);
        using var decoded = XwdImage.Decode(dump);
        check("xwd dump decodes to its size", decoded is { Width: 4, Height: 1 });
        check("xwd dump decodes the pixel bytes",
            decoded.GetPixel(0, 0) == new SKColor(0, 0, 255)
            && decoded.GetPixel(1, 0) == new SKColor(0, 255, 0)
            && decoded.GetPixel(2, 0) == new SKColor(255, 0, 0)
            && decoded.GetPixel(3, 0) == new SKColor(0x12, 0x34, 0x56));

        try
        {
            XwdImage.Decode(BuildXwd(2, 1, [0, 0], visualClass: 3, bitsPerPixel: 8, stride: 4));
            check("a palette xwd dump is rejected", false);
        }
        catch (InvalidOperationException exception)
        {
            check("a palette xwd dump is rejected", exception.Message.Contains("调色板", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The blank-root-window guard: xwd -root returns a black picture under a
    /// compositing window manager, and translating that would be worse than
    /// reporting that no usable capture tool was found.
    /// </summary>
    private static void CheckBlankDetection(Action<string, bool> check)
    {
        using var black = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Bgra8888, SKAlphaType.Opaque));
        check("an all black root dump is detected", LinuxScreenshotService.IsBlankRootWindow(black));

        // A single lit pixel in the sampled grid means the dump has real content.
        black.SetPixel(2, 1, new SKColor(0, 0, 1));
        check("a dump with content is not blank", !LinuxScreenshotService.IsBlankRootWindow(black));
    }

    /// <summary>Builds an XWD byte stream with the same layout xwd produces.</summary>
    private static byte[] BuildXwd(
        int width, int height, uint[] pixels, uint visualClass = 5, int bitsPerPixel = 24, int? stride = null)
    {
        var bytesPerLine = stride ?? width * 4;
        const int colourEntries = 256;
        // 100 bytes of struct + a window name + the colour table, mirroring the
        // real file where header_size stops before the colour table.
        var headerSize = 100 + 7 + colourEntries * 12;
        var header = new byte[headerSize + bytesPerLine * height];
        WriteUInt32(header, 0, 107);
        WriteUInt32(header, 4, 7);
        WriteUInt32(header, 8, 2);
        WriteUInt32(header, 12, (uint)bitsPerPixel);
        WriteUInt32(header, 16, (uint)width);
        WriteUInt32(header, 20, (uint)height);
        WriteUInt32(header, 28, 0); // LSBFirst payload
        WriteUInt32(header, 32, 32);
        WriteUInt32(header, 44, (uint)bitsPerPixel);
        WriteUInt32(header, 48, (uint)bytesPerLine);
        WriteUInt32(header, 52, visualClass);
        WriteUInt32(header, 56, 0x00FF0000);
        WriteUInt32(header, 60, 0x0000FF00);
        WriteUInt32(header, 64, 0x000000FF);
        WriteUInt32(header, 76, colourEntries);
        WriteUInt32(header, 80, (uint)width);
        WriteUInt32(header, 84, (uint)height);

        var bytesPerPixel = bitsPerPixel <= 8 ? 1 : bitsPerPixel <= 16 ? 2 : bitsPerPixel <= 24 && bytesPerLine < width * 4 ? 3 : 4;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = pixels[y * width + x];
                var offset = headerSize + y * bytesPerLine + x * bytesPerPixel;
                for (var b = 0; b < bytesPerPixel && b < 4; b++)
                    header[offset + b] = (byte)(value >> (8 * b));
            }
        }
        return header;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
