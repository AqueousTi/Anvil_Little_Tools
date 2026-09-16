using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LittleTools.Assistant.Services;

internal static class ScreenshotTranslationImage
{
    public static string Create(byte[] originalPng, string translation, Guid conversationId, string? outputPath = null)
    {
        using var sourceStream = new MemoryStream(originalPng, writable: false);
        using var source = new Bitmap(sourceStream);
        var width = Math.Max(1, source.PixelSize.Width);
        var height = Math.Max(1, source.PixelSize.Height);
        var padding = Math.Clamp(width * 0.025, 18, 42);
        var fontSize = Math.Clamp(width / 48d, 16, 28);

        var translatedText = new TextBlock
        {
            Text = translation.Trim(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            LineHeight = fontSize * 1.55,
            Foreground = Brushes.White,
            MaxWidth = width - padding * 2
        };
        var translationPanel = new Border
        {
            Width = width,
            Padding = new Thickness(padding),
            Background = new SolidColorBrush(Color.FromRgb(17, 20, 27)),
            Child = new StackPanel
            {
                Spacing = Math.Max(8, fontSize * 0.45),
                Children =
                {
                    new TextBlock
                    {
                        Text = "中文翻译",
                        FontSize = fontSize * 0.72,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(124, 237, 174))
                    },
                    translatedText
                }
            }
        };
        var composition = new StackPanel
        {
            Width = width,
            Children =
            {
                new Image
                {
                    Source = source,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Fill
                },
                translationPanel
            }
        };

        composition.Measure(new Size(width, double.PositiveInfinity));
        var totalHeight = Math.Max(height + 1, (int)Math.Ceiling(composition.DesiredSize.Height));
        composition.Arrange(new Rect(0, 0, width, totalHeight));

        using var result = new RenderTargetBitmap(new PixelSize(width, totalHeight), new Vector(96, 96));
        result.Render(composition);
        var path = outputPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var directory = Path.Combine(AppPaths.DataDirectory, "translated-screenshots");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"{conversationId:N}-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}.png");
        }
        else
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        using var output = File.Create(path);
        result.Save(output, new PngBitmapEncoderOptions());
        return path;
    }
}
