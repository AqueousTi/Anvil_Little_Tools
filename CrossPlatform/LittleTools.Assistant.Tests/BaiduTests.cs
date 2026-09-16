using LittleTools.Assistant.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text.Json;

internal static class BaiduTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        var credentials = new BaiduCredentials("2015063000000001", "1234567890");
        var secondary = new Avalonia.PixelRect(-1080, 4, 1080, 1920);
        check("secondary screen keeps negative physical coordinates",
            LittleTools.Assistant.Platform.SelectionWindow.ClipSelection(new(-1000, 104), new(-800, 304), secondary) == new Avalonia.PixelRect(-1000, 104, 200, 200));
        check("reverse selection clamps to secondary screen",
            LittleTools.Assistant.Platform.SelectionWindow.ClipSelection(new(30, 2000), new(-1200, -10), secondary) == secondary);
        var cacheTest = Path.GetFullPath(Path.Combine(".artifacts", "cache-test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(cacheTest);
        var generated = Path.Combine(cacheTest, Guid.NewGuid().ToString("N") + "-20260916-123456789.png");
        var exported = Path.Combine(cacheTest, "saved-image.png");
        File.WriteAllText(generated, "cache");
        File.WriteAllText(exported, "export");
        TranslationImageCache.ClearDirectory(cacheTest);
        check("cache cleanup removes generated screenshots", !File.Exists(generated));
        check("cache cleanup preserves unrelated images", File.Exists(exported));
        File.Delete(exported);
        Directory.Delete(cacheTest);
        check("Baidu documented text signature", BaiduTranslationService.TextSignature(credentials, "apple", "65478") == "a1a7461d92e5194c5cae3182b5b24de1");
        check("Baidu returns every result", BaiduTranslationService.ParseText("""{"trans_result":[{"src":"one","dst":"一"},{"src":"two","dst":"二"}]}""") == "一\n二");
        check("Baidu language codes", BaiduTranslationService.Direction(TranslationRoutes.Find("zh-ja"), "你好") == ("zh", "jp"));
        check("Baidu detects Chinese input", BaiduTranslationService.Direction(TranslationRoutes.Default, "你好") == ("zh", "en"));
        check("Baidu detects English input", BaiduTranslationService.Direction(TranslationRoutes.Default, "Hello") == ("auto", "zh"));
        check("wide screenshot padded", PreparedTranslationImage.PaddedSize(900, 50) == (900, 300));
        check("tall screenshot padded", PreparedTranslationImage.PaddedSize(50, 900) == (300, 900));
        check("small screenshot padded", PreparedTranslationImage.PaddedSize(10, 20) == (30, 30));

        var protectedText = new TranslationText("Open `/etc/hosts` and run `git status`.\ngit diff --stat");
        check("code placeholders restore", protectedText.Restore(protectedText.Text) == "Open `/etc/hosts` and run `git status`.\ngit diff --stat");
        check("code only bypasses translation", new TranslationText("sudo apt update").IsEntirelyProtected);
        check("prose between code fences still translates", !new TranslationText("```\ngit status\n```\nPlease open the terminal\n```\npwd\n```").IsEntirelyProtected);
        check("English UI label needs translation", ScreenshotTranslationImage.NeedsChineseTranslation(new(0, 0, 10, 10, "Settings", "Settings")));
        check("uppercase UI label needs translation", ScreenshotTranslationImage.NeedsChineseTranslation(new(0, 0, 10, 10, "SETTINGS", "SETTINGS")));
        check("English with equals needs translation", ScreenshotTranslationImage.NeedsChineseTranslation(new(0, 0, 10, 10, "Set value = enabled", "Set value = enabled")));
        check("commands retain English", !ScreenshotTranslationImage.NeedsChineseTranslation(new(0, 0, 10, 10, "sudo apt update", "sudo apt update")));

        var imageJson = """{"error_code":"0","data":{"content":[{"src":"Settings","dst":"设置","rect":"400 10 50 20"},{"src":"sudo apt update","dst":"错误改写","rect":"10 40 100 20"}]}}""";
        var blocks = BaiduTranslationService.ParseImage(imageJson, 500, 100);
        check("pixel coordinates normalize against content", blocks[0].X == 800 && blocks[0].Y == 100 && blocks[0].Width == 100 && blocks[0].Height == 200);
        check("screenshot commands preserve source", blocks[1].Translation == "sudo apt update");
        ExpectError("unopened image service gives actionable error", () => BaiduTranslationService.ParseText("""{"error_code":"58002"}"""), check);
        ExpectError("missing coordinates are rejected", () => BaiduTranslationService.ParseImage("""{"data":{"content":[{"src":"Settings","dst":"设置"}]}}""", 100, 100), check);
        ExpectError("empty image response rejected", () => BaiduTranslationService.ParseImage("""{"data":{"content":[]}}""", 100, 100), check);
        ExpectError("mutated code is rejected", () => protectedText.Restore("all code was deleted"), check);

        using var handler = new StubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            check("Baidu sends text by POST", request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/trans/vip/translate");
            check("Baidu sends explicit target language", body.Contains("to=zh", StringComparison.Ordinal));
            check("Baidu never transmits raw secret", !body.Contains(credentials.SecretKey, StringComparison.Ordinal));
            return """{"trans_result":[{"src":"hello","dst":"你好"}]}""";
        });
        using var client = new HttpClient(handler);
        check("Baidu service uses parsed response", await new BaiduTranslationService(client).TranslateAsync("hello", TranslationRoutes.Default, credentials, CancellationToken.None) == "你好");
        var imageAttempts = 0;
        using var imageHandler = new StubHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            check("image retries retain multipart upload", body.Contains("screenshot.png", StringComparison.Ordinal));
            imageAttempts++;
            return imageAttempts == 1 ? """{"error_code":"54003"}""" : imageJson;
        });
        using var imageClient = new HttpClient(imageHandler);
        var retried = await new BaiduTranslationService(imageClient).TranslateImageAsync(new([1, 2, 3], 500, 100), credentials, CancellationToken.None);
        check("image rate limit retries successfully", imageAttempts == 2 && retried.Count == 2);
        try { BaiduTranslationService.ParseImage("""{"error_code":"69004"}""", 100, 100); check("empty OCR explains failure", false); }
        catch (BaiduApiException exception) { check("empty OCR explains failure", exception.Code == "69004" && exception.Message.Contains("未识别到文字", StringComparison.Ordinal)); }
    }

    public static async Task<int> RunLiveAsync()
    {
        var credentials = new SettingsStore().ResolveBaiduCredentials();
        if (credentials is null) { Console.Error.WriteLine("BAIDU_LIVE_NO_CREDENTIALS"); return 1; }
        var service = new BaiduTranslationService();
        foreach (var input in new[] { "Settings", "Please open the terminal.", "请打开终端。", "Open `/etc/hosts` and run `git status`." })
        {
            var result = await service.TranslateAsync(input, TranslationRoutes.Default, credentials, CancellationToken.None);
            var expectChinese = !TranslationRoutes.ContainsChinese(input);
            if (string.IsNullOrWhiteSpace(result) || TranslationRoutes.ContainsChinese(result) != expectChinese)
                throw new InvalidOperationException("Live text translation returned the wrong language.");
            if (input.Contains('`') && (!result.Contains("`/etc/hosts`", StringComparison.Ordinal) || !result.Contains("`git status`", StringComparison.Ordinal)))
                throw new InvalidOperationException("Live text translation modified code.");
            Console.WriteLine("BAIDU_TEXT_LIVE_OK: " + result);
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return 0;
        using var bitmap = new Bitmap(660, 260);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", 22))
        {
            graphics.Clear(Color.White);
            graphics.DrawString("Settings", font, Brushes.Black, 24, 20);
            graphics.DrawString("Cancel", font, Brushes.Black, 24, 70);
            graphics.DrawString("Open the terminal", font, Brushes.Black, 24, 120);
            graphics.DrawString("sudo apt update", font, Brushes.Black, 24, 170);
        }
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        var blocks = await service.TranslateImageAsync(new(output.ToArray(), 660, 260), credentials, CancellationToken.None);
        if (blocks.Count < 3 || blocks.Any(ScreenshotTranslationImage.NeedsChineseTranslation))
            throw new InvalidOperationException("Live screenshot translation has missing or untranslated text.");
        var artifactDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".artifacts", "baidu-translation"));
        Directory.CreateDirectory(artifactDir);
        File.WriteAllBytes(Path.Combine(artifactDir, "sample.png"), output.ToArray());
        File.WriteAllText(Path.Combine(artifactDir, "sample-blocks.json"), JsonSerializer.Serialize(blocks));
        Console.WriteLine("BAIDU_IMAGE_LIVE_OK: " + blocks.Count + " blocks; same legacy credentials.");
        return 0;
    }

    private static void ExpectError(string name, Action action, Action<string, bool> check)
    {
        try { action(); check(name, false); }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException) { check(name, true); }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await respond(request)) };
    }
}
