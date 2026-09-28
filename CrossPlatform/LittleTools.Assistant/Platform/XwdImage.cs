using SkiaSharp;

namespace LittleTools.Assistant.Platform;

/// <summary>
/// Minimal reader for the X Window Dump format written by <c>xwd</c>.
///
/// It backs the X11-only fallback of the Linux screenshot service: <c>xwd</c> is a
/// non-interactive dump tool that ships with the X client utilities, so the screen
/// can be captured even when no desktop-specific screenshot tool is installed and
/// without ever handing the selection UI to the desktop (which is what made
/// <c>gnome-screenshot -a</c> hang, see <see cref="LinuxScreenshotService"/>).
///
/// Only the TrueColor/DirectColor layouts an X server actually serves today are
/// supported; a palette (8-bit PseudoColor) dump is rejected with a readable
/// message instead of producing a wrong picture.
/// </summary>
internal static class XwdImage
{
    /// <summary>25 big-endian CARD32 fields, see X11's XWDFile.h.</summary>
    private const int HeaderBytes = 25 * 4;

    private const int OffsetPixmapWidth = 16;
    private const int OffsetPixmapHeight = 20;
    private const int OffsetByteOrder = 28;
    private const int OffsetBitsPerPixel = 44;
    private const int OffsetBytesPerLine = 48;
    private const int OffsetVisualClass = 52;
    private const int OffsetRedMask = 56;
    private const int OffsetGreenMask = 60;
    private const int OffsetBlueMask = 64;

    /// <summary>TrueColor and DirectColor are the visual classes with usable channel masks.</summary>
    private const uint VisualClassTrueColor = 4;
    private const uint VisualClassDirectColor = 5;

    public static SKBitmap Decode(byte[] data)
    {
        if (data.Length < HeaderBytes)
            throw new InvalidOperationException("xwd 输出过短，不是有效的屏幕转储。");

        // The header is always written in the X protocol byte order (big endian);
        // only the pixel payload below follows the byte_order field.
        var width = (int)ReadUInt32(data, OffsetPixmapWidth);
        var height = (int)ReadUInt32(data, OffsetPixmapHeight);
        var byteOrder = ReadUInt32(data, OffsetByteOrder);
        var bitsPerPixel = (int)ReadUInt32(data, OffsetBitsPerPixel);
        var bytesPerLine = (int)ReadUInt32(data, OffsetBytesPerLine);
        var visualClass = ReadUInt32(data, OffsetVisualClass);
        var redMask = ReadUInt32(data, OffsetRedMask);
        var greenMask = ReadUInt32(data, OffsetGreenMask);
        var blueMask = ReadUInt32(data, OffsetBlueMask);

        if (width <= 0 || height <= 0 || bytesPerLine <= 0)
            throw new InvalidOperationException($"xwd 转储的尺寸无效（{width}x{height}，行字节 {bytesPerLine}）。");
        if (visualClass is not (VisualClassTrueColor or VisualClassDirectColor))
            throw new InvalidOperationException(
                $"xwd 输出是调色板格式（visual class {visualClass}，每像素 {bitsPerPixel} 位），无法直接解码。");

        var bytesPerPixel = BytesPerPixel(bitsPerPixel, bytesPerLine, width);
        if (bytesPerPixel == 0)
            throw new InvalidOperationException($"xwd 输出每像素 {bitsPerPixel} 位，暂不支持解码。");

        // The payload always sits at the end of the file. header_size does not
        // reliably account for the colour table (xwd reports 107 for a dump whose
        // colour table is 3 KiB), so the length is what identifies the pixels.
        var payload = data.Length - (long)bytesPerLine * height;
        if (payload < HeaderBytes || payload > data.Length)
            throw new InvalidOperationException("xwd 输出的像素数据长度与文件头不一致。");

        var red = MaskShift(redMask);
        var green = MaskShift(greenMask);
        var blue = MaskShift(blueMask);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        try
        {
            for (var y = 0; y < height; y++)
            {
                var row = (int)payload + y * bytesPerLine;
                for (var x = 0; x < width; x++)
                {
                    var pixel = ReadPixel(data, row + x * bytesPerPixel, bytesPerPixel, byteOrder == 0);
                    var r = (byte)Extract(pixel, redMask, red);
                    var g = (byte)Extract(pixel, greenMask, green);
                    var b = (byte)Extract(pixel, blueMask, blue);
                    bitmap.SetPixel(x, y, new SKColor(r, g, b, 255));
                }
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static int BytesPerPixel(int bitsPerPixel, int bytesPerLine, int width) => bitsPerPixel switch
    {
        32 => 4,
        // A 24-bit visual is usually padded to a 32-bit unit per scanline.
        24 => bytesPerLine >= width * 4 ? 4 : 3,
        16 => 2,
        _ => 0
    };

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
        | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static uint ReadPixel(byte[] data, int offset, int bytes, bool littleEndian)
    {
        return bytes switch
        {
            2 => littleEndian
                ? (uint)(data[offset] | (data[offset + 1] << 8))
                : (uint)((data[offset] << 8) | data[offset + 1]),
            3 => littleEndian
                ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16))
                : (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]),
            _ => littleEndian
                ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
                : ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3]
        };
    }

    /// <summary>Trailing-zero count of a channel mask, so masked bits can be shifted down.</summary>
    private static int MaskShift(uint mask)
    {
        if (mask == 0) return 0;
        var shift = 0;
        while ((mask & 1) == 0) { mask >>= 1; shift++; }
        return shift;
    }

    private static uint Extract(uint pixel, uint mask, int shift)
    {
        if (mask == 0) return 0;
        var value = (pixel & mask) >> shift;
        var bits = 0;
        for (var probe = mask >> shift; probe != 0; probe >>= 1) bits++;
        return bits >= 8 ? value >> (bits - 8) : value << (8 - bits);
    }
}
