using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LittleTools.Assistant.Services;
using System.Text;

namespace LittleTools.Assistant;

/// <summary>
/// Offline verification for the click to magnify viewer.
///
/// It plants locally annotated translation images (no Baidu account needed) into
/// the real result list, then drives the same code path the pointer uses and
/// records every number the report quotes. It lives in its own partial file so the
/// shared <c>MainWindow.axaml.cs</c> keeps only the preview wiring.
/// </summary>
public sealed partial class MainWindow
{
    private const string PreviewSmokeBlocks =
        "{\"blocks\":["
        + "{\"x\":70,\"y\":140,\"width\":470,\"height\":70,\"source\":\"Open the terminal\",\"translation\":\"打开终端\"},"
        + "{\"x\":70,\"y\":256,\"width\":540,\"height\":70,\"source\":\"and run the update command\",\"translation\":\"并运行更新命令\"},"
        + "{\"x\":70,\"y\":487,\"width\":420,\"height\":70,\"source\":\"Run the update command\",\"translation\":\"运行更新命令\"}"
        + "]}";

    private const string PreviewSmokeBlocksPortrait =
        "{\"blocks\":["
        + "{\"x\":80,\"y\":830,\"width\":420,\"height\":60,\"source\":\"Meeting notes\",\"translation\":\"会议记录\"},"
        + "{\"x\":80,\"y\":905,\"width\":460,\"height\":60,\"source\":\"Ship the fix today\",\"translation\":\"今天发布修复\"}"
        + "]}";

    internal async Task RunScreenshotPreviewSmokeAsync(string outputPath, bool hold)
    {
        var first = ScreenshotTranslationImage.Create(
            BuildPreviewFixturePng(900, 640), PreviewSmokeBlocks, Guid.NewGuid(), outputPath + ".translated.png");
        var second = ScreenshotTranslationImage.Create(
            BuildPreviewFixturePng(640, 900), PreviewSmokeBlocksPortrait, Guid.NewGuid(), outputPath + ".translated-portrait.png");

        ShowTranslation();
        _conversation = new Conversation { Mode = AssistantMode.Screenshot };
        SetMode(AssistantMode.Screenshot);
        ConfigureComponentLayout(AssistantMode.Screenshot);
        _conversation.Messages.Add(new ConversationMessage { Role = "user", Content = "截图翻译测试（两张）" });
        _conversation.Messages.Add(new ConversationMessage
        {
            Role = "assistant",
            Model = "百度图片翻译",
            Content = "打开终端\n并运行更新命令\n运行更新命令",
            ImagePath = first.ImagePath
        });
        _conversation.Messages.Add(new ConversationMessage
        {
            Role = "assistant",
            Model = "百度图片翻译",
            Content = "会议记录\n今天发布修复",
            ImagePath = second.ImagePath
        });
        ExpandForContent();
        RenderConversation();
        await SmokeWaiter.PumpAsync();
        UpdateLayout();

        // Every translated image in the list must offer the magnifier, not just the
        // newest one.
        var images = Find<StackPanel>("MessagesPanel").GetVisualDescendants().OfType<Image>()
            .Where(item => item.Source is not null).ToList();
        if (images.Count != 2)
            throw new InvalidOperationException($"Expected two translated result images, found {images.Count}.");
        if (images.Any(item => !ReferenceEquals(item.Cursor, ScreenshotPreview.HandCursor)))
            throw new InvalidOperationException("A result image does not use the hand cursor, so the magnifier is undiscoverable.");
        var image = images[0];

        var center = image.TranslatePoint(new Point(image.Bounds.Width / 2, image.Bounds.Height / 2), this);
        var metrics = new StringBuilder();
        metrics.AppendLine("translatedImage=" + first.ImagePath);
        metrics.AppendLine("translatedImage2=" + second.ImagePath);
        metrics.AppendLine("bitmapPixelSize=" + image.Source!.Size.Width.ToString("0.#") + "x" + image.Source.Size.Height.ToString("0.#"));
        metrics.AppendLine("shownImageDip=" + image.Bounds.Width.ToString("0.#") + "x" + image.Bounds.Height.ToString("0.#"));
        metrics.AppendLine("imageCenterInWindowDip=" + (center is null ? "null" : $"{center.Value.X:0.#},{center.Value.Y:0.#}"));
        metrics.AppendLine("windowPositionPx=" + Position.X + "," + Position.Y);
        metrics.AppendLine("renderScaling=" + RenderScaling.ToString("0.##"));
        SaveRender(outputPath + ".list.png");
        File.WriteAllText(outputPath + ".metrics.txt", metrics.ToString());

        // Hold mode is the production run the shell drives with a real pointer: the
        // window stays exactly as the user would see it until the runner kills it.
        if (hold) return;

        var results = new StringBuilder(metrics.ToString());
        // Every step is flushed immediately so a failing assertion still leaves the
        // numbers that were observed before it.
        void Note(string line)
        {
            results.AppendLine(line);
            File.WriteAllText(outputPath + ".results.txt", results.ToString());
        }

        // 1. Open the viewer through the entry the image click calls.
        OpenScreenshotPreview(image, first.ImagePath);
        var preview = await WaitForPreviewAsync();
        Note("previewShowInTaskbar=" + preview.ShowInTaskbar);
        if (preview.ShowInTaskbar)
            throw new InvalidOperationException("The preview window would appear in the taskbar.");
        if (!preview.IsFitting)
            throw new InvalidOperationException("The preview did not start at the fit-to-window factor.");
        var fitZoom = preview.Zoom;
        Note("fitZoom=" + fitZoom.ToString("0.###"));
        Note("bitmapDip=" + preview.PreviewSize.Width.ToString("0.#") + "x" + preview.PreviewSize.Height.ToString("0.#"));
        SaveRender(outputPath + ".opened-main.png");
        preview.SaveRender(outputPath + ".preview.png");

        // 2. Wheel/button zoom in, then drag to pan.
        preview.ZoomStep(inward: false);
        preview.ZoomStep(inward: false);
        await SmokeWaiter.PumpAsync();
        if (preview.Zoom <= fitZoom)
            throw new InvalidOperationException($"Zooming in did not magnify: {fitZoom:0.###} -> {preview.Zoom:0.###}.");
        Note("zoomAfterTwoSteps=" + preview.Zoom.ToString("0.###") + "; fitting=" + preview.IsFitting);
        await SmokeWaiter.PumpAsync();
        Note("scrollExtent=" + preview.ScrollExtent.Width.ToString("0.#") + "x" + preview.ScrollExtent.Height.ToString("0.#")
            + "; scrollViewport=" + preview.ScrollViewport.Width.ToString("0.#") + "x" + preview.ScrollViewport.Height.ToString("0.#"));
        preview.DragBy(-60, -40);
        await SmokeWaiter.PumpAsync();
        var pan = preview.PanOffset;
        Note("panOffsetAfterDrag=-60,-40 -> " + pan.X.ToString("0.#") + "," + pan.Y.ToString("0.#"));
        if (pan.X <= 0 || pan.Y <= 0)
            throw new InvalidOperationException($"Dragging did not pan the magnified image: offset={pan}.");
        preview.SaveRender(outputPath + ".preview-zoom.png");

        // 3. 100%, then back to fit.
        preview.ZoomToActualSize();
        await SmokeWaiter.PumpAsync();
        if (Math.Abs(preview.Zoom - 1) > 1e-6)
            throw new InvalidOperationException($"100% did not show one bitmap pixel per DIP: zoom={preview.Zoom:0.###}.");
        Note("zoomActualSize=" + preview.Zoom.ToString("0.###"));
        preview.SaveRender(outputPath + ".preview-100.png");
        preview.FitToWindow();
        await SmokeWaiter.PumpAsync();
        if (Math.Abs(preview.Zoom - preview.FitZoom) > 1e-6)
            throw new InvalidOperationException($"Fit did not restore {preview.FitZoom:0.###}: zoom={preview.Zoom:0.###}.");
        Note("zoomAfterFit=" + preview.Zoom.ToString("0.###"));

        // 4. Escape closes the viewer and leaves the translation window alone.
        preview.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
        if (!await SmokeWaiter.WaitAsync(() => !IsPreviewOpen(), TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("Escape did not close the preview window.");
        if (!IsVisible) throw new InvalidOperationException("Closing the preview also hid the translation window.");
        Note("escapeClosedPreview=true; translationWindowVisible=" + IsVisible);

        // 5. The close button does the same.
        OpenScreenshotPreview(image, first.ImagePath);
        preview = await WaitForPreviewAsync();
        preview.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!await SmokeWaiter.WaitAsync(() => !IsPreviewOpen(), TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException("The preview close button did not close the window.");
        if (!IsVisible) throw new InvalidOperationException("Closing the preview also hid the translation window.");
        Note("closeButtonClosedPreview=true; translationWindowVisible=" + IsVisible);

        // 6. The second translated image opens its own preview at its own size.
        var secondImage = images[1];
        OpenScreenshotPreview(secondImage, second.ImagePath);
        preview = await WaitForPreviewAsync();
        Note("secondPreviewDip=" + preview.PreviewSize.Width.ToString("0.#") + "x" + preview.PreviewSize.Height.ToString("0.#"));
        if (Math.Abs(preview.PreviewSize.Width - secondImage.Source!.Size.Width) > 0.5)
            throw new InvalidOperationException(
                $"The second image opened the wrong bitmap: preview={preview.PreviewSize}, image={secondImage.Source.Size}.");
        preview.SaveRender(outputPath + ".preview-second.png");
        preview.Close();
        await SmokeWaiter.WaitAsync(() => !IsPreviewOpen(), TimeSpan.FromSeconds(2));

        // 7. Losing the focus closes the viewer, matching the suite dialogs. Some
        //    window managers do not deliver an in-app probe activation to a
        //    transient window; that is reported with the observed state and the
        //    real cross-application check is driven with XTest in the report.
        OpenScreenshotPreview(image, first.ImagePath);
        preview = await WaitForPreviewAsync();
        var other = new Window { Width = 120, Height = 90, ShowInTaskbar = false };
        try
        {
            other.Show();
            other.Activate();
            await SmokeWaiter.WaitAsync(() => !preview.IsActive, TimeSpan.FromSeconds(2));
            if (await SmokeWaiter.WaitAsync(() => !IsPreviewOpen(), TimeSpan.FromSeconds(2)))
                Note("focusLossClosedPreview=true; translationWindowVisible=" + IsVisible);
            else
                Note("focusLossClosedPreview=notDeliveredByThisDesktop; previewActive=" + preview.IsActive
                    + "; DeactivatedEvents=" + preview.DeactivatedEvents);
        }
        finally
        {
            other.Close();
        }
        if (IsPreviewOpen()) VisiblePreviewWindow()!.Close();
        await SmokeWaiter.PumpAsync();
        if (!IsVisible) throw new InvalidOperationException("Closing the preview also hid the translation window.");
        Note("translationWindowVisible=" + IsVisible);

        File.WriteAllText(outputPath + ".results.txt", results.ToString());
    }

    private static ScreenshotPreviewWindow? VisiblePreviewWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .OfType<ScreenshotPreviewWindow>().LastOrDefault(item => item.IsVisible);

    private static bool IsPreviewOpen() => VisiblePreviewWindow() is not null;

    private static async Task<ScreenshotPreviewWindow> WaitForPreviewAsync()
    {
        if (!await SmokeWaiter.WaitAsync(IsPreviewOpen, TimeSpan.FromSeconds(3)))
            throw new InvalidOperationException("The magnified preview window never opened.");
        var preview = VisiblePreviewWindow()!;
        // The window manager grants the focus asynchronously, and the focus-loss
        // path can only be exercised once the viewer really owns it.
        await SmokeWaiter.WaitAsync(() => preview.IsActive, TimeSpan.FromSeconds(2));
        return preview;
    }

    /// <summary>
    /// A terminal-like source screenshot, so the smoke needs no API account and no
    /// recorded image. The landscape one is taller than the 520 DIP the result list
    /// allows, which is exactly the case the viewer exists for.
    /// </summary>
    private static byte[] BuildPreviewFixturePng(int width, int height)
    {
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromRgb(13, 17, 23))
        };
        void Write(double top, string text, double size, string color)
        {
            var label = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("DejaVu Sans Mono,monospace"),
                FontSize = size,
                Foreground = Brush.Parse(color)
            };
            Canvas.SetLeft(label, 48);
            Canvas.SetTop(label, top);
            canvas.Children.Add(label);
        }

        Write(34, "user@devbox:~/project$", 30, "#7CEDAE");
        Write(96, "Open the terminal", 32, "#DCE5EF");
        Write(170, "and run the update command", 32, "#DCE5EF");
        Write(244, "$ sudo apt update", 30, "#8CC8FF");
        Write(318, "Run the update command", 32, "#DCE5EF");
        Write(392, "Enter your password", 32, "#DCE5EF");
        Write(466, "[sudo] password for user:", 28, "#FFD479");
        if (height > 560) Write(540, "Reading package lists... Done", 28, "#9AA5B1");
        if (height > 640) Write(614, "Building dependency tree... Done", 28, "#9AA5B1");
        if (height > 720) Write(688, "0 upgraded, 0 newly installed", 28, "#9AA5B1");
        if (height > 800) Write(762, "Meeting notes", 32, "#DCE5EF");
        if (height > 860) Write(836, "Ship the fix today", 32, "#DCE5EF");
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(canvas);
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }
}
