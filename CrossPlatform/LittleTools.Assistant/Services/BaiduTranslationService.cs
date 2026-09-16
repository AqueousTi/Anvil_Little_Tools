using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LittleTools.Assistant.Services;

internal sealed class BaiduTranslationService(HttpClient? client = null)
{
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private static readonly SemaphoreSlim TextGate = new(1, 1);
    private static readonly SemaphoreSlim ImageGate = new(1, 1);
    private static DateTimeOffset _lastTextRequest;
    private static DateTimeOffset _lastImageRequest;
    private readonly HttpClient _client = client ?? DefaultClient;

    internal static string TextSignature(BaiduCredentials credentials, string text, string salt) =>
        Hash(Encoding.UTF8.GetBytes(credentials.AppId + text + salt + credentials.SecretKey));

    internal static string ImageSignature(BaiduCredentials credentials, byte[] image, string salt) =>
        Hash(Encoding.UTF8.GetBytes(credentials.AppId + Hash(image) + salt + "APICUIDmac" + credentials.SecretKey));

    private static string Hash(byte[] value) => Convert.ToHexStringLower(MD5.HashData(value));

    public async Task<string> TranslateAsync(string text, TranslationRoute route, BaiduCredentials credentials, CancellationToken cancellationToken)
    {
        var direction = Direction(route, text);
        var protectedText = new TranslationText(text);
        if (protectedText.IsEntirelyProtected) return text;
        if (Encoding.UTF8.GetByteCount(protectedText.Text) > 6000)
            throw new InvalidOperationException("文字过长，请分段翻译（每次不超过 6000 个 UTF-8 字节，约 2000 个汉字）。");
        var salt = Guid.NewGuid().ToString("N");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = protectedText.Text, ["from"] = direction.From, ["to"] = direction.To,
            ["appid"] = credentials.AppId, ["salt"] = salt,
            ["sign"] = TextSignature(credentials, protectedText.Text, salt)
        });
        // The legacy standard plan only allows one text request per second.
        await TextGate.WaitAsync(cancellationToken);
        try
        {
            var delay = TimeSpan.FromSeconds(1.3) - (DateTimeOffset.UtcNow - _lastTextRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                using var response = await _client.PostAsync("https://fanyi-api.baidu.com/api/trans/vip/translate", content, cancellationToken);
                var body = await ReadResponseAsync(response, cancellationToken);
                try { return protectedText.Restore(ParseText(body)); }
                catch (BaiduApiException exception) when (exception.Code == "54003" && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken);
                }
            }
        }
        finally { _lastTextRequest = DateTimeOffset.UtcNow; TextGate.Release(); }
    }

    public async Task<IReadOnlyList<ScreenshotTranslationBlock>> TranslateImageAsync(
        PreparedTranslationImage image, BaiduCredentials credentials, CancellationToken cancellationToken)
    {
        await ImageGate.WaitAsync(cancellationToken);
        try
        {
            var delay = TimeSpan.FromSeconds(1.3) - (DateTimeOffset.UtcNow - _lastImageRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                try { return await SendImageAsync(image, credentials, cancellationToken); }
                catch (BaiduApiException exception) when (exception.Code == "54003" && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken);
                }
            }
        }
        finally { _lastImageRequest = DateTimeOffset.UtcNow; ImageGate.Release(); }
    }

    private async Task<IReadOnlyList<ScreenshotTranslationBlock>> SendImageAsync(
        PreparedTranslationImage image, BaiduCredentials credentials, CancellationToken cancellationToken)
    {
        var salt = Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent();
        foreach (var pair in new Dictionary<string, string>
        {
            ["from"] = "auto", ["to"] = "zh", ["appid"] = credentials.AppId,
            ["salt"] = salt, ["cuid"] = "APICUID", ["mac"] = "mac", ["version"] = "3", ["paste"] = "0",
            ["sign"] = ImageSignature(credentials, image.Png, salt)
        }) form.Add(new StringContent(pair.Value), pair.Key);
        var binary = new ByteArrayContent(image.Png);
        binary.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(binary, "image", "screenshot.png");
        using var response = await _client.PostAsync("https://fanyi-api.baidu.com/api/trans/sdk/picture", form, cancellationToken);
        return ParseImage(await ReadResponseAsync(response, cancellationToken), image.ContentWidth, image.ContentHeight);
    }

    internal static (string From, string To) Direction(TranslationRoute route, string input) => route.SmartChineseEnglish
        ? TranslationRoutes.ContainsChinese(input) ? ("zh", "en") : ("auto", "zh")
        : (LanguageCode(route.Source), LanguageCode(route.Target));

    private static string LanguageCode(string code) => code switch
    {
        "ja" => "jp", "ko" => "kor", "fr" => "fra", "de" => "de", _ => code
    };

    internal static string ParseText(string body)
    {
        using var document = JsonDocument.Parse(body);
        CheckError(document.RootElement);
        if (!document.RootElement.TryGetProperty("trans_result", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("百度没有返回翻译结果。");
        var translations = results.EnumerateArray().Select(item => ReadString(item, "dst"))
            .Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        if (translations.Length == 0) throw new InvalidDataException("百度返回的译文为空。");
        return string.Join("\n", translations);
    }

    internal static IReadOnlyList<ScreenshotTranslationBlock> ParseImage(string body, int contentWidth, int contentHeight)
    {
        if (contentWidth <= 0 || contentHeight <= 0) throw new ArgumentOutOfRangeException(nameof(contentWidth));
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        CheckError(root);
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("百度没有返回图片中的文字，请重新框选清晰的文字区域。");
        var blocks = new List<ScreenshotTranslationBlock>();
        foreach (var item in items.EnumerateArray())
        {
            var source = ReadString(item, "src").Trim();
            var translated = ReadString(item, "dst").Trim();
            if (string.IsNullOrWhiteSpace(source)) continue;
            if (string.IsNullOrWhiteSpace(translated)) throw new InvalidDataException("图片中有文字未返回译文，请重试。");
            var rectangle = ReadString(item, "rect").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (rectangle.Length != 4 || !rectangle.All(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)))
                throw new InvalidDataException("百度返回的文字坐标无效，无法生成译图。");
            var coordinates = rectangle.Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            if (coordinates[2] <= 0 || coordinates[3] <= 0) throw new InvalidDataException("百度返回了空的文字区域。");
            // Padding is only on the bottom/right. Normalize against actual
            // screenshot content, not the padded upload, to retain alignment.
            var x = Math.Clamp(coordinates[0], 0, contentWidth - 1);
            var y = Math.Clamp(coordinates[1], 0, contentHeight - 1);
            blocks.Add(new ScreenshotTranslationBlock(
                (int)Math.Round(x * 1000 / contentWidth), (int)Math.Round(y * 1000 / contentHeight),
                Math.Max(1, (int)Math.Round(Math.Min(coordinates[2], contentWidth - x) * 1000 / contentWidth)),
                Math.Max(1, (int)Math.Round(Math.Min(coordinates[3], contentHeight - y) * 1000 / contentHeight)),
                source, TranslationText.IsProtectedOnly(source) ? source : translated));
        }
        if (blocks.Count == 0) throw new InvalidDataException("未识别到文字，请重新框选清晰的文字区域。");
        return blocks;
    }

    internal static void CheckError(JsonElement root)
    {
        if (!root.TryGetProperty("error_code", out var value)) return;
        var code = value.ToString();
        if (code is "0" or "52000") return;
        var message = code switch
        {
            "52001" => "请求超时，请稍后重试。",
            "52002" => "百度服务暂时异常，请稍后重试。",
            "52003" => "百度未授权当前请求，请检查 APPID 和对应服务是否开通。",
            "54000" => "百度请求参数无效，请重试并反馈错误码。",
            "54001" => "百度密钥或签名无效，请检查 APPID 和密钥是否对应。",
            "54003" => "请求过于频繁，请稍后重试。",
            "54004" => "百度账户余额不足或可用额度已用完。",
            "54005" => "长文本请求过于频繁，请稍后重试。",
            "58000" => "当前网络 IP 不在百度允许列表中，请检查开放平台的 IP 限制设置。",
            "58001" => "百度不支持当前翻译方向。",
            "58002" => "相关翻译服务尚未开通或已停用，请检查百度翻译开放平台。",
            "58003" => "当前请求被百度限制，请稍后重试。",
            "69001" => "百度无法读取截图数据，请重新截图。",
            "69002" => "百度图片识别超时，请缩小截图区域后重试。",
            "69003" => "百度未能识别图片内容，请框选清晰的文字区域。",
            "69004" => "截图中未识别到文字，请重新框选文字区域。",
            "69005" => "截图文件超过百度的 4 MB 限制，请缩小截图区域。",
            "69006" => "截图尺寸不符合百度要求，请重新截图。",
            "69007" => "百度不支持当前图片格式，请重新截图。",
            _ => "百度翻译请求未成功，请检查服务开通状态或稍后重试。"
        };
        throw new BaiduApiException(code, $"{message}（{code}）");
    }

    private static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"百度翻译连接失败：HTTP {(int)response.StatusCode}。");
        return await response.Content.ReadAsStringAsync(token);
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

internal sealed class BaiduApiException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
