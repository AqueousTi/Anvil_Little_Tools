using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Deterministic render check for the stock module, mirroring the todo module's
/// render smoke. It drives the real capsule and detail windows with the recorded
/// market data responses and writes one PNG per screen, then asserts the realised
/// chart geometry and the drawn colours from the pixels, so the A-share colour
/// rule (red up, green down) and the premium outline cannot regress silently.
/// </summary>
internal static class StockSmoke
{
    private static readonly DateTime FixedNow = new(2026, 9, 21, 13, 0, 0);

    /// <summary>Colour tolerance for the pixel scan, in one channel.</summary>
    private const int Tolerance = 2;

    public static async Task RunAsync(string directory, bool live)
    {
        Directory.CreateDirectory(directory);
        var dataDirectory = Path.Combine(directory, "data");
        Directory.CreateDirectory(dataDirectory);

        var store = new StockStore(dataDirectory);
        var settings = store.Load();
        using var service = StockFixtures.CreateService(() => FixedNow);
        var window = new StockWindow(store, settings, service, edgeHideEnabled: false);
        window.Show();
        await Settle();
        await window.RefreshAllMonitoredAsync(true);
        await Settle();

        var report = new List<string>();

        // ------------------------------------------------------ capsule renders
        window.SelectCode("513500");
        await window.RefreshSelectedAsync(true);
        await Settle();
        Save(window, Path.Combine(directory, "stock-compact-513500.png"));
        var etf = window.DescribeCompactForSmoke();
        report.Add("compact-513500: " + etf);
        Expect(etf.Contains("price=2.709", StringComparison.Ordinal)
            && etf.Contains("name=标普500ETF", StringComparison.Ordinal)
            && etf.Contains("metric=参考溢价", StringComparison.Ordinal)
            && etf.Contains("premium=10.82%", StringComparison.Ordinal)
            && etf.Contains("outline=False", StringComparison.Ordinal)
            && etf.Contains("changeColor=#FFEF585B", StringComparison.Ordinal),
            "513500 capsule", etf);

        window.SelectCode("510300");
        await window.RefreshSelectedAsync(true);
        await Settle();
        Save(window, Path.Combine(directory, "stock-compact-510300.png"));
        var lowPremium = window.DescribeCompactForSmoke();
        report.Add("compact-510300: " + lowPremium);
        Expect(lowPremium.Contains("price=4.595", StringComparison.Ordinal)
            && lowPremium.Contains("premium=-0.07%", StringComparison.Ordinal)
            && lowPremium.Contains("outline=True", StringComparison.Ordinal),
            "510300 capsule keeps the outlined premium under 2%", lowPremium);

        // An ordinary stock shows its rolling PE and falls, so the change colour is green.
        window.SelectCode("600519");
        await window.RefreshSelectedAsync(true);
        await Settle();
        Save(window, Path.Combine(directory, "stock-compact-600519.png"));
        var stock = window.DescribeCompactForSmoke();
        report.Add("compact-600519: " + stock);
        Expect(stock.Contains("price=1251.57", StringComparison.Ordinal)
            && stock.Contains("metric=滚动 PE", StringComparison.Ordinal)
            && stock.Contains("premium=17.57×", StringComparison.Ordinal)
            && stock.Contains("outline=False", StringComparison.Ordinal)
            && stock.Contains("changeColor=#FF7CEDAE", StringComparison.Ordinal),
            "600519 capsule shows the PE and the falling colour", stock);

        // ------------------------------------------------------ detail renders
        window.SelectCode("510300");
        await window.RefreshSelectedAsync(true);
        window.ToggleDetails();
        await Settle();
        var details = window.DetailsForSmoke ?? throw new InvalidOperationException("The detail window did not open.");
        await window.RefreshDetailsAsync();
        await Settle();
        Save(details, Path.Combine(directory, "stock-details-daily.png"));

        var dailyDescription = details.DescribeDetailsForSmoke();
        report.Add("details-510300-daily: " + dailyDescription);
        Expect(dailyDescription.Contains("symbol=510300", StringComparison.Ordinal)
            && dailyDescription.Contains("price=4.595", StringComparison.Ordinal)
            && dailyDescription.Contains("metric=参考溢价", StringComparison.Ordinal)
            && dailyDescription.Contains("instrument=IOPV", StringComparison.Ordinal)
            && dailyDescription.Contains("instrumentValue=4.5981", StringComparison.Ordinal)
            && dailyDescription.Contains("pe=13.42×", StringComparison.Ordinal)
            && dailyDescription.Contains("valuation=中证指数官方 · 2026-09-18", StringComparison.Ordinal)
            && dailyDescription.Contains("monitor=移出监控", StringComparison.Ordinal)
            && dailyDescription.Contains("alert=提醒 < 2%,alertEnabled=True", StringComparison.Ordinal)
            && dailyDescription.Contains("tabs=2", StringComparison.Ordinal)
            && dailyDescription.Contains("period=Daily", StringComparison.Ordinal)
            && dailyDescription.Contains("range=1", StringComparison.Ordinal),
            "510300 detail window content", dailyDescription);

        await RenderChart(window, details, report, directory, "stock-details-daily.png", StockPeriods.Daily, 1,
            expectedCandles: 242, lineMode: false, candlesDrawn: true);
        await RenderChart(window, details, report, directory, "stock-details-daily-3y.png", StockPeriods.Daily, 3,
            expectedCandles: 726, lineMode: true, candlesDrawn: false);
        await RenderChart(window, details, report, directory, "stock-details-minute.png", StockPeriods.Minute, 1,
            expectedCandles: 121, lineMode: true, candlesDrawn: false);
        await RenderChart(window, details, report, directory, "stock-details-weekly.png", StockPeriods.Weekly, 1,
            expectedCandles: -1, lineMode: false, candlesDrawn: null);
        await RenderChart(window, details, report, directory, "stock-details-monthly.png", StockPeriods.Monthly, 5,
            expectedCandles: -1, lineMode: false, candlesDrawn: null);

        // The S&P tracker shows the monthly multpl valuation. Its own kline series
        // was never recorded (the kline host refused it after the daily 510300
        // capture), so the missing fixture doubles as the error path check and the
        // valuation card is then rendered from the recorded series.
        window.SelectCode("513500");
        await window.RefreshSelectedAsync(false);
        await Settle();
        Save(details, Path.Combine(directory, "stock-details-513500.png"));
        var sp500 = details.DescribeDetailsForSmoke();
        report.Add("details-513500: " + sp500);
        Expect(sp500.Contains("changeColor=#FFEF585B", StringComparison.Ordinal)
            && sp500.Contains("alert=提醒 < 2%  ✓", StringComparison.Ordinal)
            && sp500.Contains("premium=10.82%", StringComparison.Ordinal),
            "513500 detail shows the quote and re-armed alert", sp500);

        var has513500Kline = StockFixtures.Exists("eastmoney-kline-weekly-513500.json")
            || StockFixtures.Exists("eastmoney-kline-daily-513500.json");
        await window.RefreshDetailsAsync();
        await Settle();
        if (has513500Kline)
        {
            var complete = details.DescribeDetailsForSmoke();
            report.Add("details-513500-complete: " + complete);
            Expect(complete.Contains("pe=26.07×", StringComparison.Ordinal)
                && complete.Contains("valuation=Multpl · 月度PE · 2026-09-18", StringComparison.Ordinal),
                "513500 detail shows the S&P valuation", complete);
        }
        else
        {
            // No recorded 513500 series: the detail refresh must fail visibly
            // instead of showing stale or invented data.
            var failed = details.DescribeDetailsForSmoke();
            report.Add("details-513500-kline-missing: " + failed);
            Expect(failed.Contains("valuation=估值数据暂不可用", StringComparison.Ordinal)
                && failed.Contains("percentile=样本不足", StringComparison.Ordinal)
                && failed.Contains("部分数据暂不可用", StringComparison.Ordinal),
                "a failed detail refresh clears the chart and reports it", failed);
            Save(details, Path.Combine(directory, "stock-details-513500-error.png"));

            details.SetChartData(await service.GetCandlesAsync("510300", StockPeriods.Daily, 1));
            details.RenderValuation(await service.GetValuationAsync("513500", true, null, 1));
            await Settle();
            Save(details, Path.Combine(directory, "stock-details-513500-valuation.png"));
            var rendered = details.DescribeDetailsForSmoke();
            report.Add("details-513500-valuation: " + rendered);
            Expect(rendered.Contains("pe=26.07×", StringComparison.Ordinal)
                && rendered.Contains("percentile=1年 · 46%", StringComparison.Ordinal)
                && rendered.Contains("valuation=Multpl · 月度PE · 2026-09-18", StringComparison.Ordinal),
                "513500 detail shows the S&P valuation", rendered);
        }

        // 600519 has no recorded kline series either, so the same two step check
        // applies: the failed refresh, then the rendered valuation card.
        window.SelectCode("600519");
        await window.RefreshSelectedAsync(false);
        await Settle();
        await window.RefreshDetailsAsync();
        await Settle();
        var failedOrdinary = details.DescribeDetailsForSmoke();
        report.Add("details-600519-kline-missing: " + failedOrdinary);
        Expect(failedOrdinary.Contains("valuation=估值数据暂不可用", StringComparison.Ordinal),
            "a failed 600519 refresh clears the valuation", failedOrdinary);

        details.SetChartData(await service.GetCandlesAsync("510300", StockPeriods.Daily, 1));
        details.RenderValuation(await service.GetValuationAsync("600519", false, 17.57, 1, "贵州茅台"));
        await Settle();
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
        await Settle();
        Save(details, Path.Combine(directory, "stock-details-empty.png"));
        var empty = details.ChartForSmoke.LastRender ?? throw new InvalidOperationException("The empty chart did not paint.");
        report.Add("chart-empty: " + empty);
        Expect(empty.CandleCount == 0, "empty chart reports no bars", empty.ToString());
        var emptyColors = CountChartColors(details, ChartRect(details));
        Expect(emptyColors.Up == 0 && emptyColors.Down == 0 && emptyColors.Line == 0,
            "empty chart draws no bars", emptyColors.ToString());

        var text = string.Join('\n', report);
        Console.WriteLine(text);
        File.WriteAllText(Path.Combine(directory, "stock-chart.txt"), text + Environment.NewLine);

        window.CloseDetails();
        await Settle();
        window.Close();
        await Settle();

        if (live) await RunLiveAsync(directory);
    }

    /// <summary>
    /// The opt-in live pass. It never falls back to the fixtures: when the market
    /// data hosts are unreachable it writes the failure next to the renders and
    /// fails the smoke, so a blocked network cannot look like a success.
    /// </summary>
    private static async Task RunLiveAsync(string directory)
    {
        using var service = new StockDataService();
        var store = new StockStore(Path.Combine(directory, "live-data"));
        var settings = store.Load();
        try
        {
            var quote = await service.GetQuoteAsync("513500");
            var candles = await service.GetCandlesAsync("510300", StockPeriods.Daily, 1);
            var minute = await service.GetMinuteAsync("510300");
            var valuation = await service.GetValuationAsync("510300", true, null, 1);
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
            await Settle();
            window.SelectCode("510300");
            await window.RefreshAllMonitoredAsync(true);
            await Settle();
            Save(window, Path.Combine(directory, "stock-live-compact.png"));
            window.ToggleDetails();
            await Settle();
            await window.RefreshDetailsAsync();
            await Settle();
            var details = window.DetailsForSmoke;
            if (details is not null) Save(details, Path.Combine(directory, "stock-live-details.png"));
            window.CloseDetails();
            window.Close();
            await Settle();
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(directory, "stock-live.error.txt"), exception.ToString());
            throw new InvalidOperationException("The live market data pass failed: " + exception.Message, exception);
        }
    }

    private static async Task RenderChart(StockWindow window, StockDetailsWindow details, List<string> report,
        string directory, string file, string period, int years, int expectedCandles, bool lineMode, bool? candlesDrawn)
    {
        window.SetChoice(true, period);
        window.SetChoice(false, years.ToString(CultureInfo.InvariantCulture));
        details.UpdateChoiceButtons();
        await window.RefreshDetailsAsync();
        await Settle();
        Save(details, Path.Combine(directory, file));

        var info = details.ChartForSmoke.LastRender
            ?? throw new InvalidOperationException("The chart did not paint for " + file + ".");
        report.Add("chart-" + file + ": " + info + " | " + details.DescribeDetailsForSmoke());
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

    // ---------------------------------------------------------------- helpers

    private static async Task Settle()
    {
        await Task.Delay(300);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(150);
    }

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
}
