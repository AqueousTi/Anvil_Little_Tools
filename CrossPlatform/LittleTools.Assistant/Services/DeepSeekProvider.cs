using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LittleTools.Assistant.Services;

internal sealed class DeepSeekProvider : IAssistantProvider
{
    private static readonly Uri Endpoint = new("https://api.deepseek.com/responses");
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

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
        await foreach (var item in SseReader.ReadAsync(stream, cancellationToken))
        {
            if (item.Data is "[DONE]" or "") continue;
            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            var eventType = item.Name ?? ReadString(root, "type");
            if (string.Equals(eventType, "response.output_text.delta", StringComparison.OrdinalIgnoreCase))
            {
                var delta = ReadString(root, "delta");
                if (!string.IsNullOrEmpty(delta)) yield return new StreamUpdate { TextDelta = delta };
            }
            else if (string.Equals(eventType, "response.completed", StringComparison.OrdinalIgnoreCase))
            {
                var sources = ReadSources(root);
                if (sources.Count > 0) yield return new StreamUpdate { Sources = sources };
            }
        }
    }

    internal static object BuildBody(AssistantRequest request)
    {
        var input = new List<object>();
        for (var index = 0; index < request.Messages.Count; index++)
        {
            var message = request.Messages[index];
            if (message.Role == "user" && request.ImageBytes is not null && index == request.Messages.Count - 1)
            {
                input.Add(new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_image", image_url = DataUrl(request.ImageMimeType, request.ImageBytes) },
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
            max_output_tokens = request.DeepThinking ? 3000 : 1400,
            thinking = new { type = request.DeepThinking ? "enabled" : "disabled", reasoning_effort = request.DeepThinking ? "high" : "low" },
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
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string DataUrl(string mime, byte[] bytes) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    private static string FriendlyBody(string body) => body.Length <= 500 ? body : body[..500];
}
