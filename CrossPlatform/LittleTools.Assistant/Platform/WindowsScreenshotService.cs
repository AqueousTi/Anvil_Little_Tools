using Avalonia.Controls;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace LittleTools.Assistant.Platform;

[SupportedOSPlatform("windows")]
internal sealed class WindowsScreenshotService : IScreenshotService
{
    public async Task<byte[]?> CaptureRegionAsync(Window owner, CancellationToken cancellationToken)
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary
            ?? throw new InvalidOperationException("无法读取当前屏幕。");
        owner.Hide();
        try
        {
            await Task.Delay(120, cancellationToken);
            var overlay = new SelectionWindow(screen);
            var rectangle = await overlay.SelectAsync();
            if (rectangle is null) return null;
            await Task.Delay(120, cancellationToken);
            return CapturePixels(rectangle.Value);
        }
        finally
        {
            owner.Show();
            owner.Activate();
        }
    }

    internal static byte[] CapturePixels(Avalonia.PixelRect rectangle)
    {
        // GDI must read physical desktop pixels on monitors with differing DPI.
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            using var bitmap = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(rectangle.X, rectangle.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
