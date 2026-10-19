using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;

namespace LittleTools.Assistant.Services;

internal sealed class DeepSeekProvider : IAssistantProvider
{
    private static readonly Uri Endpoint = new("https://api.deepseek.com/responses");
    private static readonly HttpClient Client = DomesticApiHttpClient.Create(Timeout.InfiniteTimeSpan);

    public ProviderKind Kind => ProviderKind.DeepSeek;

    public async IAsyncEnumerable<StreamUpdate> StreamAsync(
        AssistantRequest request,
        string apiKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(BuildBody(request));
        using var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"DeepSeek HTTP {(int)response.StatusCode}: {FriendlyBody(error)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var update in ReadResponseAsync(stream, cancellationToken)) yield return update;
    }

    internal static async IAsyncEnumerable<StreamUpdate> ReadResponseAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var emitted = new StringBuilder();
        var completed = false;
        await foreach (var item in SseReader.ReadAsync(stream, cancellationToken))
        {
            if (item.Data is "[DONE]" or "") continue;
            using var document = ParseEvent(item.Data);
            var root = document.RootElement;
            var eventType = ReadString(root, "type") ?? item.Name;
            if (string.Equals(eventType, "response.output_text.delta", StringComparison.OrdinalIgnoreCase))
            {
                var delta = ReadString(root, "delta");
                if (!string.IsNullOrEmpty(delta))
                {
                    emitted.Append(delta);
                    yield return new StreamUpdate { TextDelta = delta };
                }
            }
            else if (string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase))
            {
                completed = true;
                var fullText = ReadOutputText(root);
                if (fullText.Length > emitted.Length && fullText.StartsWith(emitted.ToString(), StringComparison.Ordinal))
                {
                    var remaining = fullText[emitted.Length..];
                    emitted.Append(remaining);
                    yield return new StreamUpdate { TextDelta = remaining };
                }
                if (emitted.Length == 0) throw new ModelResponseException("模型未返回方案文字。", true);
                var sources = ReadSources(root);
                if (sources.Count > 0) yield return new StreamUpdate { Sources = sources };
            }
            else if (string.Equals(eventType, "response.incomplete", StringComparison.OrdinalIgnoreCase))
            {
                var reason = root.TryGetProperty("response", out var incomplete) && incomplete.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object
                    ? ReadString(details, "reason") : null;
                throw new ModelResponseException(reason == "max_output_tokens"
                    ? "模型达到输出上限，响应未完整生成。"
                    : "模型响应未完整生成" + (reason is null ? "。" : $"（{reason}）。"), reason == "max_output_tokens");
            }
            else if (eventType is "response.failed" or "error")
            {
                var failure = root.TryGetProperty("response", out var failed) ? failed : root;
                var description = failure.TryGetProperty("error", out var error) ? ReadString(error, "message") : ReadString(failure, "message");
                throw new ModelResponseException("DeepSeek 返回错误：" + (description ?? "请求失败。"), false);
            }
        }
        if (!completed) throw new ModelResponseException("模型连接在完成前中断。", true);
    }

    private static JsonDocument ParseEvent(string data)
    {
        try { return JsonDocument.Parse(data); }
        catch (JsonException) { throw new ModelResponseException("模型连接返回了不完整的响应数据。", true); }
    }

    private static string ReadOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (ReadString(item, "type") != "message" || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
                if (ReadString(part, "type") == "output_text") text.Append(ReadString(part, "text"));
        }
        return text.ToString();
    }

    internal static object BuildBody(AssistantRequest request)
    {
        var input = new List<object>();
        for (var index = 0; index < request.Messages.Count; index++)
        {
            var message = request.Messages[index];
            var image = message.ImageBytes ?? (index == request.Messages.Count - 1 ? request.ImageBytes : null);
            if (message.Role == "user" && image is not null)
            {
                input.Add(new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_image", image_url = DataUrl(request.ImageMimeType, image) },
                        new { type = "input_text", text = message.Content }
                    }
                });
            }
            else input.Add(new { role = message.Role, content = message.Content });
        }
        var tools = request.EnableSearch ? new object[] { new { type = "web_search" } } : null;
        return new
        {
            model = request.Model,
            instructions = request.SystemPrompt,
            input,
            stream = true,
            max_output_tokens = request.MaxOutputTokens ?? (request.DeepThinking ? 3000 : 1400),
            reasoning = new { effort = request.DeepThinking ? "high" : "none" },
            text = request.JsonOutput ? new { format = new { type = "json_object" } } : null,
            tools
        };
    }

    private static List<WebSource> ReadSources(JsonElement root)
    {
        var result = new List<WebSource>();
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array) continue;
                foreach (var annotation in annotations.EnumerateArray())
                {
                    var url = ReadString(annotation, "url");
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    result.Add(new WebSource { Title = ReadString(annotation, "title") ?? url, Url = url });
                }
            }
        }
        return result.GroupBy(item => item.Url).Select(group => group.First()).ToList();
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string DataUrl(string mime, byte[] bytes) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    private static string FriendlyBody(string body) => body.Length <= 500 ? body : body[..500];
}

internal sealed class ModelResponseException(string message, bool canRetry) : InvalidOperationException(message)
{
    internal bool CanRetry { get; } = canRetry;
}
