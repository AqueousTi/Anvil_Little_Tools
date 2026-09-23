namespace LittleTools.Assistant.Monitor;

/// <summary>
/// The day/week/month spending estimate, ported from the Windows
/// <c>DailyUsageTracker</c> (AIUsageMonitor/Program.cs L1157-L1266) and
/// <c>GlmUsageTracker</c> (L1276-L1391).
///
/// DeepSeek only publishes a balance, so a range's spend is the sum of the
/// balance decreases between adjacent samples, with the last sample before the
/// range boundary reused as the baseline (that is what makes a restart not lose
/// the history). GLM prefers the account report's cumulative spend - it is
/// recharge proof - and falls back to balance decreases when only the older
/// balance endpoint answered (L1339-L1343). Both keep 35 days.
/// </summary>
internal sealed class MonitorUsageTracker
{
    /// <summary>Windows keeps the last 35 days (Program.cs L1180, L1291).</summary>
    internal const int RetentionDays = 35;

    /// <summary>Windows samples at most once a minute (Program.cs L1186, L1298).</summary>
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    /// <summary>Windows treats two balances as equal below this (Program.cs L1187, L1326).</summary>
    internal const double Epsilon = 0.000001;

    private readonly MonitorStore _store;
    private readonly Func<DateTime> _now;

    public MonitorUsageTracker(MonitorStore store, Func<DateTime>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTime.Now);
    }

    /// <summary>Windows MonitorController.RefreshNow's tracker pass (Program.cs L365-L366).</summary>
    public void Update(UsageSnapshot state)
    {
        var now = _now();
        UpdateDeepSeek(state, now);
        UpdateGlm(state, now);
    }

    // ------------------------------------------------------------- DeepSeek

    /// <summary>Windows DailyUsageTracker.Update (Program.cs L1175-L1209).</summary>
    internal void UpdateDeepSeek(UsageSnapshot state, DateTime now)
    {
        var samples = _store.LoadBalanceSamples();
        samples.RemoveAll(sample => sample is null);
        var retention = now.Date.AddDays(-RetentionDays);
        samples.RemoveAll(sample => sample.Timestamp < retention);

        if (state.DeepSeekCurrentBalance.HasValue && !string.IsNullOrEmpty(state.DeepSeekCurrencySymbol))
        {
            var latest = samples.Count > 0 ? samples[^1] : null;
            if (latest is null || now - latest.Timestamp >= SampleInterval
                || Math.Abs(latest.Balance - state.DeepSeekCurrentBalance.Value) > Epsilon)
            {
                samples.Add(new BalanceSample
                {
                    Timestamp = now,
                    Balance = state.DeepSeekCurrentBalance.Value,
                    Currency = state.DeepSeekCurrencySymbol
                });
                _store.SaveBalanceSamples(samples);
            }
        }

        var (today, weekStart, monthStart) = Boundaries(now);
        state.DeepSeekTodayPoints = BuildRange(samples, today, out var todaySpend, out var todayTracking);
        state.TodayDeepSeekSpend = todaySpend;
        state.DeepSeekTrackingStart = todayTracking;
        state.DeepSeekWeekPoints = BuildRange(samples, weekStart, out var weekSpend, out var weekTracking);
        state.WeeklyDeepSeekSpend = weekSpend;
        state.DeepSeekWeekTrackingStart = weekTracking;
        state.DeepSeekMonthPoints = BuildRange(samples, monthStart, out var monthSpend, out var monthTracking);
        state.MonthlyDeepSeekSpend = monthSpend;
        state.DeepSeekMonthTrackingStart = monthTracking;
        if (string.IsNullOrEmpty(state.DeepSeekCurrencySymbol) && samples.Count > 0)
            state.DeepSeekCurrencySymbol = samples[^1].Currency;
    }

    /// <summary>Windows Program.cs L1199-L1202: local midnight, Monday, first of the month.</summary>
    internal static (DateTime Today, DateTime WeekStart, DateTime MonthStart) Boundaries(DateTime now)
    {
        var today = now.Date;
        var daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
        return (today, today.AddDays(-daysSinceMonday), new DateTime(now.Year, now.Month, 1));
    }

    /// <summary>Windows DailyUsageTracker.BuildRange (Program.cs L1211-L1246).</summary>
    internal static List<UsagePoint> BuildRange(List<BalanceSample> samples, DateTime rangeStart,
        out double? spend, out DateTime? trackingStart)
    {
        var rangeSamples = new List<BalanceSample>();
        BalanceSample? baseline = null;
        foreach (var sample in samples)
        {
            if (sample.Timestamp >= rangeStart) rangeSamples.Add(sample);
            else if (baseline is null || sample.Timestamp > baseline.Timestamp) baseline = sample;
        }
        rangeSamples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));

        var points = new List<UsagePoint>();
        spend = null;
        trackingStart = null;
        if (rangeSamples.Count == 0) return points;

        trackingStart = baseline is null ? rangeSamples[0].Timestamp : rangeStart;
        double cumulativeSpend = 0;
        points.Add(new UsagePoint { Timestamp = trackingStart.Value, Value = 0 });
        var previous = baseline ?? rangeSamples[0];
        var firstIndex = baseline is null ? 1 : 0;
        for (var index = firstIndex; index < rangeSamples.Count; index++)
        {
            var decrease = previous.Balance - rangeSamples[index].Balance;
            if (decrease > 0) cumulativeSpend += decrease;
            points.Add(new UsagePoint { Timestamp = rangeSamples[index].Timestamp, Value = cumulativeSpend });
            previous = rangeSamples[index];
        }
        spend = cumulativeSpend;
        return points;
    }

    // ------------------------------------------------------------------ GLM

    /// <summary>Windows GlmUsageTracker.Update (Program.cs L1286-L1321).</summary>
    internal void UpdateGlm(UsageSnapshot state, DateTime now)
    {
        var samples = _store.LoadGlmSamples();
        samples.RemoveAll(sample => sample is null);
        var retention = now.Date.AddDays(-RetentionDays);
        samples.RemoveAll(sample => sample.Timestamp < retention);

        var freshWallet = state.GlmWalletUpdatedAt.HasValue
            && now - state.GlmWalletUpdatedAt.Value < SampleInterval;
        if (freshWallet && (state.GlmBalance.HasValue || state.GlmTotalSpend.HasValue))
        {
            var latest = samples.Count > 0 ? samples[^1] : null;
            if (latest is null || now - latest.Timestamp >= SampleInterval
                || !Same(latest.Balance, state.GlmBalance) || !Same(latest.TotalSpend, state.GlmTotalSpend))
            {
                samples.Add(new GlmSpendSample
                {
                    Timestamp = now,
                    Balance = state.GlmBalance,
                    TotalSpend = state.GlmTotalSpend,
                    Currency = state.GlmCurrencySymbol
                });
                _store.SaveGlmSamples(samples);
            }
        }

        var (today, weekStart, monthStart) = Boundaries(now);
        state.GlmTodayPoints = BuildGlmRange(samples, today, out var todaySpend, out var todayTracking);
        state.TodayGlmSpend = todaySpend;
        state.GlmTrackingStart = todayTracking;
        state.GlmWeekPoints = BuildGlmRange(samples, weekStart, out var weekSpend, out var weekTracking);
        state.WeeklyGlmSpend = weekSpend;
        state.GlmWeekTrackingStart = weekTracking;
        state.GlmMonthPoints = BuildGlmRange(samples, monthStart, out var monthSpend, out var monthTracking);
        state.MonthlyGlmSpend = monthSpend;
        state.GlmMonthTrackingStart = monthTracking;
        if (string.IsNullOrEmpty(state.GlmCurrencySymbol) && samples.Count > 0)
            state.GlmCurrencySymbol = samples[^1].Currency;
    }

    /// <summary>Windows GlmUsageTracker.Same (Program.cs L1323-L1327).</summary>
    internal static bool Same(double? first, double? second)
    {
        if (!first.HasValue || !second.HasValue) return first.HasValue == second.HasValue;
        return Math.Abs(first.Value - second.Value) <= Epsilon;
    }

    /// <summary>Windows GlmUsageTracker.BuildRange (Program.cs L1329-L1374).</summary>
    internal static List<UsagePoint> BuildGlmRange(List<GlmSpendSample> samples, DateTime rangeStart,
        out double? spend, out DateTime? trackingStart)
    {
        var rangeSamples = samples.Where(sample => sample.Timestamp >= rangeStart).ToList();
        rangeSamples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));

        var useCumulativeSpend = rangeSamples.Exists(sample => sample.TotalSpend.HasValue);
        rangeSamples.RemoveAll(sample => useCumulativeSpend ? !sample.TotalSpend.HasValue : !sample.Balance.HasValue);

        GlmSpendSample? baseline = null;
        foreach (var sample in samples)
        {
            var compatible = useCumulativeSpend ? sample.TotalSpend.HasValue : sample.Balance.HasValue;
            if (compatible && sample.Timestamp < rangeStart
                && (baseline is null || sample.Timestamp > baseline.Timestamp)) baseline = sample;
        }

        var points = new List<UsagePoint>();
        spend = null;
        trackingStart = null;
        if (rangeSamples.Count == 0) return points;

        trackingStart = baseline is null ? rangeSamples[0].Timestamp : rangeStart;
        double cumulative = 0;
        points.Add(new UsagePoint { Timestamp = trackingStart.Value, Value = 0 });
        var previous = baseline ?? rangeSamples[0];
        var firstIndex = baseline is null ? 1 : 0;
        for (var index = firstIndex; index < rangeSamples.Count; index++)
        {
            var increase = useCumulativeSpend
                ? rangeSamples[index].TotalSpend!.Value - previous.TotalSpend!.Value
                : previous.Balance!.Value - rangeSamples[index].Balance!.Value;
            if (increase > 0) cumulative += increase;
            points.Add(new UsagePoint { Timestamp = rangeSamples[index].Timestamp, Value = cumulative });
            previous = rangeSamples[index];
        }
        spend = cumulative;
        return points;
    }
}
