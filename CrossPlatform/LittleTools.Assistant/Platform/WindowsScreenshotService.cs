using Avalonia.Controls;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace LittleTools.Assistant.Platform;

[SupportedOSPlatform("windows")]
internal sealed class WindowsScreenshotService : IScreenshotService
{
    public async Task<byte[]?> CaptureRegionAsync(Window owner, CancellationToken cancellationToken)
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary
            ?? throw new InvalidOperationException("无法读取当前屏幕。");
        owner.Hide();
        await Task.Delay(120, cancellationToken);
        var overlay = new SelectionWindow(screen);
        var rectangle = await overlay.SelectAsync();
        if (rectangle is null)
        {
            owner.Show();
            owner.Activate();
            return null;
        }
        await Task.Delay(120, cancellationToken);
        try
        {
            using var bitmap = new Bitmap(rectangle.Value.Width, rectangle.Value.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(rectangle.Value.X, rectangle.Value.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        finally
        {
            owner.Show();
            owner.Activate();
        }
    }
}
