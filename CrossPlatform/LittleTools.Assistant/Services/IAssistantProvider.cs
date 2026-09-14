namespace LittleTools.Assistant.Services;

internal interface IAssistantProvider
{
    ProviderKind Kind { get; }
    IAsyncEnumerable<StreamUpdate> StreamAsync(
        AssistantRequest request,
        string apiKey,
        CancellationToken cancellationToken);
}

internal static class AssistantProviderFactory
{
    public static IAssistantProvider Create(ProviderKind provider) => provider switch
    {
        ProviderKind.DeepSeek => new DeepSeekProvider(),
        _ => new GlmProvider()
    };
}
