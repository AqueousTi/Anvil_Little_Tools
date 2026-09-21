using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Deterministic render check for the stock module, mirroring the todo module's
/// render smoke. It drives the real capsule and detail windows with the recorded
/// market data responses and writes one PNG per screen, then asserts the realised
/// chart geometry and the drawn colours from the pixels, so the A-share colour
/// rule (red up, green down), the premium outline, the axis labels and the empty
/// placeholder cannot regress silently.
///
/// Determinism rules this file follows:
///   * the run owns its state: a throwaway data directory is recreated and seeded
///     here, and <c>StockStore.Load</c> is never called, so neither the previous
///     run's settings.json nor the legacy import path can decide what is tested;
///   * every step waits for the condition it needs (a quote, a painted chart, a
///     status line) instead of sleeping a fixed time;
///   * failure branches are triggered by a refusing handler, never by the absence
///     of a fixture or by the network.
/// </summary>
internal static class StockSmoke
{
    private static readonly DateTime FixedNow = new(2026, 9, 21, 13, 0, 0);

    /// <summary>Colour tolerance for the pixel scan, in one channel.</summary>
    private const int Tolerance = 2;

    /// <summary>How long a single condition may take before the run fails.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>The only code with a fully recorded series; every chart case uses it.</summary>
    private const string ChartCode = "510300";

    /// <summary>The exact renders a successful run produces, asserted at the end.</summary>
    private static readonly string[] ExpectedArtifacts =
    [
        "stock-compact-513500.png", "stock-compact-510300.png", "stock-compact-600519.png",
        "stock-details-daily.png", "stock-details-daily-3y.png", "stock-details-minute.png",
        "stock-details-weekly.png", "stock-details-monthly.png",
        "stock-details-513500.png", "stock-details-kline-missing.png", "stock-details-513500-valuation.png",
        "stock-details-600519.png", "stock-details-empty.png"
    ];

    public static async Task RunAsync(string directory, bool live)
    {
        Directory.CreateDirectory(directory);

        // Self contained state. The directory is recreated so a previous run's
        // settings.json cannot survive, and the settings are seeded explicitly so
        // the run never depends on what the machine had selected. Load() is not
        // called, which also keeps StockStore's legacy import (the
        // LITTLETOOLS_STOCK_DATA override and the XDG candidates) out of the test.
        var dataDirectory = Path.Combine(directory, "data");
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
        Directory.CreateDirectory(dataDirectory);
        var store = new StockStore(dataDirectory);
        var settings = BuildSettings();
        if (!store.Save(settings))
            throw new InvalidOperationException("The smoke could not seed " + store.DataPath + ".");

        var handler = new SmokeHandler();
        using var service = new StockDataService(handler, () => FixedNow);
        var window = new StockWindow(store, settings, service, edgeHideEnabled: false);
        var report = new List<string> { "state: " + DescribeState(settings) + " | data=" + store.DataPath };

        window.Show();
        await SmokeWaiter.WaitOrThrowAsync(() => window.IsVisible, Wait, () => "the capsule window to show");
        await window.RefreshAllMonitoredAsync(true);
        await SmokeWaiter.WaitOrThrowAsync(() => window.QuoteFor("510300") is not null, Wait,
            () => "the 510300 quote; capsule=" + window.DescribeCompactForSmoke());

        // ------------------------------------------------------ capsule renders
        window.SelectCode("513500");
        await window.RefreshSelectedAsync(true);
        var etf = await WaitForDescriptionAsync(window.DescribeCompactForSmoke,
            text => text.Contains("price=2.709", StringComparison.Ordinal), "the 513500 capsule quote");
        Save(window, Path.Combine(directory, "stock-compact-513500.png"));
        report.Add("compact-513500: " + etf);
        Expect(etf.Contains("name=标普500ETF", StringComparison.Ordinal)
            && etf.Contains("metric=参考溢价", StringComparison.Ordinal)
            && etf.Contains("premium=10.82%", StringComparison.Ordinal)
            && etf.Contains("outline=False", StringComparison.Ordinal)
            && etf.Contains("changeColor=#FFEF585B", StringComparison.Ordinal),
            "513500 capsule", etf);

        window.SelectCode("510300");
        await window.RefreshSelectedAsync(true);
        var lowPremium = await WaitForDescriptionAsync(window.DescribeCompactForSmoke,
            text => text.Contains("price=4.595", StringComparison.Ordinal), "the 510300 capsule quote");
        Save(window, Path.Combine(directory, "stock-compact-510300.png"));
        report.Add("compact-510300: " + lowPremium);
        Expect(lowPremium.Contains("premium=-0.07%", StringComparison.Ordinal)
            && lowPremium.Contains("outline=True", StringComparison.Ordinal),
            "510300 capsule keeps the outlined premium under 2%", lowPremium);

        // An ordinary stock shows its rolling PE and falls, so the change colour is green.
        window.SelectCode("600519");
        await window.RefreshSelectedAsync(true);
        var stock = await WaitForDescriptionAsync(window.DescribeCompactForSmoke,
            text => text.Contains("price=1251.57", StringComparison.Ordinal), "the 600519 capsule quote");
        Save(window, Path.Combine(directory, "stock-compact-600519.png"));
        report.Add("compact-600519: " + stock);
        Expect(stock.Contains("metric=滚动 PE", StringComparison.Ordinal)
            && stock.Contains("premium=17.57×", StringComparison.Ordinal)
            && stock.Contains("outline=False", StringComparison.Ordinal)
            && stock.Contains("changeColor=#FF7CEDAE", StringComparison.Ordinal),
            "600519 capsule shows the PE and the falling colour", stock);

        // ------------------------------------------------------ detail renders
        window.SelectCode(ChartCode);
        await window.RefreshSelectedAsync(true);
        var details = await OpenDetailsAsync(window);
        await DriveChartAsync(window, details, StockPeriods.Daily, 1, expectedCandles: 242);
        await window.RefreshDetailsAsync();
        var dailyDescription = await WaitForDescriptionAsync(details.DescribeDetailsForSmoke,
            text => text.Contains("pe=13.42×", StringComparison.Ordinal), "the 510300 detail window");
        Save(details, Path.Combine(directory, "stock-details-daily.png"));
        report.Add("details-510300-daily: " + dailyDescription);
        Expect(dailyDescription.Contains("symbol=510300", StringComparison.Ordinal)
            && dailyDescription.Contains("price=4.595", StringComparison.Ordinal)
            && dailyDescription.Contains("metric=参考溢价", StringComparison.Ordinal)
            && dailyDescription.Contains("instrument=IOPV", StringComparison.Ordinal)
            && dailyDescription.Contains("instrumentValue=4.5981", StringComparison.Ordinal)
            && dailyDescription.Contains("valuation=中证指数官方 · 2026-09-18", StringComparison.Ordinal)
            && dailyDescription.Contains("monitor=移出监控", StringComparison.Ordinal)
            && dailyDescription.Contains("alert=提醒 < 2%,alertEnabled=True", StringComparison.Ordinal)
            && dailyDescription.Contains("tabs=2", StringComparison.Ordinal)
            && dailyDescription.Contains("period=Daily", StringComparison.Ordinal)
            && dailyDescription.Contains("range=1", StringComparison.Ordinal),
            "510300 detail window content", dailyDescription);

        await RenderChartAsync(window, details, report, directory, "stock-details-daily.png",
            StockPeriods.Daily, 1, expectedCandles: 242, lineMode: false, candlesDrawn: true);
        await RenderChartAsync(window, details, report, directory, "stock-details-daily-3y.png",
            StockPeriods.Daily, 3, expectedCandles: 726, lineMode: true, candlesDrawn: false);
        await RenderChartAsync(window, details, report, directory, "stock-details-minute.png",
            StockPeriods.Minute, 1, expectedCandles: 121, lineMode: true, candlesDrawn: false);
        await RenderChartAsync(window, details, report, directory, "stock-details-weekly.png",
            StockPeriods.Weekly, 1, expectedCandles: -1, lineMode: false, candlesDrawn: null);
        await RenderChartAsync(window, details, report, directory, "stock-details-monthly.png",
            StockPeriods.Monthly, 5, expectedCandles: -1, lineMode: false, candlesDrawn: null);

        // The S&P tracker shows the quote plus the monthly multpl valuation.
        window.SelectCode("513500");
        await window.RefreshSelectedAsync(true);
        await DriveChartAsync(window, details, StockPeriods.Daily, 1, expectedCandles: -1);
        var sp500 = details.DescribeDetailsForSmoke();
        Save(details, Path.Combine(directory, "stock-details-513500.png"));
        report.Add("details-513500: " + sp500);
        Expect(sp500.Contains("changeColor=#FFEF585B", StringComparison.Ordinal)
            && sp500.Contains("alert=提醒 < 2%  ✓", StringComparison.Ordinal)
            && sp500.Contains("premium=10.82%", StringComparison.Ordinal),
            "513500 detail shows the quote and the re-armed alert", sp500);

        // ------------------------------------------- the failed refresh branch
        // Triggered deterministically: the handler refuses every K line and intraday
        // request (eastmoney and the tencent fallback), so the refresh cannot
        // succeed regardless of which fixtures exist or whether a network is up.
        // The UI must say so and draw the placeholder instead of stale or invented
        // data. The unit tests pin the same promise with an injected failure.
        handler.RefuseCandles = true;
        window.SelectCode(ChartCode);
        await window.RefreshSelectedAsync(true);
        await window.RefreshDetailsAsync();
        await WaitForDescriptionAsync(details.DescribeDetailsForSmoke,
            text => text.Contains("部分数据暂不可用", StringComparison.Ordinal), "the refused refresh to be reported");
        await SmokeWaiter.WaitOrThrowAsync(
            () => details.ChartForSmoke.LastRender is { CandleCount: 0 } && details.ChartForSmoke.LastRenderWasEmpty,
            Wait, () => "the empty chart placeholder; chart=" + DescribeChart(details));
        var missing = details.DescribeDetailsForSmoke();
        Save(details, Path.Combine(directory, "stock-details-kline-missing.png"));
        report.Add("details-510300-kline-missing: " + missing + " | chart=" + DescribeChart(details));
        Expect(missing.Contains("部分数据暂不可用", StringComparison.Ordinal)
            && missing.Contains("valuation=估值数据暂不可用", StringComparison.Ordinal)
            && missing.Contains("percentile=样本不足", StringComparison.Ordinal),
            "a failed detail refresh clears the chart and reports it", missing);
        var refusedColors = CountChartColors(details, ChartRect(details));
        report.Add("pixels-details-510300-kline-missing: " + refusedColors);
        Expect(refusedColors.Up == 0 && refusedColors.Down == 0 && refusedColors.Line == 0,
            "a failed refresh draws no bars and no line", refusedColors.ToString());

        // The valuation card itself still renders from its recorded series.
        handler.RefuseCandles = false;
        details.SetChartData(await service.GetCandlesAsync(ChartCode, StockPeriods.Daily, 1));
        details.RenderValuation(await service.GetValuationAsync("513500", true, null, 1));
        await WaitForChartAsync(details, info => info.CandleCount == 242);
        Save(details, Path.Combine(directory, "stock-details-513500-valuation.png"));
        var rendered = details.DescribeDetailsForSmoke();
        report.Add("details-513500-valuation: " + rendered);
        Expect(rendered.Contains("pe=26.07×", StringComparison.Ordinal)
            && rendered.Contains("percentile=1年 · 46%", StringComparison.Ordinal)
            && rendered.Contains("valuation=Multpl · 月度PE · 2026-09-18", StringComparison.Ordinal),
            "513500 detail shows the S&P valuation", rendered);

        // 600519 has no recorded kline series either, so the same two step check
        // applies: the refused refresh, then the rendered PE-TTM valuation.
        handler.RefuseCandles = true;
        window.SelectCode("600519");
        await window.RefreshSelectedAsync(true);
        await window.RefreshDetailsAsync();
        await SmokeWaiter.WaitOrThrowAsync(
            () => details.ChartForSmoke.LastRender is { CandleCount: 0 } && details.ChartForSmoke.LastRenderWasEmpty,
            Wait, () => "the empty chart placeholder for 600519; chart=" + DescribeChart(details));
        var failedOrdinary = details.DescribeDetailsForSmoke();
        report.Add("details-600519-kline-missing: " + failedOrdinary);
        Expect(failedOrdinary.Contains("部分数据暂不可用", StringComparison.Ordinal)
            && failedOrdinary.Contains("valuation=估值数据暂不可用", StringComparison.Ordinal),
            "a failed 600519 refresh clears the valuation", failedOrdinary);

        handler.RefuseCandles = false;
        details.SetChartData(await service.GetCandlesAsync(ChartCode, StockPeriods.Daily, 1));
        details.RenderValuation(await service.GetValuationAsync("600519", false, 17.57, 1, "贵州茅台"));
        await WaitForChartAsync(details, info => info.CandleCount == 242);
        Save(details, Path.Combine(directory, "stock-details-600519.png"));
        var ordinary = details.DescribeDetailsForSmoke();
        report.Add("details-600519: " + ordinary);
        Expect(ordinary.Contains("instrument=证券类型", StringComparison.Ordinal)
            && ordinary.Contains("instrumentValue=普通股票", StringComparison.Ordinal)
            && ordinary.Contains("pe=19.30×", StringComparison.Ordinal)
            && ordinary.Contains("valuation=东方财富 · PE-TTM · 2026-09-18", StringComparison.Ordinal)
            && ordinary.Contains("alertEnabled=False", StringComparison.Ordinal)
            && ordinary.Contains("changeColor=#FF7CEDAE", StringComparison.Ordinal),
            "600519 detail shows the stock valuation and the disabled premium alert", ordinary);

        // An empty series has to draw the placeholder text and no candles at all.
        details.SetChartData(null);
        await SmokeWaiter.WaitOrThrowAsync(
            () => details.ChartForSmoke.LastRender is { CandleCount: 0 } && details.ChartForSmoke.LastRenderWasEmpty,
            Wait, () => "the empty chart placeholder; chart=" + DescribeChart(details));
        Save(details, Path.Combine(directory, "stock-details-empty.png"));
        var empty = details.ChartForSmoke.LastRender
            ?? throw new InvalidOperationException("The empty chart did not paint.");
        report.Add("chart-empty: " + empty + ", placeholder=" + details.ChartForSmoke.LastRenderWasEmpty);
        Expect(empty.CandleCount == 0, "empty chart reports no bars", empty.ToString());
        var emptyColors = CountChartColors(details, ChartRect(details));
        report.Add("pixels-stock-details-empty.png: " + emptyColors);
        Expect(emptyColors.Up == 0 && emptyColors.Down == 0 && emptyColors.Line == 0,
            "empty chart draws no bars", emptyColors.ToString());

        // The artifact set is part of the contract: the same run must always write
        // the same renders, which is what makes two consecutive runs comparable.
        var produced = Directory.GetFiles(directory, "*.png").Select(Path.GetFileName).OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        report.Add("artifacts: " + produced.Length + " -> " + string.Join(", ", produced));
        Expect(produced.SequenceEqual(ExpectedArtifacts.OrderBy(name => name, StringComparer.Ordinal)),
            "rendered artifact set", string.Join(", ", produced));

        var text = string.Join('\n', report);
        Console.WriteLine(text);
        File.WriteAllText(Path.Combine(directory, "stock-chart.txt"), text + Environment.NewLine);

        window.CloseDetails();
        await SmokeWaiter.WaitOrThrowAsync(() => !window.IsDetailsOpen, Wait, () => "the detail window to close");
        window.Close();
        await SmokeWaiter.WaitAsync(() => !window.IsVisible, TimeSpan.FromSeconds(2));

        if (live) await RunLiveAsync(directory);
    }

    /// <summary>
    /// The state the smoke runs against, written by the smoke itself. The watch
    /// list matches what <see cref="StockStore.Normalize"/> seeds, so the tab count
    /// and the alert captions are the product defaults, and the selection is the
    /// one code with a complete recorded series.
    /// </summary>
    private static StockSettings BuildSettings() => new()
    {
        Watched =
        [
            new StockWatchEntry { Code = "510300", AlertEnabled = false, PremiumThreshold = 2.0 },
            new StockWatchEntry { Code = "513500", AlertEnabled = true, PremiumThreshold = 2.0 }
        ],
        SelectedCode = ChartCode,
        Topmost = false,
        Compact = false,
        KlinePeriod = StockPeriods.Daily,
        RangeYears = 1,
        Left = double.NaN,
        Top = double.NaN
    };

    private static string DescribeState(StockSettings settings) =>
        "selected=" + settings.SelectedCode + ",period=" + settings.KlinePeriod
        + ",years=" + settings.RangeYears.ToString(CultureInfo.InvariantCulture)
        + ",topmost=" + settings.Topmost + ",compact=" + settings.Compact
        + ",watched=" + string.Join("|", settings.Watched.Select(entry => entry.Code));

    /// <summary>
    /// The opt-in live pass. It never falls back to the fixtures: when the market
    /// data hosts are unreachable it writes the failure next to the renders and
    /// fails the smoke, so a blocked network cannot look like a success.
    /// </summary>
    private static async Task RunLiveAsync(string directory)
    {
        using var service = new StockDataService();
        var dataDirectory = Path.Combine(directory, "live-data");
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
        Directory.CreateDirectory(dataDirectory);
        var store = new StockStore(dataDirectory);
        var settings = BuildSettings();
        store.Save(settings);
        try
        {
            var quote = await service.GetQuoteAsync("513500");
            var candles = await service.GetCandlesAsync(ChartCode, StockPeriods.Daily, 1);
            var minute = await service.GetMinuteAsync(ChartCode);
            var valuation = await service.GetValuationAsync(ChartCode, true, null, 1);
            Console.WriteLine($"LIVE capsule: {quote.Code} {quote.Name} {quote.Price} {quote.ChangePercent:0.00}% "
                + $"premium={quote.PremiumPercent:0.00} iopv={quote.Iopv}");
            Console.WriteLine($"LIVE candles: daily={candles.Count} minute={minute.Count} "
                + $"pe={valuation.CurrentPe} samples={valuation.SampleCount} source={valuation.Source}");
            File.WriteAllText(Path.Combine(directory, "stock-live.txt"),
                $"quote={quote.Code} {quote.Name} {quote.Price} {quote.ChangePercent:0.00}% premium={quote.PremiumPercent:0.00}"
                + Environment.NewLine
                + $"daily={candles.Count} minute={minute.Count} pe={valuation.CurrentPe} samples={valuation.SampleCount}"
                + Environment.NewLine);

            var window = new StockWindow(store, settings, service, edgeHideEnabled: false);
            window.Show();
            await window.RefreshAllMonitoredAsync(true);
            await SmokeWaiter.WaitOrThrowAsync(() => window.QuoteFor(ChartCode) is not null, Wait,
                () => "the live quote");
            Save(window, Path.Combine(directory, "stock-live-compact.png"));
            var details = await OpenDetailsAsync(window);
            await window.RefreshDetailsAsync();
            await WaitForChartAsync(details, info => info.CandleCount > 0);
            Save(details, Path.Combine(directory, "stock-live-details.png"));
            window.CloseDetails();
            window.Close();
            await SmokeWaiter.WaitAsync(() => !window.IsVisible, TimeSpan.FromSeconds(2));
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(directory, "stock-live.error.txt"), exception.ToString());
            throw new InvalidOperationException("The live market data pass failed: " + exception.Message, exception);
        }
    }

    // ------------------------------------------------------------ chart driving

    private static async Task RenderChartAsync(StockWindow window, StockDetailsWindow details, List<string> report,
        string directory, string file, string period, int years, int expectedCandles, bool lineMode, bool? candlesDrawn)
    {
        var info = await DriveChartAsync(window, details, period, years, expectedCandles);
        Save(details, Path.Combine(directory, file));

        var chart = details.ChartForSmoke;
        report.Add("chart-" + file + ": " + info + ", labels=" + string.Join("|", chart.LastLabels)
            + " | " + details.DescribeDetailsForSmoke());
        if (expectedCandles >= 0)
            Expect(info.CandleCount == expectedCandles, file + " candle count", info.ToString());
        else
            Expect(info.CandleCount > 4, file + " has bars", info.ToString());
        if (expectedCandles >= 0)
            Expect(info.LineMode == lineMode, file + " chart mode", info.ToString());
        // The realised bar width and step must match the Windows formulas.
        Expect(Math.Abs(info.Step * info.CandleCount - info.PlotWidth) < 0.001,
            file + " step covers the plot", info.ToString());
        Expect(info.BodyWidth >= 1 && info.BodyWidth <= 8, file + " body width is clamped", info.ToString());
        Expect(info.Min <= info.Max, file + " price range is valid", info.ToString());

        // The label invariant: the drawn label is the formatted candle time, never
        // the format string ("MM-dd" / "HH:mm") the first port printed, and it uses
        // the date or the time format the Windows source picks.
        var expectedLabels = StockChartMath.LabelIndices(chart.Candles.Count)
            .Select(index => StockChartMath.TimeLabel(chart.Candles[index].Time)).ToArray();
        Expect(chart.LastLabels.SequenceEqual(expectedLabels), file + " x axis labels are the candle times",
            string.Join("|", chart.LastLabels) + " expected " + string.Join("|", expectedLabels));
        var intraday = period == StockPeriods.Minute;
        var pattern = intraday ? new Regex(@"^\d{2}:\d{2}$") : new Regex(@"^\d{2}-\d{2}$");
        foreach (var label in chart.LastLabels)
            Expect(pattern.IsMatch(label), file + " x axis label is a formatted value, not the format string",
                "label=" + label + " labels=" + string.Join("|", chart.LastLabels));

        var rect = ChartRect(details);
        Expect(rect.Width > 300 && rect.Height > 150, file + " chart is laid out", rect.ToString());
        var colors = CountChartColors(details, rect);
        report.Add("pixels-" + file + ": " + colors);
        if (candlesDrawn == true)
            Expect(colors.Up > 0 && colors.Down > 0, file + " draws red and grey bars", colors.ToString());
        else if (candlesDrawn == false)
            Expect(colors.Up == 0 && colors.Down == 0 && colors.Line > 0,
                file + " draws the intraday line only", colors.ToString());
    }

    /// <summary>
    /// Drives the period and span choice until the chart really holds the expected
    /// series. The window refreshes in the background as well (opening the detail
    /// window posts one, and the 60 second timer can add another), and a refresh
    /// that loses the version race returns without touching the chart, so the
    /// choice is re-applied and the refresh re-issued until the chart matches.
    /// </summary>
    private static async Task<StockChartRenderInfo> DriveChartAsync(StockWindow window, StockDetailsWindow details,
        string period, int years, int expectedCandles)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        StockChartRenderInfo? last = null;
        var rounds = 0;
        while (DateTime.UtcNow < deadline)
        {
            rounds++;
            window.SetChoice(true, period);
            window.SetChoice(false, years.ToString(CultureInfo.InvariantCulture));
            details.UpdateChoiceButtons();
            await window.RefreshDetailsAsync();
            var painted = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < painted)
            {
                last = details.ChartForSmoke.LastRender;
                if (last is not null && (expectedCandles < 0 || last.CandleCount == expectedCandles)) return last;
                await SmokeWaiter.PumpAsync();
            }
        }
        throw new InvalidOperationException(
            "The chart never reached " + (expectedCandles < 0 ? "a series" : expectedCandles + " candles")
            + " for " + period + "/" + years.ToString(CultureInfo.InvariantCulture) + " after " + rounds
            + " refresh rounds. Last render: " + (last?.ToString() ?? "no-render")
            + "; chart=" + DescribeChart(details) + "; details=" + details.DescribeDetailsForSmoke());
    }

    private static async Task<StockChartRenderInfo> WaitForChartAsync(StockDetailsWindow details, Func<StockChartRenderInfo, bool> accept)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        StockChartRenderInfo? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = details.ChartForSmoke.LastRender;
            if (last is not null && accept(last)) return last;
            await SmokeWaiter.PumpAsync();
        }
        throw new InvalidOperationException("The chart never reached the expected state. Last render: "
            + (last?.ToString() ?? "no-render") + "; chart=" + DescribeChart(details));
    }

    /// <summary>
    /// Opens the detail window and returns it once it is really visible. The window
    /// closes itself when it loses focus, and ToggleDetails closes instead of opens
    /// while one is still visible, so the state is settled first and opening is
    /// retried; a run that cannot open it reports the window state it saw.
    /// </summary>
    private static async Task<StockDetailsWindow> OpenDetailsAsync(StockWindow window)
    {
        window.CloseDetails();
        await SmokeWaiter.WaitOrThrowAsync(() => !window.IsDetailsOpen, TimeSpan.FromSeconds(2),
            () => "the detail window to start closed; details=" + (window.DetailsForSmoke?.DescribeDetailsForSmoke() ?? "none"));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            window.ToggleDetails();
            if (await SmokeWaiter.WaitAsync(() => window.DetailsForSmoke is { IsVisible: true }, TimeSpan.FromSeconds(3)))
                return window.DetailsForSmoke!;
            window.CloseDetails();
            await SmokeWaiter.PumpAsync();
        }
        throw new InvalidOperationException("The detail window did not open after 3 attempts (capsule="
            + window.DescribeCompactForSmoke() + ").");
    }

    /// <summary>Polls a window description until it satisfies the predicate.</summary>
    private static async Task<string> WaitForDescriptionAsync(Func<string> describe, Func<string, bool> accept, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        var last = describe();
        while (!accept(last) && DateTime.UtcNow < deadline)
        {
            await SmokeWaiter.PumpAsync();
            last = describe();
        }
        if (!accept(last))
            throw new InvalidOperationException("Timed out waiting for " + what + ": " + last);
        return last;
    }

    // ---------------------------------------------------------------- helpers

    private static string DescribeChart(StockDetailsWindow details) =>
        (details.ChartForSmoke.LastRender?.ToString() ?? "no-render")
        + ",placeholder=" + details.ChartForSmoke.LastRenderWasEmpty
        + ",labels=" + string.Join("|", details.ChartForSmoke.LastLabels);

    private static void Save(Window window, string path)
    {
        var scale = window.RenderScaling <= 0 ? 1 : window.RenderScaling;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        using var stream = File.Create(path);
        bitmap.Save(stream, new PngBitmapEncoderOptions());
    }

    private static PixelRect ChartRect(StockDetailsWindow details)
    {
        var chart = details.ChartForSmoke;
        var scale = details.RenderScaling <= 0 ? 1 : details.RenderScaling;
        var origin = chart.TranslatePoint(new Point(0, 0), details) ?? new Point(0, 0);
        return new PixelRect(
            (int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale),
            Math.Max(1, (int)Math.Round(chart.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Round(chart.Bounds.Height * scale)));
    }

    private static void Expect(bool condition, string name, string detail)
    {
        if (!condition) throw new InvalidOperationException("Unexpected " + name + ": " + detail);
    }

    /// <summary>Counts the exact chart colours the Windows renderer uses.</summary>
    private static (int Up, int Down, int Line) CountChartColors(StockDetailsWindow details, PixelRect rect)
    {
        var scale = details.RenderScaling <= 0 ? 1 : details.RenderScaling;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(details.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(details.Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(details);

        var width = Math.Min(rect.Width, size.Width - rect.X);
        var height = Math.Min(rect.Height, size.Height - rect.Y);
        if (width <= 0 || height <= 0) return (0, 0, 0);
        var source = new PixelRect(rect.X, rect.Y, width, height);
        var stride = width * 4;
        var buffer = Marshal.AllocHGlobal(stride * height);
        try
        {
            bitmap.CopyPixels(source, buffer, stride * height, stride);
            var bytes = new byte[stride * height];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            // RenderTargetBitmap is BGRA on every backend this app targets.
            var bgra = (bitmap.Format ?? PixelFormat.Bgra8888).Equals(PixelFormat.Bgra8888);
            int up = 0, down = 0, line = 0;
            for (var index = 0; index + 3 < bytes.Length; index += 4)
            {
                var c0 = bytes[index];
                var c1 = bytes[index + 1];
                var c2 = bytes[index + 2];
                var blue = bgra ? c0 : c2;
                var green = c1;
                var red = bgra ? c2 : c0;
                if (Near(red, green, blue, StockPalette.UpCandle)) up++;
                else if (Near(red, green, blue, StockPalette.DownCandle)) down++;
                else if (Near(red, green, blue, StockPalette.LineStroke)) line++;
            }
            return (up, down, line);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool Near(byte red, byte green, byte blue, Color expected) =>
        Math.Abs(red - expected.R) <= Tolerance
        && Math.Abs(green - expected.G) <= Tolerance
        && Math.Abs(blue - expected.B) <= Tolerance;

    /// <summary>
    /// Serves the recorded responses and can refuse every candle request on
    /// demand, so the failure branch is exercised deterministically instead of
    /// depending on a fixture being absent.
    /// </summary>
    private sealed class SmokeHandler : HttpMessageHandler
    {
        /// <summary>When true every K line and intraday request fails with a 404.</summary>
        public bool RefuseCandles { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (RefuseCandles && IsCandleRequest(request.RequestUri?.ToString() ?? string.Empty))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            return Task.FromResult(StockFixtures.Replay(request)
                ?? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }

        private static bool IsCandleRequest(string url) =>
            url.Contains("kline/get", StringComparison.Ordinal)       // eastmoney daily/weekly/monthly
            || url.Contains("trends2/get", StringComparison.Ordinal)   // eastmoney intraday
            || url.Contains("fqkline", StringComparison.Ordinal)       // tencent fallback
            || url.Contains("minute/query", StringComparison.Ordinal); // tencent intraday fallback
    }
}
