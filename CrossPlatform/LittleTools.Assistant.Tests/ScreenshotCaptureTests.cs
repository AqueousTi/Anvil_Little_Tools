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

        CheckRegionMapping(Check);
        CheckCrop(Check);
        CheckXwdDecode(Check);
        CheckBlankDetection(Check);
        Console.WriteLine($"SCREEN CAPTURE: {count} checks");
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
