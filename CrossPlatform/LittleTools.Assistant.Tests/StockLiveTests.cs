using LittleTools.Assistant.Stock;

/// <summary>
/// Real network checks for every stock data source. They are opt-in (run the
/// test assembly with <c>--stock-live</c>) because the sandbox and most build
/// machines cannot reach the market data hosts, and a blocked host must be
/// reported instead of silently passing.
/// </summary>
internal static class StockLiveTests
{
    public static async Task RunAsync(Action<string, bool> check)
    {
        using var service = new StockDataService();
        var now = DateTime.Now;

        await Probe(check, "live quote 513500", async () =>
        {
            var quote = await service.GetQuoteAsync("513500");
            Console.WriteLine($"LIVE quote 513500: {quote.Code} {quote.Name} {quote.Price} {quote.ChangePercent:0.00}% "
                + $"premium={quote.PremiumPercent:0.00} iopv={quote.Iopv} at {quote.UpdatedAt:yyyy-MM-dd HH:mm:ss}");
            return quote.Price > 0 && quote.IsEtf && quote.Name.Length > 0;
        });

        await Probe(check, "live quote and pe 600519", async () =>
        {
            var quote = await service.GetQuoteAsync("600519");
            Console.WriteLine($"LIVE quote 600519: {quote.Name} {quote.Price} pe={quote.Pe}");
            return quote.Price > 0 && quote.Pe is > 0;
        });

        foreach (var (code, period, years) in new[]
                 {
                     ("510300", StockPeriods.Minute, 1),
                     ("510300", StockPeriods.Daily, 1),
                     ("510300", StockPeriods.Weekly, 1),
                     ("510300", StockPeriods.Monthly, 5)
                 })
        {
            await Probe(check, $"live candles {code} {period}", async () =>
            {
                var candles = await service.GetCandlesAsync(code, period, years);
                if (candles.Count > 0)
                    Console.WriteLine($"LIVE candles {code}/{period}: {candles.Count} bars {candles[0].Time:yyyy-MM-dd HH:mm}"
                        + $" .. {candles[^1].Time:yyyy-MM-dd HH:mm} last close {candles[^1].Close}");
                return candles.Count > 20 && candles.All(item => item.High >= item.Low);
            });
        }

        await Probe(check, "live valuation 510300 (csindex)", async () =>
        {
            var value = await service.GetValuationAsync("510300", true, null, 1);
            Console.WriteLine($"LIVE valuation 510300: pe={value.CurrentPe} date={value.DataDate:yyyy-MM-dd} "
                + $"samples={value.SampleCount} percentile={value.Percentile:0.0} source={value.Source}");
            return value.CurrentPe is > 0 && value.SampleCount > 0;
        });

        await Probe(check, "live valuation 513500 (multpl)", async () =>
        {
            var value = await service.GetValuationAsync("513500", true, null, 1);
            Console.WriteLine($"LIVE valuation 513500: pe={value.CurrentPe} date={value.DataDate:yyyy-MM-dd} "
                + $"samples={value.SampleCount} percentile={value.Percentile:0.0} source={value.Source}");
            return value.CurrentPe is > 0 && value.SampleCount > 0;
        });

        await Probe(check, "live valuation 600519 (eastmoney pe-ttm)", async () =>
        {
            var value = await service.GetValuationAsync("600519", false, null, 1);
            Console.WriteLine($"LIVE valuation 600519: pe={value.CurrentPe} date={value.DataDate:yyyy-MM-dd} "
                + $"samples={value.SampleCount} percentile={value.Percentile:0.0} source={value.Source}");
            return value.CurrentPe is > 0 && value.SampleCount > 100;
        });

        Console.WriteLine("live checks finished at " + now.ToString("HH:mm:ss"));
    }

    private static async Task Probe(Action<string, bool> check, string name, Func<Task<bool>> action)
    {
        try
        {
            check(name, await action());
        }
        catch (Exception exception)
        {
            check(name + " (" + exception.GetType().Name + ": " + exception.Message + ")", false);
        }
    }
}
