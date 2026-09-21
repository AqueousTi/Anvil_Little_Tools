using System.Net;
using System.Reflection;
using System.Text;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Recorded sample responses for every stock data source, embedded in the
/// assembly so the parser and the chart can be exercised offline and
/// deterministically. Each file is the verbatim body of one live request made
/// with the URL the Windows module builds (including the GB18030 encoded Tencent
/// quote), so replaying it walks the real parsing path.
/// </summary>
internal static class StockFixtures
{
    private const string Prefix = "LittleTools.Assistant.Stock.Fixtures.";

    /// <summary>Request URL fragment to fixture file, first match wins.</summary>
    private static readonly (string Match, string File)[] Routes =
    [
        ("qt.gtimg.cn/q=sh513500", "tencent-sh513500.txt"),
        ("qt.gtimg.cn/q=sh510300", "tencent-sh510300.txt"),
        ("qt.gtimg.cn/q=sh600519", "tencent-sh600519.txt"),
        ("qt.gtimg.cn/q=sz159915", "tencent-sz159915.txt"),
        ("secid=1.600519&fields=f162", "eastmoney-pe-600519.json"),
        ("kline/get?secid=1.510300", "eastmoney-kline-daily-510300.json"),
        ("kline/get?secid=1.513500", "eastmoney-kline-weekly-513500.json"),
        ("kline/get?secid=1.600519", "eastmoney-kline-monthly-600519.json"),
        ("trends2/get?secid=1.510300", "eastmoney-trends-510300.json"),
        ("RPT_VALUEANALYSIS_DET", "eastmoney-valuation-600519.json"),
        ("index-perf?indexCode=000300", "csindex-000300.json"),
        // The tencent kline/minute endpoints belong to the fallback source, which
        // only runs when the eastmoney host refuses the request; see
        // StockFallbackSource for why that source exists on Linux.
        ("fqkline/get?param=sh510300,day", "tencent-kline-day-510300.json"),
        ("fqkline/get?param=sh510300,week", "tencent-kline-week-510300.json"),
        ("fqkline/get?param=sh510300,month", "tencent-kline-month-510300.json"),
        ("minute/query?code=sh510300", "tencent-minute-510300.json"),
        ("multpl.com/s-p-500-pe-ratio", "multpl-sp500-pe.html")
    ];

    private static readonly Assembly Source = typeof(StockFixtures).Assembly;

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
        using var stream = Find(name) ?? throw new FileNotFoundException("Missing stock fixture: " + name);
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

    /// <summary>A client for the recorded responses; unknown URLs answer 404.</summary>
    public static StockDataService CreateService(Func<DateTime>? now = null) =>
        new(new StockFixtureHandler(), now);

    /// <summary>The recorded response for a request, or null when none was recorded.</summary>
    internal static HttpResponseMessage? Replay(HttpRequestMessage request)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        var file = FileFor(url);
        if (file is null || !Exists(file)) return null;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(Read(file))
        };
    }
}

/// <summary>Serves the embedded fixtures, so tests and the render smoke need no network.</summary>
internal sealed class StockFixtureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(StockFixtures.Replay(request)
            ?? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
}
