using System;
using System.Globalization;
using System.Threading.Tasks;
using LittleTools.StockMonitor;

internal static class StockDataSmoke
{
    private static int Main()
    {
        return Run().GetAwaiter().GetResult();
    }

    private static async Task<int> Run()
    {
        using (var service = new StockDataService())
        {
            foreach (string code in new[] { "510300", "513500" })
            {
                StockQuote quote = await service.GetQuoteAsync(code);
                if (!quote.IsEtf || quote.Price <= 0 || !quote.Iopv.HasValue || !quote.PremiumPercent.HasValue)
                    throw new InvalidOperationException(code + " ETF quote fields are incomplete.");
                Console.WriteLine("QUOTE={0},{1},{2},{3}", quote.Code,
                    quote.Price.ToString("0.000", CultureInfo.InvariantCulture),
                    quote.Iopv.Value.ToString("0.0000", CultureInfo.InvariantCulture),
                    quote.PremiumPercent.Value.ToString("0.00", CultureInfo.InvariantCulture));
            }

            StockQuote lof = await service.GetQuoteAsync("161125");
            if (!lof.IsEtf || lof.Price <= 0 || !lof.PremiumPercent.HasValue)
                throw new InvalidOperationException("161125 LOF quote or premium is incomplete.");
            Console.WriteLine("LOF={0},{1},PREMIUM={2}", lof.Code, lof.Name,
                lof.PremiumPercent.Value.ToString("0.00", CultureInfo.InvariantCulture));

            StockQuote stock = await service.GetQuoteAsync("600519");
            if (stock.IsEtf || stock.Price <= 0 || !stock.Pe.HasValue)
                throw new InvalidOperationException("Ordinary A-share quote or PE is incomplete.");
            Console.WriteLine("STOCK={0},{1},PE={2}", stock.Code, stock.Name, stock.Pe.Value.ToString("0.00", CultureInfo.InvariantCulture));

            var candles = await service.GetCandlesAsync("513500", "Daily", 1);
            if (candles.Count < 100) throw new InvalidOperationException("K-line sample is unexpectedly small.");
            Console.WriteLine("KLINES=" + candles.Count);

            ValuationInfo csi = await service.GetValuationAsync("510300", true, null, 5);
            ValuationInfo sp = await service.GetValuationAsync("513500", true, null, 5);
            ValuationInfo lofValuation = await service.GetValuationAsync("161125", true, null, 5, lof.Name);
            ValuationInfo stockValuation = await service.GetValuationAsync("600519", false, stock.Pe, 1);
            if (!csi.CurrentPe.HasValue || !csi.Percentile.HasValue || csi.SampleCount < 500)
                throw new InvalidOperationException("CSI 300 valuation is incomplete.");
            if (!sp.CurrentPe.HasValue || !sp.Percentile.HasValue || sp.SampleCount < 50)
                throw new InvalidOperationException("S&P 500 valuation is incomplete.");
            if (!lofValuation.CurrentPe.HasValue || !lofValuation.Percentile.HasValue || lofValuation.SampleCount < 50)
                throw new InvalidOperationException("161125 tracked-index valuation is incomplete.");
            if (!stockValuation.CurrentPe.HasValue || !stockValuation.Percentile.HasValue || stockValuation.SampleCount < 200)
                throw new InvalidOperationException("Ordinary A-share historical PE valuation is incomplete.");
            Console.WriteLine("CSI_PE={0},PCTL={1:0.0},N={2}", csi.CurrentPe, csi.Percentile, csi.SampleCount);
            Console.WriteLine("SP_PE={0},PCTL={1:0.0},N={2}", sp.CurrentPe, sp.Percentile, sp.SampleCount);
            Console.WriteLine("STOCK_PE={0},PCTL={1:0.0},N={2}", stockValuation.CurrentPe, stockValuation.Percentile, stockValuation.SampleCount);
        }
        Console.WriteLine("STOCK_DATA_SMOKE_OK");
        return 0;
    }
}
