using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using LittleTools.Common;

namespace LittleTools.StockMonitor
{
    internal sealed class StockWatchEntry
    {
        public string Code;
        public bool AlertEnabled = true;
        public double PremiumThreshold = 2.0;
    }

    internal sealed class StockSettings
    {
        public List<StockWatchEntry> Watched = new List<StockWatchEntry>();
        public string SelectedCode = "513500";
        public bool Topmost = false;
        public bool Compact = false;
        public string KlinePeriod = "Daily";
        public int RangeYears = 1;
        public double Left = double.NaN;
        public double Top = double.NaN;
    }

    internal sealed class StockQuote
    {
        public string Code;
        public string Name;
        public double Price;
        public double PreviousClose;
        public double ChangePercent;
        public bool IsEtf;
        public double? Iopv;
        public double? PremiumPercent;
        public double? Pe;
        public DateTime UpdatedAt;
        public string DataSource;
    }

    internal sealed class Candle
    {
        public DateTime Time;
        public double Open;
        public double Close;
        public double High;
        public double Low;
    }

    internal sealed class ValuationInfo
    {
        public double? CurrentPe;
        public double? Percentile;
        public int Years;
        public DateTime? DataDate;
        public string Source;
        public int SampleCount;
    }

    internal sealed class StockStore
    {
        private readonly string path;

        public StockStore()
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LittleTools", "StockMonitor", "settings.json");
        }

        public StockSettings Load()
        {
            StockSettings settings = null;
            try
            {
                if (File.Exists(path))
                    settings = new JavaScriptSerializer().Deserialize<StockSettings>(File.ReadAllText(path, Encoding.UTF8));
            }
            catch { }
            if (settings == null) settings = new StockSettings();
            if (settings.Watched == null) settings.Watched = new List<StockWatchEntry>();
            settings.Watched.RemoveAll(delegate(StockWatchEntry item)
            {
                return item == null || !StockDataService.IsValidCode(item.Code);
            });
            foreach (StockWatchEntry item in settings.Watched)
            {
                item.Code = item.Code.Trim();
                if (item.PremiumThreshold <= -100 || item.PremiumThreshold > 100) item.PremiumThreshold = 2.0;
            }
            if (settings.Watched.Count == 0)
            {
                settings.Watched.Add(new StockWatchEntry { Code = "510300", AlertEnabled = false });
                settings.Watched.Add(new StockWatchEntry { Code = "513500", AlertEnabled = true, PremiumThreshold = 2.0 });
            }
            if (!StockDataService.IsValidCode(settings.SelectedCode)) settings.SelectedCode = settings.Watched[0].Code;
            if (settings.RangeYears != 0 && settings.RangeYears != 1 && settings.RangeYears != 3 && settings.RangeYears != 5)
                settings.RangeYears = 1;
            return settings;
        }

        public void Save(StockSettings settings)
        {
            AtomicFile.WriteUtf8(path, new JavaScriptSerializer().Serialize(settings));
        }
    }

    internal sealed class StockDataService : IDisposable
    {
        private readonly HttpClient client;
        private readonly Dictionary<string, Tuple<DateTime, ValuationInfo>> valuationCache =
            new Dictionary<string, Tuple<DateTime, ValuationInfo>>(StringComparer.Ordinal);

        public StockDataService()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LittleTools/1.0");
        }

        public static bool IsValidCode(string code)
        {
            return !string.IsNullOrWhiteSpace(code) && Regex.IsMatch(code.Trim(), "^[0-9]{6}$");
        }

        private static string Symbol(string code)
        {
            return (code.StartsWith("5") || code.StartsWith("6") || code.StartsWith("9") ? "sh" : "sz") + code;
        }

        private static string SecId(string code)
        {
            return (code.StartsWith("5") || code.StartsWith("6") || code.StartsWith("9") ? "1." : "0.") + code;
        }

        public async Task<StockQuote> GetQuoteAsync(string code)
        {
            code = (code ?? "").Trim();
            if (!IsValidCode(code)) throw new InvalidOperationException("请输入六位沪深证券代码");
            string url = "https://qt.gtimg.cn/q=" + Symbol(code);
            byte[] bytes = await client.GetByteArrayAsync(url).ConfigureAwait(false);
            string text = Encoding.GetEncoding("GB18030").GetString(bytes);
            int first = text.IndexOf('"');
            int last = text.LastIndexOf('"');
            if (first < 0 || last <= first) throw new InvalidOperationException("没有查询到该证券代码");
            string[] fields = text.Substring(first + 1, last - first - 1).Split('~');
            if (fields.Length < 35 || fields[2] != code) throw new InvalidOperationException("行情数据格式异常");

            var quote = new StockQuote
            {
                Code = code,
                Name = fields[1],
                Price = Number(fields[3]),
                PreviousClose = Number(fields[4]),
                ChangePercent = Number(fields[32]),
                IsEtf = fields.Length > 61 && IsExchangeTradedFundType(fields[61]),
                UpdatedAt = ParseQuoteTime(fields.Length > 30 ? fields[30] : null),
                DataSource = "腾讯行情"
            };
            if (quote.Price <= 0) throw new InvalidOperationException("该证券当前没有有效行情");
            if (quote.IsEtf && fields.Length > 78)
            {
                double premium;
                double iopv;
                if (TryNumber(fields[77], out premium)) quote.PremiumPercent = premium;
                if (TryNumber(fields[78], out iopv) && iopv > 0) quote.Iopv = iopv;
                if (!quote.PremiumPercent.HasValue && quote.Iopv.HasValue)
                    quote.PremiumPercent = (quote.Price / quote.Iopv.Value - 1.0) * 100.0;
            }
            if (!quote.IsEtf)
            {
                try { quote.Pe = await GetStockPeAsync(code).ConfigureAwait(false); } catch { }
            }
            return quote;
        }

        private static bool IsExchangeTradedFundType(string value)
        {
            return string.Equals(value, "ETF", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "LOF", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<double?> GetStockPeAsync(string code)
        {
            string url = "https://push2.eastmoney.com/api/qt/stock/get?fltt=2&secid=" + SecId(code) + "&fields=f162";
            Dictionary<string, object> root = Json(await client.GetStringAsync(url).ConfigureAwait(false));
            Dictionary<string, object> data = Child(root, "data");
            double value;
            return data != null && TryObjectNumber(Value(data, "f162"), out value) && value > 0 ? (double?)value : null;
        }

        public async Task<List<Candle>> GetCandlesAsync(string code, string period, int rangeYears)
        {
            if (period == "Minute") return await GetMinuteAsync(code).ConfigureAwait(false);
            int klt = period == "Weekly" ? 102 : period == "Monthly" ? 103 : 101;
            int limit;
            if (rangeYears == 0) limit = period == "Daily" ? 35 : 24;
            else if (period == "Monthly") limit = rangeYears * 12 + 2;
            else if (period == "Weekly") limit = rangeYears * 53 + 3;
            else limit = rangeYears * 260 + 5;
            limit = Math.Max(20, Math.Min(1500, limit));
            string url = "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=" + SecId(code)
                + "&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56&klt=" + klt
                + "&fqt=1&end=20500101&lmt=" + limit.ToString(CultureInfo.InvariantCulture);
            Dictionary<string, object> root = Json(await client.GetStringAsync(url).ConfigureAwait(false));
            Dictionary<string, object> data = Child(root, "data");
            var result = new List<Candle>();
            foreach (object raw in ArrayValue(data, "klines"))
            {
                string[] parts = Convert.ToString(raw, CultureInfo.InvariantCulture).Split(',');
                DateTime date;
                if (parts.Length < 5 || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                result.Add(new Candle { Time = date, Open = Number(parts[1]), Close = Number(parts[2]), High = Number(parts[3]), Low = Number(parts[4]) });
            }
            return FilterRange(result, rangeYears);
        }

        private async Task<List<Candle>> GetMinuteAsync(string code)
        {
            string url = "https://push2.eastmoney.com/api/qt/stock/trends2/get?secid=" + SecId(code)
                + "&fields1=f1,f2,f3,f4,f5,f6,f7,f8&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&iscca=0&ndays=1";
            Dictionary<string, object> root = Json(await client.GetStringAsync(url).ConfigureAwait(false));
            Dictionary<string, object> data = Child(root, "data");
            var result = new List<Candle>();
            foreach (object raw in ArrayValue(data, "trends"))
            {
                string[] parts = Convert.ToString(raw, CultureInfo.InvariantCulture).Split(',');
                DateTime time;
                if (parts.Length < 5 || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out time)) continue;
                double price = Number(parts[1]);
                result.Add(new Candle { Time = time, Open = price, Close = price, High = Number(parts[3]), Low = Number(parts[4]) });
            }
            return result;
        }

        private static List<Candle> FilterRange(List<Candle> source, int years)
        {
            if (years <= 0 || source.Count == 0) return source;
            DateTime cutoff = source[source.Count - 1].Time.AddYears(-years);
            return source.Where(delegate(Candle item) { return item.Time >= cutoff; }).ToList();
        }

        public async Task<ValuationInfo> GetValuationAsync(string code, bool isEtf, double? stockPe, int years, string securityName = null)
        {
            string key = code + ":" + years.ToString(CultureInfo.InvariantCulture);
            Tuple<DateTime, ValuationInfo> cached;
            if (valuationCache.TryGetValue(key, out cached) && DateTime.Now - cached.Item1 < TimeSpan.FromHours(6)) return cached.Item2;

            ValuationInfo value;
            if (code == "510300") value = await GetCsi300ValuationAsync(years).ConfigureAwait(false);
            else if (code == "513500" || (isEtf && !string.IsNullOrWhiteSpace(securityName) && securityName.IndexOf("标普500", StringComparison.OrdinalIgnoreCase) >= 0))
                value = await GetSp500ValuationAsync(years).ConfigureAwait(false);
            else if (!isEtf)
            {
                try { value = await GetStockHistoricalValuationAsync(code, years).ConfigureAwait(false); }
                catch
                {
                    value = new ValuationInfo
                    {
                        CurrentPe = stockPe,
                        Percentile = null,
                        Years = years,
                        DataDate = stockPe.HasValue ? (DateTime?)DateTime.Today : null,
                        Source = "东方财富动态PE",
                        SampleCount = 0
                    };
                }
            }
            else value = new ValuationInfo
            {
                CurrentPe = stockPe,
                Percentile = null,
                Years = years,
                DataDate = stockPe.HasValue ? (DateTime?)DateTime.Today : null,
                Source = isEtf ? "暂未匹配跟踪指数" : "东方财富动态PE",
                SampleCount = 0
            };
            valuationCache[key] = Tuple.Create(DateTime.Now, value);
            return value;
        }

        private async Task<ValuationInfo> GetStockHistoricalValuationAsync(string code, int years)
        {
            int pageSize = Math.Max(280, Math.Min(1500, Math.Max(1, years) * 260 + 30));
            string url = "https://datacenter-web.eastmoney.com/api/data/v1/get?reportName=RPT_VALUEANALYSIS_DET"
                + "&columns=SECURITY_CODE%2CTRADE_DATE%2CPE_TTM"
                + "&filter=(SECURITY_CODE%3D%22" + code + "%22)"
                + "&pageNumber=1&pageSize=" + pageSize.ToString(CultureInfo.InvariantCulture)
                + "&sortTypes=-1&sortColumns=TRADE_DATE&source=WEB&client=WEB";
            Dictionary<string, object> root = Json(await client.GetStringAsync(url).ConfigureAwait(false));
            Dictionary<string, object> result = Child(root, "result");
            var points = new List<Tuple<DateTime, double>>();
            foreach (object raw in ArrayValue(result, "data"))
            {
                var row = raw as Dictionary<string, object>;
                DateTime date;
                double pe;
                if (row == null || !DateTime.TryParse(Convert.ToString(Value(row, "TRADE_DATE"), CultureInfo.InvariantCulture), out date)) continue;
                if (TryObjectNumber(Value(row, "PE_TTM"), out pe) && pe > 0) points.Add(Tuple.Create(date, pe));
            }
            return BuildValuation(points, years, "东方财富 · PE-TTM");
        }

        private async Task<ValuationInfo> GetCsi300ValuationAsync(int years)
        {
            DateTime end = DateTime.Today;
            DateTime start = end.AddYears(-Math.Max(1, years)).AddDays(-7);
            string url = "https://www.csindex.com.cn/csindex-home/perf/index-perf?indexCode=000300&startDate="
                + start.ToString("yyyyMMdd") + "&endDate=" + end.ToString("yyyyMMdd");
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri("https://www.csindex.com.cn/");
            HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            Dictionary<string, object> root = Json(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var points = new List<Tuple<DateTime, double>>();
            foreach (object raw in ArrayValue(root, "data"))
            {
                var row = raw as Dictionary<string, object>;
                DateTime date;
                double pe;
                if (row == null || !DateTime.TryParseExact(Convert.ToString(Value(row, "tradeDate")), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) continue;
                if (TryObjectNumber(Value(row, "peg"), out pe) && pe > 0) points.Add(Tuple.Create(date, pe));
            }
            return BuildValuation(points, years, "中证指数官方");
        }

        private async Task<ValuationInfo> GetSp500ValuationAsync(int years)
        {
            string html = await client.GetStringAsync("https://www.multpl.com/s-p-500-pe-ratio/table/by-month").ConfigureAwait(false);
            var points = new List<Tuple<DateTime, double>>();
            MatchCollection rows = Regex.Matches(html,
                "<tr[^>]*>\\s*<td>(?<date>[^<]+)</td>\\s*<td>(?<value>.*?)</td>\\s*</tr>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match row in rows)
            {
                DateTime date;
                double pe;
                string dateText = WebUtility.HtmlDecode(Regex.Replace(row.Groups["date"].Value, "<.*?>", "")).Trim();
                string valueText = WebUtility.HtmlDecode(Regex.Replace(row.Groups["value"].Value, "<.*?>", "")).Replace("†", "").Trim();
                if (DateTime.TryParse(dateText, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out date)
                    && double.TryParse(valueText, NumberStyles.Any, CultureInfo.InvariantCulture, out pe) && pe > 0)
                    points.Add(Tuple.Create(date, pe));
            }
            return BuildValuation(points, years, "Multpl · 月度PE");
        }

        private static ValuationInfo BuildValuation(List<Tuple<DateTime, double>> source, int years, string sourceName)
        {
            source.Sort(delegate(Tuple<DateTime, double> a, Tuple<DateTime, double> b) { return a.Item1.CompareTo(b.Item1); });
            if (source.Count == 0) return new ValuationInfo { Years = years, Source = sourceName };
            Tuple<DateTime, double> latest = source[source.Count - 1];
            DateTime cutoff = latest.Item1.AddYears(-Math.Max(1, years));
            List<double> values = source.Where(delegate(Tuple<DateTime, double> item) { return item.Item1 >= cutoff; }).Select(delegate(Tuple<DateTime, double> item) { return item.Item2; }).ToList();
            int atOrBelow = values.Count(delegate(double item) { return item <= latest.Item2; });
            return new ValuationInfo
            {
                CurrentPe = latest.Item2,
                Percentile = values.Count == 0 ? (double?)null : atOrBelow * 100.0 / values.Count,
                Years = years,
                DataDate = latest.Item1,
                Source = sourceName,
                SampleCount = values.Count
            };
        }

        private static DateTime ParseQuoteTime(string value)
        {
            DateTime result;
            return DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out result) ? result : DateTime.Now;
        }

        private static double Number(string value)
        {
            double result;
            return TryNumber(value, out result) ? result : 0;
        }

        private static bool TryNumber(string value, out double result)
        {
            return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryObjectNumber(object value, out double result)
        {
            if (value == null) { result = 0; return false; }
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
        }

        private static Dictionary<string, object> Json(string text)
        {
            var value = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(text) as Dictionary<string, object>;
            if (value == null) throw new InvalidOperationException("数据源返回了无效JSON");
            return value;
        }

        private static object Value(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> dictionary, string key)
        {
            return Value(dictionary, key) as Dictionary<string, object>;
        }

        private static object[] ArrayValue(Dictionary<string, object> dictionary, string key)
        {
            object value = Value(dictionary, key);
            object[] array = value as object[];
            return array ?? new object[0];
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
