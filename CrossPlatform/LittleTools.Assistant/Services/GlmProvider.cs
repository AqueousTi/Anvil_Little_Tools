using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LittleTools.Assistant.Services;

internal sealed class GlmProvider : IAssistantProvider
{
    private static readonly Uri Endpoint = new("https://open.bigmodel.cn/api/paas/v4/chat/completions");
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    public ProviderKind Kind => ProviderKind.Glm;

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
            throw new InvalidOperationException($"GLM HTTP {(int)response.StatusCode}: {FriendlyBody(error)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in SseReader.ReadAsync(stream, cancellationToken))
        {
            if (item.Data is "[DONE]" or "") continue;
            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            var delta = ReadGlmDelta(root);
            var sources = ReadSources(root);
            if (!string.IsNullOrEmpty(delta) || sources.Count > 0)
                yield return new StreamUpdate { TextDelta = delta ?? string.Empty, Sources = sources };
        }
    }

    internal static object BuildBody(AssistantRequest request)
    {
        var messages = new List<object>
        {
            new { role = "system", content = request.SystemPrompt }
        };
        foreach (var source in request.Messages)
        {
            var image = source.ImageBytes ?? (ReferenceEquals(source, request.Messages[^1]) ? request.ImageBytes : null);
            if (source.Role == "user" && image is not null)
            {
                messages.Add(new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = DataUrl(request.ImageMimeType, image) } },
                        new { type = "text", text = source.Content }
                    }
                });
            }
            else
            {
                messages.Add(new { role = source.Role, content = source.Content });
            }
        }

        var tools = request.EnableSearch
            ? new object[] { new { type = "web_search", web_search = new { enable = true, search_result = true, count = 6, content_size = "medium" } } }
            : null;
        return new
        {
            model = request.Model,
            messages,
            stream = true,
            max_tokens = request.DeepThinking ? 3000 : 1400,
            thinking = new { type = "enabled" },
            reasoning_effort = request.DeepThinking ? "max" : "low",
            tools
        };
    }

    private static string? ReadGlmDelta(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta)) return null;
        return delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
    }

    private static List<WebSource> ReadSources(JsonElement root)
    {
        var result = new List<WebSource>();
        if (!root.TryGetProperty("web_search", out var search) || search.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in search.EnumerateArray())
        {
            var url = ReadString(item, "link");
            if (string.IsNullOrWhiteSpace(url)) continue;
            result.Add(new WebSource
            {
                Title = ReadString(item, "title") ?? url,
                Url = url,
                PublishedAt = ReadString(item, "publish_date")
            });
        }
        return result;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string DataUrl(string mime, byte[] bytes) => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    private static string FriendlyBody(string body) => body.Length <= 500 ? body : body[..500];
}
