using LittleTools.Assistant.Stock;
using System.Globalization;

/// <summary>
/// Covers the ported stock rules that hold without a network or a display: the
/// Windows compatible settings file, the load time repairs, the candle/valuation
/// math and the premium alert state machine.
/// </summary>
internal static class StockCoreTests
{
    public static void Run(Action<string, bool> check)
    {
        CheckCodes(check);
        CheckKlineLimits(check);
        CheckAxisPlanner(check);
        CheckJson(check);
        CheckStore(check);
        CheckLegacyCandidates(check);
        CheckValuation(check);
        CheckFormatting(check);
        CheckRefreshToken(check);
        CheckAlerts(check);
    }

    private static void CheckCodes(Action<string, bool> check)
    {
        check("six digit code is valid", StockMath.IsValidCode("510300") && StockMath.IsValidCode(" 513500 "));
        check("short or lettered code is invalid",
            !StockMath.IsValidCode("51030") && !StockMath.IsValidCode("abcdef") && !StockMath.IsValidCode("") && !StockMath.IsValidCode(null));
        check("shanghai symbol prefix", StockMath.Symbol("600519") == "sh600519" && StockMath.Symbol("510300") == "sh510300"
            && StockMath.Symbol("900901") == "sh900901");
        check("shenzhen symbol prefix", StockMath.Symbol("000001") == "sz000001" && StockMath.Symbol("159915") == "sz159915");
        check("secid market prefix", StockMath.SecId("600519") == "1.600519" && StockMath.SecId("159915") == "0.159915");
    }

    private static void CheckKlineLimits(Action<string, bool> check)
    {
        // StockData.cs L204-L209.
        check("daily limit counts years", StockMath.KlineLimit(StockPeriods.Daily, 1) == 265);
        check("weekly limit counts years", StockMath.KlineLimit(StockPeriods.Weekly, 3) == 162);
        check("monthly limit counts years", StockMath.KlineLimit(StockPeriods.Monthly, 5) == 62);
        check("one month span for daily", StockMath.KlineLimit(StockPeriods.Daily, 0) == 35);
        check("one month span for weekly", StockMath.KlineLimit(StockPeriods.Weekly, 0) == 24);
        check("limit is clamped to 1500", StockMath.KlineLimit(StockPeriods.Weekly, 100) == 1500);
        check("klt codes", StockMath.Klt(StockPeriods.Daily) == 101
            && StockMath.Klt(StockPeriods.Weekly) == 102 && StockMath.Klt(StockPeriods.Monthly) == 103);
        // StockWindow.cs L131-L132: the axis label is the formatted date or time,
        // not the format string the first port printed.
        check("intraday axis label is a real time",
            StockChartMath.AxisLabel(new DateTime(2026, 9, 21, 9, 30, 0), StockPeriods.Minute, true) == "09:30");
        check("short daily axis label keeps MM-dd",
            StockChartMath.AxisLabel(new DateTime(2026, 9, 21), StockPeriods.Daily, false) == "09-21");
        // Deliberate deviation from Windows (StockWindow.cs L128-L135 always printed
        // MM-dd): a one-year daily axis crosses a year, so the year has to be there.
        check("cross-year daily axis label carries the year",
            StockChartMath.AxisLabel(new DateTime(2026, 3, 31), StockPeriods.Daily, true) == "26-03-31"
            && StockChartMath.AxisLabel(new DateTime(2025, 9, 30), StockPeriods.Daily, true) == "25-09-30");
        check("weekly axis label carries the year and month",
            StockChartMath.AxisLabel(new DateTime(2026, 3, 31), StockPeriods.Weekly, false) == "2026-03");
        check("monthly axis label carries the year and month",
            StockChartMath.AxisLabel(new DateTime(2026, 3, 31), StockPeriods.Monthly, true) == "2026-03");
        check("price axis label keeps the windows precision",
            StockChartMath.PriceLabel(4.5949) == "4.595" && StockChartMath.PriceLabel(1251.567) == "1251.57");
    }

    private static void CheckAxisPlanner(Action<string, bool> check)
    {
        // A daily year that really crosses one: 2025-09-30 .. 2026-09-21 (357 bars).
        var year = DailySeries(new DateTime(2025, 9, 30), 357);
        check("a daily year is detected as crossing a year", StockChartMath.CrossesYear(year));
        var labels = StockChartMath.AxisLabels(year, StockPeriods.Daily);
        check("the cross-year axis has three ticks", labels.Length == 3);
        check("the cross-year axis labels carry the year",
            labels[0].Text == "25-09-30" && labels[^1].Text == "26-09-21"
            && labels.All(axis => axis.Text.Length == 8 && axis.Text[2] == '-' && axis.Text[5] == '-'));
        // The middle tick sits on a first trading day of a month, not on bar N/2.
        var middle = labels[1].Index;
        check("the middle tick snaps to a month boundary",
            middle is > 0 and < 356
            && (year[middle].Time.Month != year[middle - 1].Time.Month || year[middle].Time.Year != year[middle - 1].Time.Year)
            && labels[1].Text.StartsWith("26-", StringComparison.Ordinal));

        // The same series cut to one month does not cross a year and keeps MM-dd.
        var month = year.Where(candle => candle.Time >= new DateTime(2026, 8, 21)).ToList();
        check("a daily month does not cross a year", !StockChartMath.CrossesYear(month));
        var monthLabels = StockChartMath.AxisLabels(month, StockPeriods.Daily);
        check("a daily month keeps MM-dd",
            monthLabels.Length == 3 && monthLabels.All(axis => axis.Text.Length == 5 && axis.Text[2] == '-'));

        // Weekly and monthly always carry yyyy-MM, intraday keeps HH:mm, and the
        // format string itself is never what gets drawn.
        var weekly = DailySeries(new DateTime(2025, 10, 6), 60, 7);
        check("weekly ticks are yyyy-MM",
            StockChartMath.AxisLabels(weekly, StockPeriods.Weekly).All(axis => axis.Text.Length == 7));
        var monthly = DailySeries(new DateTime(2021, 9, 1), 60, 30);
        check("monthly ticks are yyyy-MM",
            StockChartMath.AxisLabels(monthly, StockPeriods.Monthly).All(axis => axis.Text.Length == 7));
        var minute = DailySeries(new DateTime(2026, 9, 21, 9, 30, 0), 121, 0, TimeSpan.FromMinutes(1));
        check("intraday ticks are HH:mm",
            StockChartMath.AxisLabels(minute, StockPeriods.Minute).All(axis => axis.Text.Length == 5 && axis.Text[2] == ':'));
        check("no tick is the format string itself",
            StockChartMath.AxisLabels(year, StockPeriods.Daily).All(axis => axis.Text != "MM-dd" && axis.Text != "yy-MM-dd")
            && StockChartMath.AxisLabels(monthly, StockPeriods.Monthly).All(axis => axis.Text != "yyyy-MM"));
        check("the tick indices stay inside the series",
            StockChartMath.AxisLabelIndices(year, StockPeriods.Daily).All(index => index >= 0 && index < year.Count));
        check("a single bar still gets one tick",
            StockChartMath.AxisLabelIndices(DailySeries(new DateTime(2026, 9, 21), 1), StockPeriods.Daily).Length == 1);
    }

    /// <summary>
    /// A synthetic series with trading-day spacing; <paramref name="stepDays"/> of 0
    /// together with <paramref name="step"/> builds an intraday one.
    /// </summary>
    private static List<Candle> DailySeries(DateTime start, int count, int stepDays = 1, TimeSpan? step = null)
    {
        var candles = new List<Candle>(count);
        for (var index = 0; index < count; index++)
        {
            var time = step is { } intraday
                ? start.AddMinutes(index * intraday.TotalMinutes)
                : start.AddDays(index * stepDays);
            candles.Add(new Candle { Time = time, Open = 10, Close = 10, High = 10, Low = 10 });
        }
        return candles;
    }

    private static void CheckJson(Action<string, bool> check)
    {
        // Exactly the shape JavaScriptSerializer writes, including its invalid
        // bare NaN literal for an unpositioned window.
        const string windowsFile =
            "{\"Watched\":[{\"Code\":\"510300\",\"AlertEnabled\":false,\"PremiumThreshold\":2}," +
            "{\"Code\":\"513500\",\"AlertEnabled\":true,\"PremiumThreshold\":2}]," +
            "\"SelectedCode\":\"513500\",\"Topmost\":false,\"Compact\":false,\"KlinePeriod\":\"Daily\"," +
            "\"RangeYears\":1,\"Left\":NaN,\"Top\":NaN}";
        var parsed = StockJson.Deserialize(windowsFile);
        check("windows settings parse", parsed is not null && parsed.Watched.Count == 2);
        check("windows NaN literal becomes NaN", parsed is not null && double.IsNaN(parsed.Left) && double.IsNaN(parsed.Top));
        check("windows strings stay intact", parsed is not null && parsed.SelectedCode == "513500" && parsed.Watched[0].Code == "510300");
        check("windows bools parse", parsed is not null && parsed.Watched[1].AlertEnabled && !parsed.Watched[0].AlertEnabled);

        var quoted = StockJson.Deserialize("{\"Left\":\"NaN\",\"Top\":null,\"SelectedCode\":\"600519\"}");
        check("quoted NaN and null both read as NaN",
            quoted is not null && double.IsNaN(quoted.Left) && double.IsNaN(quoted.Top) && quoted.SelectedCode == "600519");

        check("infinity literals are accepted",
            StockMath.TryNumber(StockJson.MaskNonFiniteLiterals("{\"Left\":-Infinity}"), out _) == false
            && StockJson.MaskNonFiniteLiterals("{\"Left\":-Infinity}") == "{\"Left\":null}"
            && StockJson.MaskNonFiniteLiterals("{\"Left\":Infinity}") == "{\"Left\":null}");
        check("NaN inside a string is untouched",
            StockJson.MaskNonFiniteLiterals("{\"SelectedCode\":\"NaN\"}") == "{\"SelectedCode\":\"NaN\"}");
        check("escaped quote does not end a string",
            StockJson.MaskNonFiniteLiterals("{\"a\":\"x\\\"NaN\"}") == "{\"a\":\"x\\\"NaN\"}");

        var settings = new StockSettings { SelectedCode = "600519", Left = 12.5, Top = 34.25 };
        var roundTrip = StockJson.Deserialize(StockJson.Serialize(settings));
        check("settings round trip",
            roundTrip is not null && roundTrip.SelectedCode == "600519" && roundTrip.Left == 12.5 && roundTrip.Top == 34.25);
        check("settings keep PascalCase names",
            StockJson.Serialize(settings).Contains("\"SelectedCode\"", StringComparison.Ordinal)
            && StockJson.Serialize(settings).Contains("\"RangeYears\"", StringComparison.Ordinal));
        check("cjk is not escaped", StockJson.Serialize(new StockSettings { SelectedCode = "513500" })
            .Contains("513500", StringComparison.Ordinal));
        var unpositioned = StockJson.Serialize(new StockSettings { Left = double.NaN, Top = double.NaN });
        check("unpositioned window writes the bare NaN literal",
            unpositioned.Contains(": NaN", StringComparison.Ordinal) && !unpositioned.Contains("\"NaN\"", StringComparison.Ordinal));
        check("bare NaN literal round trips",
            StockJson.Deserialize(unpositioned) is { } unpositionedRoundTrip
            && double.IsNaN(unpositionedRoundTrip.Left) && double.IsNaN(unpositionedRoundTrip.Top));

        var withUnknownField = StockJson.Deserialize("{\"SelectedCode\":\"600519\",\"ServerSideField\":7}");
        check("unknown fields are ignored", withUnknownField is not null && withUnknownField.SelectedCode == "600519");
        check("empty file yields nothing", StockJson.Deserialize("") is null);
    }

    private static void CheckStore(Action<string, bool> check)
    {
        var root = TempDirectory();
        try
        {
            var store = new StockStore(root);
            var defaults = store.Load();
            check("default watch list", defaults.Watched.Count == 2
                && defaults.Watched[0].Code == "510300" && !defaults.Watched[0].AlertEnabled
                && defaults.Watched[1].Code == "513500" && defaults.Watched[1].AlertEnabled);
            check("default selection", defaults.SelectedCode == "513500");
            check("default period and span", defaults.KlinePeriod == StockPeriods.Daily && defaults.RangeYears == 1);
            check("default position is unknown", double.IsNaN(defaults.Left) && double.IsNaN(defaults.Top));
            check("no data file before the first save", !File.Exists(store.DataPath));

            // StockData.cs L90-L107 repairs a hand edited file.
            var repaired = new StockSettings
            {
                Watched =
                [
                    new StockWatchEntry { Code = " 600519 ", PremiumThreshold = 250 },
                    new StockWatchEntry { Code = "abc" },
                    new StockWatchEntry { Code = "000001", PremiumThreshold = -100 }
                ],
                SelectedCode = "nope",
                RangeYears = 2
            };
            StockStore.Normalize(repaired);
            check("invalid codes dropped", repaired.Watched.Count == 2 && repaired.Watched[0].Code == "600519");
            check("code is trimmed", repaired.Watched[0].Code == "600519");
            check("impossible threshold repaired", repaired.Watched[0].PremiumThreshold == 2.0);
            check("boundary threshold repaired", repaired.Watched[1].PremiumThreshold == 2.0);
            check("invalid selection falls back to the first watch",
                repaired.SelectedCode == "600519");
            check("unsupported span falls back to one year", repaired.RangeYears == 1);

            repaired.Left = 120;
            repaired.Top = 60;
            check("save succeeds", store.Save(repaired));
            check("no temp file left behind", !File.Exists(store.DataPath + ".tmp"));
            var reloaded = new StockStore(root).Load();
            check("saved position is restored", reloaded.Left == 120 && reloaded.Top == 60);
            check("saved watch list is restored", reloaded.Watched.Count == 2 && reloaded.SelectedCode == "600519");

            var broken = new StockStore(Path.Combine(root, "broken"));
            Directory.CreateDirectory(broken.DataPath[..broken.DataPath.LastIndexOf('/')]);
            File.WriteAllText(broken.DataPath, "{ this is not json");
            var recovered = broken.Load();
            check("broken file falls back to the defaults", recovered.Watched.Count == 2);
            check("broken file reports a warning", broken.LoadWarning is not null);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void CheckLegacyCandidates(Action<string, bool> check)
    {
        var explicitPath = Path.Combine(Path.GetTempPath(), "stock-legacy-settings.json");
        var previous = Environment.GetEnvironmentVariable("LITTLETOOLS_STOCK_DATA");
        try
        {
            Environment.SetEnvironmentVariable("LITTLETOOLS_STOCK_DATA", explicitPath);
            var candidates = StockStore.LegacyCandidates("/tmp/target/stock").ToList();
            check("explicit stock data path comes first", candidates[0] == explicitPath);
            check("windows folder name is searched",
                candidates.Any(path => path.EndsWith(Path.Combine("LittleTools", "StockMonitor", "settings.json"), StringComparison.Ordinal)));

            // The import copies, never moves, and only when the target is absent.
            var root = TempDirectory();
            try
            {
                var source = Path.Combine(root, "windows-settings.json");
                File.WriteAllText(source, "{\"SelectedCode\":\"600519\",\"Watched\":[{\"Code\":\"600519\"}],\"RangeYears\":3}");
                Environment.SetEnvironmentVariable("LITTLETOOLS_STOCK_DATA", source);
                var store = new StockStore(Path.Combine(root, "linux"));
                var imported = store.Load();
                check("windows settings are adopted", imported.SelectedCode == "600519" && imported.RangeYears == 3);
                check("import source is reported", store.ImportedFrom == source);
                check("import copies instead of moving", File.Exists(source));
                check("second load does not re-import", new StockStore(Path.Combine(root, "linux")).ImportedFrom is null);
            }
            finally
            {
                Cleanup(root);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("LITTLETOOLS_STOCK_DATA", previous);
        }
    }

    private static void CheckValuation(Action<string, bool> check)
    {
        var points = new List<(DateTime Date, double Value)>
        {
            (new DateTime(2024, 1, 2), 10),
            (new DateTime(2025, 1, 2), 20),
            (new DateTime(2026, 1, 2), 15)
        };
        var valuation = StockMath.BuildValuation(points, 1, "测试源");
        check("valuation latest value", valuation.CurrentPe == 15);
        check("valuation date", valuation.DataDate == new DateTime(2026, 1, 2));
        check("valuation samples inside the span", valuation.SampleCount == 2);
        check("valuation percentile", Math.Abs(valuation.Percentile!.Value - 50) < 0.0001);
        check("valuation source is kept", valuation.Source == "测试源");

        var unsorted = new List<(DateTime Date, double Value)>
        {
            (new DateTime(2026, 1, 2), 15),
            (new DateTime(2024, 1, 2), 10)
        };
        var sorted = StockMath.BuildValuation(unsorted, 3, "s");
        check("valuation sorts by date", sorted.DataDate == new DateTime(2026, 1, 2) && sorted.CurrentPe == 15);

        var empty = StockMath.BuildValuation([], 5, "空源");
        check("empty valuation keeps the label", empty.CurrentPe is null && empty.Percentile is null && empty.Years == 5 && empty.Source == "空源");

        var candles = new List<Candle>
        {
            new() { Time = new DateTime(2020, 1, 1) },
            new() { Time = new DateTime(2025, 6, 1) },
            new() { Time = new DateTime(2026, 6, 1) }
        };
        check("range filters from the newest candle",
            StockMath.FilterRange(candles, 1).Count == 2);
        check("zero years keeps the whole series", StockMath.FilterRange(candles, 0).Count == 3);
        check("empty series survives the filter", StockMath.FilterRange([], 3).Count == 0);
    }

    private static void CheckFormatting(Action<string, bool> check)
    {
        check("price uses three decimals under ten", StockFormat.Price(2.709) == "2.709");
        check("price uses two decimals from ten", StockFormat.Price(12.3456) == "12.35");
        check("iopv uses four decimals", StockFormat.Iopv(2.4444) == "2.4444");
        check("premium formats as percent", StockFormat.Premium(1.5) == "1.50%" && StockFormat.Premium(null) == "不适用");
        check("change keeps the sign", StockFormat.Change(0.89) == "+0.89%" && StockFormat.Change(-1.2) == "-1.20%");
        check("pe formats with a times sign", StockFormat.Pe(17.57) == "17.57×" && StockFormat.Pe(null) == "--");
        check("short name drops the fund manager prefix",
            StockFormat.ShortName("华泰柏瑞沪深300ETF") == "沪深300ETF" && StockFormat.ShortName("博时标普500ETF") == "标普500ETF");
        check("change colours follow the ascii share habit",
            StockFormat.ChangeOf(1.2) == StockFormat.ChangeKind.Up
            && StockFormat.ChangeOf(-1.2) == StockFormat.ChangeKind.Down
            && StockFormat.ChangeOf(0) == StockFormat.ChangeKind.Flat);
        check("premium outline only below two percent",
            StockFormat.PremiumOutlineEnabled(1.99) && !StockFormat.PremiumOutlineEnabled(2) && !StockFormat.PremiumOutlineEnabled(null));
        check("percentile text", StockFormat.Percentile(new ValuationInfo { Years = 3, Percentile = 42.4 }) == "3年 · 42%");
        check("missing percentile samples", StockFormat.Percentile(new ValuationInfo { Years = 1 }) == "样本不足");
        check("valuation source text",
            StockFormat.ValuationSource(new ValuationInfo { Source = "测试", DataDate = new DateTime(2026, 9, 18) }) == "测试 · 2026-09-18"
            && StockFormat.ValuationSource(null) == "估值数据暂不可用");
        check("instrument label switches for ordinary stocks",
            StockFormat.InstrumentLabel(new StockQuote { IsEtf = true }) == "IOPV"
            && StockFormat.InstrumentLabel(new StockQuote { IsEtf = false }) == "证券类型");
        check("instrument value switches for ordinary stocks",
            StockFormat.InstrumentValue(new StockQuote { IsEtf = false }) == "普通股票"
            && StockFormat.InstrumentValue(new StockQuote { IsEtf = true, Iopv = 2.4444 }) == "2.4444"
            && StockFormat.InstrumentValue(new StockQuote { IsEtf = true }) == "--");
        check("security metric label switches for ordinary stocks",
            StockFormat.SecurityMetricLabel(new StockQuote { IsEtf = false }) == "滚动 PE"
            && StockFormat.SecurityMetricLabel(new StockQuote { IsEtf = true }) == "参考溢价");
        check("tab caption carries the short name",
            StockFormat.TabCaption("513500", new StockQuote { Name = "博时标普500ETF" }) == "513500  标普500ETF");
        check("tab width is clamped", StockFormat.TabWidth("510300") == 100 && StockFormat.TabWidth(new string('x', 40)) == 185);
    }

    /// <summary>
    /// The stale response guard. A refresh answer is applied only while its token
    /// still describes the live selection, which is what keeps a quote fetched for
    /// the previous code from being rendered under the new one.
    /// </summary>
    private static void CheckRefreshToken(Action<string, bool> check)
    {
        var token = new StockRefreshToken("510300", StockPeriods.Daily, 1, 7);
        check("refresh token matches the same selection", token.Matches("510300", StockPeriods.Daily, 1));
        check("refresh token rejects another code", !token.Matches("513500", StockPeriods.Daily, 1));
        check("refresh token rejects another period", !token.Matches("510300", StockPeriods.Monthly, 1));
        check("refresh token rejects another span", !token.Matches("510300", StockPeriods.Daily, 5));
        check("refresh token carries its version", token.Version == 7 && token.ToString() == "510300/Daily/1#7");
    }

    private static void CheckAlerts(Action<string, bool> check)
    {
        var tracker = new StockAlertTracker();
        var entry = new StockWatchEntry { Code = "513500", AlertEnabled = true, PremiumThreshold = 2.0 };
        var quote = new StockQuote { Code = "513500", Name = "标普500ETF", IsEtf = true, PremiumPercent = 1.6 };

        var first = tracker.Check(entry, quote);
        check("alert fires below the threshold", first is not null && first.Title == "Little Tools · 溢价提醒");
        check("alert text carries code, value and threshold",
            first is not null && first.Message.Contains("513500 标普500ETF", StringComparison.Ordinal)
            && first.Message.Contains("1.60%", StringComparison.Ordinal)
            && first.Message.Contains("（阈值 2%", StringComparison.Ordinal));

        quote.PremiumPercent = 1.2;
        check("alert does not repeat while still below", tracker.Check(entry, quote) is null);

        quote.PremiumPercent = 5.0;
        check("recovery above the threshold is silent", tracker.Check(entry, quote) is null);
        quote.PremiumPercent = 1.9;
        check("alert re-arms after recovery", tracker.Check(entry, quote) is not null);

        var disabled = new StockWatchEntry { Code = "510300", AlertEnabled = false };
        check("disabled alert stays silent",
            tracker.Check(disabled, new StockQuote { Code = "510300", IsEtf = true, PremiumPercent = 0.1 }) is null);
        check("ordinary stocks never alert",
            tracker.Check(entry, new StockQuote { Code = "600519", IsEtf = false, PremiumPercent = 0.1 }) is null);
        check("missing premium never alerts",
            tracker.Check(entry, new StockQuote { Code = "513500", IsEtf = true }) is null);
    }

    private static string TempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "LittleTools-stock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Unsafe test cleanup path.");
        Directory.Delete(root, true);
    }
}
