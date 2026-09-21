using System.Net;
using System.Text;
using LittleTools.Assistant.Stock;

/// <summary>
/// Parser and source tests for the stock module. Every response comes from the
/// recorded fixtures embedded in the assembly (the verbatim body of one live
/// request per source), so the parse path is exercised without a network.
/// </summary>
internal static class StockDataTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        CheckFixtureSet(check);
        CheckQuoteParsing(check);
        await CheckQuotesAsync(check);
        CheckJsonParsing(check);
        await CheckCandleParsingAsync(check);
        await CheckValuationAsync(check);
        CheckUrls(check);
        await CheckFailuresAsync(check);
        await CheckValuationCacheAsync(check);
    }

    private static void CheckFixtureSet(Action<string, bool> check)
    {
        // One recorded response per source. The weekly/monthly kline variants are
        // optional: they share the daily parser and only differ by the klt
        // parameter, and the kline host refused some of the recording requests.
        foreach (var required in new[]
                 {
                     "tencent-sh513500.txt", "tencent-sh510300.txt", "tencent-sh600519.txt", "tencent-sz159915.txt",
                     "eastmoney-pe-600519.json", "eastmoney-kline-daily-510300.json", "eastmoney-trends-510300.json",
                     "eastmoney-valuation-600519.json", "csindex-000300.json", "multpl-sp500-pe.html"
                 })
            check("fixture recorded: " + required, StockFixtures.Exists(required));
        check("tencent fixture is route matched",
            StockFixtures.FileFor(StockDataService.QuoteUrl("513500")) == "tencent-sh513500.txt"
            && StockFixtures.FileFor(StockDataService.KlineUrl("510300", StockPeriods.Daily, 265)) == "eastmoney-kline-daily-510300.json"
            && StockFixtures.FileFor(StockDataService.Sp500Url) == "multpl-sp500-pe.html");
        check("unknown url is not routed", StockFixtures.FileFor("https://example.invalid/q=sh600000") is null);
    }

    private static void CheckQuoteParsing(Action<string, bool> check)
    {
        var now = new DateTime(2026, 9, 21, 13, 0, 0);

        var etf = StockDataService.ParseQuote(Tencent("tencent-sh513500.txt"), "513500", now);
        check("etf quote name", etf.Name == "标普500ETF博时");
        check("etf quote price", etf.Price == 2.709 && etf.PreviousClose == 2.685);
        check("etf quote change", etf.ChangePercent == 0.89);
        check("etf quote is detected from field 61", etf.IsEtf);
        check("etf quote premium comes from field 77", etf.PremiumPercent.HasValue && etf.PremiumPercent.Value == 10.82);
        check("etf quote iopv comes from field 78", etf.Iopv.HasValue && etf.Iopv.Value == 2.4444);
        check("etf quote time", etf.UpdatedAt == new DateTime(2026, 9, 21, 12, 5, 52));
        check("etf quote source label", etf.DataSource == "腾讯行情");
        check("etf quote has no pe before the second request", etf.Pe is null);

        var stock = StockDataService.ParseQuote(Tencent("tencent-sh600519.txt"), "600519", now);
        check("ordinary stock is not an etf", !stock.IsEtf);
        check("ordinary stock carries no premium", stock.PremiumPercent is null && stock.Iopv is null);
        check("ordinary stock price", stock.Price == 1251.57 && stock.ChangePercent == -0.44);
        check("ordinary stock name", stock.Name == "贵州茅台");

        var fund = StockDataService.ParseQuote(Tencent("tencent-sh510300.txt"), "510300", now);
        check("csi300 fund premium is negative", fund.PremiumPercent.HasValue && fund.PremiumPercent.Value == -0.07);
        check("csi300 fund iopv", fund.Iopv.HasValue && Math.Abs(fund.Iopv.Value - 4.5981) < 0.00001);

        var shenzhen = StockDataService.ParseQuote(Tencent("tencent-sz159915.txt"), "159915", now);
        check("shenzhen etf is decoded too", shenzhen.IsEtf && shenzhen.Name == "创业板ETF易方达"
            && shenzhen.PremiumPercent == -0.08);
    }

    private static async Task CheckQuotesAsync(Action<string, bool> check)
    {
        using var service = StockFixtures.CreateService(() => new DateTime(2026, 9, 21, 13, 0, 0));
        var stock = await service.GetQuoteAsync("600519");
        check("quote resolves the pe for an ordinary stock", stock.Pe.HasValue && stock.Pe.Value == 17.57);
        var fund = await service.GetQuoteAsync("513500");
        check("quote keeps the etf premium through the http path",
            fund.PremiumPercent == 10.82 && fund.Iopv == 2.4444 && fund.IsEtf);
        check("quote falls back to the price/iopv premium when field 77 is missing",
            StockDataService.ParseQuote(Tencent("tencent-sh513500.txt").Replace("~10.82~", "~~"), "513500",
                new DateTime(2026, 9, 21)).PremiumPercent is { } premium
            && Math.Abs(premium - Math.Round((2.709 / 2.4444 - 1) * 100, 10)) < 0.0001);
    }

    private static void CheckJsonParsing(Action<string, bool> check)
    {
        check("pe parses from the recorded response", StockDataService.ParseStockPe(Text("eastmoney-pe-600519.json")) == 17.57);
        check("missing pe data is null", StockDataService.ParseStockPe("{\"data\":null}") is null);
        check("non positive pe is rejected", StockDataService.ParseStockPe("{\"data\":{\"f162\":0}}") is null);
        check("string pe is accepted", StockDataService.ParseStockPe("{\"data\":{\"f162\":\"17.5\"}}") == 17.5);
        check("invalid json is reported", Throws(() => StockDataService.ParseStockPe("<html>nope</html>"), "数据源返回了无效JSON"));

        var klines = StockDataService.ParseKlines(
            "{\"data\":{\"klines\":[\"2026-09-18,1.5,1.7,1.8,1.4,100\",\"garbage\"]}}");
        check("kline parse count", klines.Count == 1);
        check("kline parse values", klines[0].Time == new DateTime(2026, 9, 18)
            && klines[0].Open == 1.5 && klines[0].Close == 1.7 && klines[0].High == 1.8 && klines[0].Low == 1.4);
        check("missing kline data is empty", StockDataService.ParseKlines("{\"data\":null}").Count == 0);

        var trends = StockDataService.ParseTrends(
            "{\"data\":{\"trends\":[\"2026-09-21 09:30,2.5,2.5,2.6,2.4,10\",\"\"]}}");
        check("trend parse count", trends.Count == 1);
        check("trend repeats the price as open and close",
            trends[0].Open == 2.5 && trends[0].Close == 2.5 && trends[0].High == 2.6 && trends[0].Low == 2.4
            && trends[0].Time == new DateTime(2026, 9, 21, 9, 30, 0));
        check("missing trend data is empty", StockDataService.ParseTrends("{\"data\":null}").Count == 0);

        var valuation = StockDataService.ParseStockValuation(
            "{\"result\":{\"data\":[{\"TRADE_DATE\":\"2026-09-18 00:00:00\",\"PE_TTM\":19.3},"
            + "{\"TRADE_DATE\":\"2026-09-17 00:00:00\",\"PE_TTM\":\"18.5\"},"
            + "{\"TRADE_DATE\":\"bad\",\"PE_TTM\":5},{\"TRADE_DATE\":\"2026-09-16 00:00:00\",\"PE_TTM\":-1}]}}");
        check("valuation rows parse", valuation.Count == 2);
        check("valuation date and value", valuation[0].Date == new DateTime(2026, 9, 18) && valuation[0].Value == 19.3
            && valuation[1].Value == 18.5);

        var csi = StockDataService.ParseCsi300(
            "{\"data\":[{\"tradeDate\":\"20260918\",\"peg\":13.42},{\"tradeDate\":\"20260917\",\"peg\":\"13.1\"},"
            + "{\"tradeDate\":\"2026-09-16\",\"peg\":12}]}");
        check("csindex rows parse from yyyyMMdd", csi.Count == 2 && csi[0].Date == new DateTime(2026, 9, 18) && csi[0].Value == 13.42);

        var sp500 = StockDataService.ParseSp500(
            "<table><tr><td>Sep 18, 2026</td><td>\n<abbr title=\"Estimate\">†</abbr>\n26.07\n</td></tr>"
            + "<tr><td>Aug 1, 2026</td><td>25.10</td></tr><tr><td>nonsense</td><td>1</td></tr></table>");
        check("multpl rows parse and the estimate marker is dropped",
            sp500.Count == 2 && sp500[0].Date == new DateTime(2026, 9, 18) && sp500[0].Value == 26.07);

        check("quote without a quoted body is reported",
            Throws(() => StockDataService.ParseQuote("v_sh513500=", "513500", DateTime.Now), "没有查询到该证券代码"));
        check("quote for another code is reported",
            Throws(() => StockDataService.ParseQuote(Tencent("tencent-sh513500.txt"), "510300", DateTime.Now), "行情数据格式异常"));
        check("short quote is reported",
            Throws(() => StockDataService.ParseQuote("x=\"1~2~3\"", "513500", DateTime.Now), "行情数据格式异常"));
        check("suspended quote is reported",
            Throws(() => StockDataService.ParseQuote(Tencent("tencent-sh513500.txt").Replace("~2.709~", "~0~"), "513500",
                DateTime.Now), "该证券当前没有有效行情"));
    }

    private static async Task CheckCandleParsingAsync(Action<string, bool> check)
    {
        using var service = StockFixtures.CreateService();

        var dailyFiveYears = await service.GetCandlesAsync("510300", StockPeriods.Daily, 5);
        check("daily fixture holds five years of bars", dailyFiveYears.Count == 1212);
        check("daily bars end on the recorded trading day", dailyFiveYears[^1].Time == new DateTime(2026, 9, 21));
        check("daily bar values survive the parse", dailyFiveYears[^1].Close == 4.595 && dailyFiveYears[^1].Low == 4.586);
        check("daily bars are ordered", dailyFiveYears.Zip(dailyFiveYears.Skip(1))
            .All(pair => pair.First.Time <= pair.Second.Time));

        var dailyOneYear = await service.GetCandlesAsync("510300", StockPeriods.Daily, 1);
        check("one year span is filtered from the series", dailyOneYear.Count == 242
            && dailyOneYear[0].Time >= new DateTime(2025, 9, 21));

        var dailyThreeYears = await service.GetCandlesAsync("510300", StockPeriods.Daily, 3);
        check("three year span crosses the line chart threshold",
            dailyThreeYears.Count == 726 && StockChartMath.IsLineMode(dailyThreeYears));

        check("weekly limit is the windows one", StockMath.KlineLimit(StockPeriods.Weekly, 1) == 56);
        if (StockFixtures.Exists("eastmoney-kline-weekly-513500.json"))
        {
            var weekly = await service.GetCandlesAsync("513500", StockPeriods.Weekly, 1);
            check("weekly series parses", weekly.Count > 20 && weekly[^1].Time == new DateTime(2026, 9, 18));
        }
        else
        {
            Console.WriteLine("SKIPPED weekly kline fixture: the kline host refused the recording request.");
        }

        if (StockFixtures.Exists("eastmoney-kline-monthly-600519.json"))
        {
            var monthly = await service.GetCandlesAsync("600519", StockPeriods.Monthly, 5);
            check("monthly series parses", monthly.Count > 12 && monthly.Count <= 62);
        }
        else
        {
            Console.WriteLine("SKIPPED monthly kline fixture: the kline host refused the recording request.");
        }

        // The weekly request must reach the same parser with klt 102 wired in.
        var weeklyFromDaily = await service.GetCandlesAsync("510300", StockPeriods.Weekly, 1);
        check("weekly request is parsed and range filtered", weeklyFromDaily.Count > 0
            && weeklyFromDaily.All(item => item.High >= item.Low));

        var minute = await service.GetMinuteAsync("510300");
        check("minute series parses", minute.Count == 121);
        // ParseTrends uses the minute's first field for both open and close, so the
        // chart draws the intraday line instead of candles (StockData.cs L237-L239).
        check("minute series repeats the price", minute.All(item => item.Open == item.Close));
        check("minute series is intraday", minute[0].Time == new DateTime(2026, 9, 21, 9, 30, 0)
            && minute[^1].Time == new DateTime(2026, 9, 21, 11, 30, 0));
        check("minute chart uses the line mode", StockChartMath.IsLineMode(minute));
    }

    private static async Task CheckValuationAsync(Action<string, bool> check)
    {
        using var service = StockFixtures.CreateService();

        var csi = await service.GetValuationAsync("510300", isEtf: true, stockPe: null, years: 1);
        check("510300 uses the official index pe", csi.Source == "中证指数官方");
        check("510300 pe value", csi.CurrentPe == 13.42);
        check("510300 pe date", csi.DataDate == new DateTime(2026, 9, 18));
        check("510300 sample count", csi.SampleCount == 243);
        check("510300 percentile", Math.Abs(csi.Percentile!.Value - 1.646091) < 0.01);

        var sp500 = await service.GetValuationAsync("513500", isEtf: true, stockPe: null, years: 1);
        check("513500 uses the monthly s&p pe table", sp500.Source == "Multpl · 月度PE");
        check("513500 pe value", sp500.CurrentPe == 26.07);
        check("513500 pe date", sp500.DataDate == new DateTime(2026, 9, 18));
        check("513500 sample count", sp500.SampleCount == 13);
        check("513500 percentile", Math.Abs(sp500.Percentile!.Value - 46.153846) < 0.01);

        var stock = await service.GetValuationAsync("600519", isEtf: false, stockPe: 17.57, years: 1);
        check("ordinary stock uses eastmoney pe-ttm history", stock.Source == "东方财富 · PE-TTM");
        check("ordinary stock pe value", Math.Abs(stock.CurrentPe!.Value - 19.2978715) < 0.000001);
        check("ordinary stock sample count", stock.SampleCount == 243);
        check("ordinary stock percentile", Math.Abs(stock.Percentile!.Value - 17.283951) < 0.01);

        var unmatched = await service.GetValuationAsync("159915", isEtf: true, stockPe: 12.3, years: 1);
        check("unmatched etf reports the placeholder source", unmatched.Source == "暂未匹配跟踪指数");
        check("unmatched etf keeps the quote pe", unmatched.CurrentPe == 12.3 && unmatched.Percentile is null);

        check("valuation source table",
            StockDataService.ResolveValuationSource("510300", true, null) == StockValuationSource.Csi300
            && StockDataService.ResolveValuationSource("513500", true, null) == StockValuationSource.Sp500
            && StockDataService.ResolveValuationSource("159612", true, "标普500ETF易方达") == StockValuationSource.Sp500
            && StockDataService.ResolveValuationSource("600519", false, "贵州茅台") == StockValuationSource.StockHistory
            && StockDataService.ResolveValuationSource("159915", true, "创业板ETF易方达") == StockValuationSource.Placeholder);

        // The history endpoint failing must fall back to the quote PE, as Windows does.
        using var partial = new StockDataService(new PartialHandler("RPT_VALUEANALYSIS_DET"), () => new DateTime(2026, 9, 21));
        var fallback = await partial.GetValuationAsync("600519", isEtf: false, stockPe: 17.57, years: 1);
        check("failed history falls back to the live pe", fallback.Source == "东方财富动态PE"
            && fallback.CurrentPe == 17.57 && fallback.DataDate == DateTime.Today);
    }

    private static void CheckUrls(Action<string, bool> check)
    {
        check("quote url", StockDataService.QuoteUrl("513500") == "https://qt.gtimg.cn/q=sh513500");
        check("pe url", StockDataService.StockPeUrl("600519")
            == "https://push2.eastmoney.com/api/qt/stock/get?fltt=2&secid=1.600519&fields=f162");
        check("kline url", StockDataService.KlineUrl("510300", StockPeriods.Daily, 265)
            == "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=1.510300"
            + "&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56&klt=101&fqt=1&end=20500101&lmt=265");
        check("weekly kline url uses klt 102", StockDataService.KlineUrl("513500", StockPeriods.Weekly, 56).Contains("klt=102&fqt=1", StringComparison.Ordinal));
        check("minute url", StockDataService.MinuteUrl("510300")
            == "https://push2.eastmoney.com/api/qt/stock/trends2/get?secid=1.510300"
            + "&fields1=f1,f2,f3,f4,f5,f6,f7,f8&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&iscca=0&ndays=1");
        check("valuation url", StockDataService.StockValuationUrl("600519", 290)
            == "https://datacenter-web.eastmoney.com/api/data/v1/get?reportName=RPT_VALUEANALYSIS_DET"
            + "&columns=SECURITY_CODE%2CTRADE_DATE%2CPE_TTM&filter=(SECURITY_CODE%3D%22600519%22)"
            + "&pageNumber=1&pageSize=290&sortTypes=-1&sortColumns=TRADE_DATE&source=WEB&client=WEB");
        check("sp500 url", StockDataService.Sp500Url == "https://www.multpl.com/s-p-500-pe-ratio/table/by-month");
        check("csindex url", StockDataService.Csi300Url(new DateTime(2025, 9, 14), new DateTime(2026, 9, 21))
            == "https://www.csindex.com.cn/csindex-home/perf/index-perf?indexCode=000300&startDate=20250914&endDate=20260921");
    }

    private static async Task CheckFailuresAsync(Action<string, bool> check)
    {
        using var service = StockFixtures.CreateService();
        var quoteFailed = false;
        try { await service.GetQuoteAsync("000002"); }
        catch (HttpRequestException) { quoteFailed = true; }
        check("an unrecorded quote surfaces the http failure", quoteFailed);

        var klineFailed = false;
        try { await service.GetCandlesAsync("000002", StockPeriods.Daily, 1); }
        catch (HttpRequestException) { klineFailed = true; }
        check("an unrecorded kline surfaces the http failure", klineFailed);

        var invalidFailed = false;
        try { await service.GetQuoteAsync("12"); }
        catch (InvalidOperationException exception) { invalidFailed = exception.Message == "请输入六位沪深证券代码"; }
        check("an invalid code never hits the network", invalidFailed);
    }

    private static async Task CheckValuationCacheAsync(Action<string, bool> check)
    {
        var now = new DateTime(2026, 9, 21, 9, 0, 0);
        var counting = new CountingHandler();
        using var service = new StockDataService(counting, () => now);

        await service.GetValuationAsync("510300", true, null, 1);
        var afterFirst = counting.Requests;
        await service.GetValuationAsync("510300", true, null, 1);
        check("valuation cache avoids a second request", counting.Requests == afterFirst);

        now = now.AddHours(7);
        await service.GetValuationAsync("510300", true, null, 1);
        check("cache expires after six hours", counting.Requests > afterFirst);
    }

    // ---------------------------------------------------------------- helpers

    private static string Tencent(string fixture) =>
        Encoding.GetEncoding("GB18030").GetString(StockFixtures.Read(fixture));

    private static string Text(string fixture) => StockFixtures.ReadText(fixture);

    private static bool Throws(Action action, string message)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception exception)
        {
            return exception.Message == message;
        }
    }

    /// <summary>Serves the fixtures but refuses one URL, to exercise a fallback path.</summary>
    private sealed class PartialHandler(string refused) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.ToString().Contains(refused, StringComparison.Ordinal) == true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });
            return Task.FromResult(StockFixtures.Replay(request)
                ?? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(StockFixtures.Replay(request)
                ?? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }
}
