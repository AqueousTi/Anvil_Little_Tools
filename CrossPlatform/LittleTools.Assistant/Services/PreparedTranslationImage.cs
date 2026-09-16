using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LittleTools.Assistant.Services;

internal sealed record PreparedTranslationImage(byte[] Png, int ContentWidth, int ContentHeight)
{
    public static PreparedTranslationImage Create(byte[] originalPng)
    {
        using var input = new MemoryStream(originalPng, writable: false);
        using var source = new Bitmap(input);
        var factor = Math.Min(1d, 4096d / Math.Max(source.PixelSize.Width, source.PixelSize.Height));
        for (var attempt = 0; attempt < 8; attempt++, factor *= 0.75)
        {
            var contentWidth = Math.Max(1, (int)Math.Floor(source.PixelSize.Width * factor));
            var contentHeight = Math.Max(1, (int)Math.Floor(source.PixelSize.Height * factor));
            var (width, height) = PaddedSize(contentWidth, contentHeight);
            var canvas = new Canvas { Width = width, Height = height, Background = Brushes.Black };
            canvas.Children.Add(new Image { Source = source, Width = contentWidth, Height = contentHeight, Stretch = Stretch.Fill });
            canvas.Measure(new Size(width, height));
            canvas.Arrange(new Rect(0, 0, width, height));
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(canvas);
            using var output = new MemoryStream();
            bitmap.Save(output, new PngBitmapEncoderOptions());
            if (output.Length < 4 * 1024 * 1024) return new PreparedTranslationImage(output.ToArray(), contentWidth, contentHeight);
        }
        throw new InvalidOperationException("截图过大，请缩小框选区域后重试。");
    }

    internal static (int Width, int Height) PaddedSize(int width, int height) =>
        (Math.Max(30, Math.Max(width, (int)Math.Ceiling(height / 3d))),
         Math.Max(30, Math.Max(height, (int)Math.Ceiling(width / 3d))));
}
