using System.Net;
using System.Reflection;
using System.Text;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// Offline replay for the monitor's network sources. The recorded and hand built
/// bodies live in <c>Monitor/Fixtures</c> (see the README there for which is
/// which) and are served through the normal <see cref="MonitorDataService"/>
/// parse path, so a replay walks exactly the code a live run walks.
/// </summary>
internal static class MonitorFixtures
{
    private const string Prefix = "LittleTools.Assistant.Monitor.Fixtures.";

    /// <summary>Request URL fragment to fixture file, first match wins.</summary>
    private static readonly (string Match, string File)[] Routes =
    [
        ("api.deepseek.com/user/balance", "deepseek-balance.json"),
        ("/api/monitor/usage/quota/limit", "glm-quota-limits.json"),
        ("query-customer-account-report", "glm-report-wallet.json"),
        ("/api/paas/v4/balance", "glm-balance-v4.json")
    ];

    private static readonly Assembly Source = typeof(MonitorFixtures).Assembly;

    public static IReadOnlyList<string> Names => Routes.Select(route => route.File).ToArray();

    /// <summary>The fixture that replays a request, or null when none was recorded.</summary>
    public static string? FileFor(string url)
    {
        foreach (var (match, file) in Routes)
            if (url.Contains(match, StringComparison.Ordinal)) return file;
        return null;
    }

    public static bool Exists(string name) => Find(name) is not null;

    public static byte[] Read(string name)
    {
        using var stream = Find(name) ?? throw new FileNotFoundException("Missing monitor fixture: " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static string ReadText(string name) => Encoding.UTF8.GetString(Read(name));

    private static Stream? Find(string name) =>
        Source.GetManifestResourceStream(Prefix + name)
        ?? Source.GetManifestResourceNames()
            .Where(candidate => candidate.EndsWith("." + name, StringComparison.Ordinal))
            .Select(Source.GetManifestResourceStream)
            .FirstOrDefault(stream => stream is not null);

    /// <summary>A service that answers from the recorded bodies; unknown URLs answer 404.</summary>
    public static MonitorDataService CreateService(MonitorFixtureScenario scenario = MonitorFixtureScenario.Success,
        ICodexAppServer? codex = null) =>
        new(new MonitorFixtureHandler(scenario), codex ?? new FixtureCodexAppServer());
}

/// <summary>The response sets a run can ask the replay for.</summary>
internal enum MonitorFixtureScenario
{
    /// <summary>Every source answers with its success body.</summary>
    Success,

    /// <summary>What a missing or rejected API key really produces (measured live).</summary>
    AuthFailure,

    /// <summary>Quota succeeds with no Coding Plan windows; wallet succeeds.</summary>
    NoCodingPlan,

    /// <summary>
    /// What this machine's real pay-as-you-go GLM key produces: the quota endpoint
    /// answers <c>code 500</c> "当前用户不存在coding plan" while the account report
    /// succeeds and the old v4 fallback answers 404.
    /// </summary>
    NoCodingPlanApiError,

    /// <summary>The account report refuses, so the wallet must use /api/paas/v4/balance.</summary>
    WalletEndpointOnly,

    /// <summary>The account report refuses and the real v4 endpoint answers 404: no wallet.</summary>
    WalletUnavailable,

    /// <summary>Quota refuses and the wallet refuses with server errors.</summary>
    BothRefused,

    /// <summary>Only the quota refuses; the wallet still answers.</summary>
    QuotaRefused,

    /// <summary>DeepSeek answers its drained-account body.</summary>
    DeepSeekDrained
}

/// <summary>Serves the embedded fixtures, with the failure branches switchable.</summary>
internal sealed class MonitorFixtureHandler : HttpMessageHandler
{
    private readonly MonitorFixtureScenario _scenario;

    public MonitorFixtureHandler(MonitorFixtureScenario scenario) => _scenario = scenario;

    /// <summary>When true the DeepSeek endpoint answers its recorded 401 instead of the fixture.</summary>
    public bool RefuseDeepSeek { get; set; }

    /// <summary>Requests seen, for the smoke report.</summary>
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        Requests.Add(url);
        var response = Build(url, request);
        return Task.FromResult(response);
    }

    private HttpResponseMessage Build(string url, HttpRequestMessage request)
    {
        if (url.Contains("api.deepseek.com", StringComparison.Ordinal))
        {
            if (RefuseDeepSeek || _scenario == MonitorFixtureScenario.AuthFailure)
                return Text(HttpStatusCode.Unauthorized, "deepseek-auth-failed.txt", request);
            if (_scenario == MonitorFixtureScenario.DeepSeekDrained)
                return File("deepseek-empty-balance.json", request);
            return File("deepseek-balance.json", request);
        }

        if (url.Contains("/api/monitor/usage/quota/limit", StringComparison.Ordinal))
        {
            return _scenario switch
            {
                MonitorFixtureScenario.AuthFailure => Text(HttpStatusCode.OK, "glm-quota-auth-failed.json", request),
                MonitorFixtureScenario.NoCodingPlan => File("glm-quota-no-plan.json", request),
                MonitorFixtureScenario.NoCodingPlanApiError => File("glm-quota-no-coding-plan.json", request),
                MonitorFixtureScenario.BothRefused or MonitorFixtureScenario.QuotaRefused =>
                    Text(HttpStatusCode.InternalServerError, null, request, "{\"error\":\"refused\"}"),
                _ => File("glm-quota-limits.json", request)
            };
        }

        if (url.Contains("query-customer-account-report", StringComparison.Ordinal))
        {
            return _scenario switch
            {
                MonitorFixtureScenario.AuthFailure => Text(HttpStatusCode.OK, "glm-report-auth-failed.json", request),
                MonitorFixtureScenario.WalletEndpointOnly or MonitorFixtureScenario.WalletUnavailable
                    or MonitorFixtureScenario.BothRefused =>
                    Text(HttpStatusCode.InternalServerError, null, request, "{\"error\":\"report refused\"}"),
                _ => File("glm-report-wallet.json", request)
            };
        }

        if (url.Contains("/api/paas/v4/balance", StringComparison.Ordinal))
        {
            return _scenario switch
            {
                MonitorFixtureScenario.AuthFailure => Text(HttpStatusCode.Unauthorized, "glm-balance-auth-failed.json", request),
                MonitorFixtureScenario.WalletUnavailable => Text(HttpStatusCode.NotFound, "glm-balance-v4-404.json", request),
                MonitorFixtureScenario.BothRefused =>
                    Text(HttpStatusCode.InternalServerError, null, request, "{\"error\":\"refused\"}"),
                _ => File("glm-balance-v4.json", request)
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
    }

    private static HttpResponseMessage File(string name, HttpRequestMessage request) =>
        Text(HttpStatusCode.OK, name, request);

    private static HttpResponseMessage Text(HttpStatusCode status, string? name, HttpRequestMessage request,
        string? inline = null)
    {
        var content = name is null ? inline ?? string.Empty : MonitorFixtures.ReadText(name);
        return new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// The app-server replay: hands the recorded reply to the same
/// <see cref="SystemCodexAppServer.ParseMessage"/> the live reader uses.
/// </summary>
internal sealed class FixtureCodexAppServer : ICodexAppServer
{
    private readonly string _fixture;

    public FixtureCodexAppServer(string fixture = "codex-rate-limits.json") => _fixture = fixture;

    /// <summary>The recorded success and the recorded "not logged in" reply.</summary>
    public static FixtureCodexAppServer LoggedIn() => new("codex-rate-limits.json");

    public static FixtureCodexAppServer LoggedOut() => new("codex-auth-required.json");

    public CodexAvailability Availability { get; init; } = CodexAvailability.DesktopRunning;

    public string Describe() => "fixture:" + _fixture;

    public Task<CodexUsage> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(SystemCodexAppServer.ParseMessage(MonitorFixtures.ReadText(_fixture)));
}
