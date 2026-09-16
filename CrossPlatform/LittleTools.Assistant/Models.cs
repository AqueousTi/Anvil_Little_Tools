using System.Text.Json.Serialization;

namespace LittleTools.Assistant;

public enum AssistantMode
{
    Translate,
    Chat,
    Screenshot
}

public enum ProviderKind
{
    Glm,
    DeepSeek
}

public enum SearchPolicy
{
    Auto,
    On,
    Off
}

public enum AppCommand
{
    ShowTranslation,
    ShowChat,
    Screenshot,
    Background,
    Exit
}

public sealed class AppSettings
{
    public ProviderKind Provider { get; set; } = ProviderKind.Glm;
    public SearchPolicy Search { get; set; } = SearchPolicy.Auto;
    public bool DeepThinking { get; set; }
    public string TranslationRouteId { get; set; } = "smart-zh-en";
    public string GlmModel { get; set; } = "glm-5.3-flash";
    public string DeepSeekModel { get; set; } = "deepseek-flash";
    public string GlmEnvironmentVariable { get; set; } = "ZHIPUAI_API_KEY";
    public string DeepSeekEnvironmentVariable { get; set; } = "DEEPSEEK_API_KEY";
    public string? GlmProtectedKey { get; set; }
    public string? DeepSeekProtectedKey { get; set; }
    public string? BaiduAppId { get; set; }
    public string? BaiduProtectedKey { get; set; }
}

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "新会话";
    public AssistantMode Mode { get; set; } = AssistantMode.Chat;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<ConversationMessage> Messages { get; set; } = [];

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "新会话" : Title;
}

public sealed class ConversationMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public ProviderKind? Provider { get; set; }
    public string? Model { get; set; }
    public List<WebSource> Sources { get; set; } = [];
}

public sealed class WebSource
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? PublishedAt { get; set; }
}

public sealed class ProviderMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public sealed class AssistantRequest
{
    public required ProviderKind Provider { get; init; }
    public required string Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required IReadOnlyList<ProviderMessage> Messages { get; init; }
    public byte[]? ImageBytes { get; init; }
    public string ImageMimeType { get; init; } = "image/png";
    public bool EnableSearch { get; init; }
    public bool DeepThinking { get; init; }
}

public sealed class StreamUpdate
{
    public string TextDelta { get; init; } = string.Empty;
    public IReadOnlyList<WebSource> Sources { get; init; } = [];
}
