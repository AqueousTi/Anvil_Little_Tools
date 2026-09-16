using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Text.Json;

namespace LittleTools.Assistant.Services;

internal sealed record ScreenshotTranslationResult(string ImagePath, string PlainText);

internal static class ScreenshotTranslationImage
{
    public static ScreenshotTranslationResult Create(
        byte[] originalPng,
        string responseJson,
        Guid conversationId,
        string? outputPath = null)
    {
        var blocks = ParseBlocks(responseJson);
        using var sourceStream = new MemoryStream(originalPng, writable: false);
        using var source = new Bitmap(sourceStream);
        var width = Math.Max(1, source.PixelSize.Width);
        var height = Math.Max(1, source.PixelSize.Height);
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromRgb(17, 20, 27))
        };
        canvas.Children.Add(new Image
        {
            Source = source,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill
        });

        var placed = new List<Rect>();
        var requiredHeight = (double)height;
        foreach (var block in blocks)
        {
            var sourceX = Math.Clamp(block.X / 1000d * width, 0, width - 1);
            var sourceY = Math.Clamp(block.Y / 1000d * height, 0, height - 1);
            var sourceWidth = Math.Clamp(block.Width / 1000d * width, 1, width - sourceX);
            var sourceHeight = Math.Clamp(block.Height / 1000d * height, 1, height - sourceY);
            var fontSize = Math.Clamp(sourceHeight * 0.72, 11, 25);
            var targetWidth = Math.Clamp(
                Math.Max(sourceWidth, Math.Min(block.Translation.Length, 24) * fontSize * 0.92 + 12),
                72,
                width - sourceX);
            var label = new Border
            {
                Width = targetWidth,
                Padding = new Thickness(5, 3),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(224, 12, 18, 24)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 124, 237, 174)),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = block.Translation,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = fontSize,
                    LineHeight = fontSize * 1.3,
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 255, 233))
                }
            };
            label.Measure(new Size(targetWidth, double.PositiveInfinity));
            var labelHeight = Math.Max(fontSize + 8, label.DesiredSize.Height);
            var targetY = sourceY + sourceHeight + 3;
            var candidate = new Rect(sourceX, targetY, targetWidth, labelHeight);
            foreach (var occupied in placed.Where(item => item.Intersects(candidate)).OrderBy(item => item.Bottom))
            {
                targetY = occupied.Bottom + 3;
                candidate = new Rect(sourceX, targetY, targetWidth, labelHeight);
            }

            Canvas.SetLeft(label, sourceX);
            Canvas.SetTop(label, targetY);
            canvas.Children.Add(label);
            placed.Add(candidate);
            requiredHeight = Math.Max(requiredHeight, candidate.Bottom + 3);
        }

        var outputHeight = Math.Max(height, (int)Math.Ceiling(requiredHeight));
        canvas.Height = outputHeight;
        canvas.Measure(new Size(width, outputHeight));
        canvas.Arrange(new Rect(0, 0, width, outputHeight));
        using var result = new RenderTargetBitmap(new PixelSize(width, outputHeight), new Vector(96, 96));
        result.Render(canvas);

        var path = ResolveOutputPath(outputPath, conversationId);
        using var output = File.Create(path);
        result.Save(output, new PngBitmapEncoderOptions());
        return new ScreenshotTranslationResult(path, string.Join("\n", blocks.Select(block => block.Translation)));
    }

    internal static IReadOnlyList<ScreenshotTranslationBlock> ParseBlocks(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("模型没有返回有效的坐标 JSON。");
        using var document = JsonDocument.Parse(response[start..(end + 1)]);
        if (!document.RootElement.TryGetProperty("blocks", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("模型返回结果缺少 blocks 数组。");

        var blocks = new List<ScreenshotTranslationBlock>();
        foreach (var item in items.EnumerateArray())
        {
            var translation = ReadString(item, "translation")?.Trim();
            if (string.IsNullOrWhiteSpace(translation)) continue;
            blocks.Add(new ScreenshotTranslationBlock(
                ReadCoordinate(item, "x"),
                ReadCoordinate(item, "y"),
                Math.Max(1, ReadCoordinate(item, "width")),
                Math.Max(1, ReadCoordinate(item, "height")),
                translation));
        }
        if (blocks.Count == 0) throw new InvalidDataException("模型没有识别出可标注的文字。");
        return blocks;
    }

    private static int ReadCoordinate(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return 0;
        var number = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : 0;
        return Math.Clamp((int)Math.Round(number), 0, 1000);
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ResolveOutputPath(string? outputPath, Guid conversationId)
    {
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var path = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }
        var directory = Path.Combine(AppPaths.DataDirectory, "translated-screenshots");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{conversationId:N}-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}.png");
    }
}

internal sealed record ScreenshotTranslationBlock(int X, int Y, int Width, int Height, string Translation);
