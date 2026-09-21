namespace LittleTools.Assistant.Services;

internal static class DomesticApiHttpClient
{
    // These providers are reached directly in China. Do not inherit a stale
    // localhost proxy from the system or the launching process's environment.
    internal static HttpClient Create(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = timeout };
}
