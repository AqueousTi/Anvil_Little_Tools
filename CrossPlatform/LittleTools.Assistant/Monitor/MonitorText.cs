using System.Globalization;

namespace LittleTools.Assistant.Monitor;

/// <summary>A composed line plus the tone it is drawn in.</summary>
internal readonly record struct MonitorLine(string Text, MonitorTone Tone);

/// <summary>
/// Every user visible string the HUD and the detail window compose, ported from
/// <c>MonitorWindow.UpdateView</c> (AIUsageMonitor/Program.cs L1593-L1668) and
/// <c>DetailsWindow.UpdateView</c> (L2110-L2164). Keeping the composition here -
/// rather than inline in the Avalonia controls - is what lets the unit tests and
/// the render smoke pin the exact Windows wording, separators and rounding.
/// </summary>
internal static class MonitorText
{
    /// <summary>Windows codex row (Program.cs L1597-L1610).</summary>
    public static MonitorLine CodexCompact(UsageSnapshot state)
    {
        if (state.FiveHourRemaining.HasValue || state.WeeklyRemaining.HasValue)
        {
            var parts = new List<string>();
            if (state.FiveHourRemaining.HasValue) parts.Add("5h " + MonitorLayout.Percent(state.FiveHourRemaining.Value));
            if (state.WeeklyRemaining.HasValue) parts.Add("周 " + MonitorLayout.Percent(state.WeeklyRemaining.Value));
            if (!string.IsNullOrEmpty(state.CodexState)
                && state.CodexState.StartsWith("Codex 未运行", StringComparison.Ordinal))
                parts.Add("缓存");
            return new MonitorLine(string.Join("  ·  ", parts), MonitorLayout.CodexTone(state));
        }
        return new MonitorLine(state.CodexState ?? "不可用", MonitorTone.Muted);
    }

    /// <summary>Windows DeepSeek row (Program.cs L1612-L1625).</summary>
    public static MonitorLine DeepSeekCompact(UsageSnapshot state)
    {
        if (!string.IsNullOrEmpty(state.DeepSeekBalance))
        {
            var today = state.TodayDeepSeekSpend.HasValue
                ? " · 今约" + (state.DeepSeekCurrencySymbol ?? "¥")
                    + state.TodayDeepSeekSpend.Value.ToString("0.####", CultureInfo.InvariantCulture)
                : string.Empty;
            var tone = state.DeepSeekState == "余额不足" ? MonitorTone.Warning : MonitorTone.Primary;
            return new MonitorLine(state.DeepSeekBalance + today, tone);
        }
        return new MonitorLine(state.DeepSeekState ?? "不可用", MonitorTone.Muted);
    }

    /// <summary>Windows GLM row (Program.cs L1627-L1649).</summary>
    public static MonitorLine GlmCompact(UsageSnapshot state)
    {
        var hasPlan = state.GlmFiveHourRemaining.HasValue || state.GlmWeeklyRemaining.HasValue
            || state.GlmMonthlyRemaining.HasValue;
        if (state.GlmBalance.HasValue || state.TodayGlmSpend.HasValue || hasPlan)
        {
            var parts = new List<string>();
            if (state.GlmBalance.HasValue)
                parts.Add((state.GlmCurrencySymbol ?? "¥") + MonitorLayout.CompactNumber(state.GlmBalance.Value));
            if (state.TodayGlmSpend.HasValue)
                parts.Add("今约" + (state.GlmBalance.HasValue ? string.Empty : (state.GlmCurrencySymbol ?? "¥"))
                    + MonitorLayout.CompactNumber(state.TodayGlmSpend.Value));
            if (state.GlmFiveHourRemaining.HasValue) parts.Add("5h " + MonitorLayout.Percent(state.GlmFiveHourRemaining.Value));
            if (state.GlmWeeklyRemaining.HasValue) parts.Add("周 " + MonitorLayout.Percent(state.GlmWeeklyRemaining.Value));
            if (!state.GlmFiveHourRemaining.HasValue && !state.GlmWeeklyRemaining.HasValue
                && state.GlmMonthlyRemaining.HasValue)
                parts.Add("MCP " + MonitorLayout.Percent(state.GlmMonthlyRemaining.Value));
            if (!string.IsNullOrEmpty(state.GlmState) && state.GlmState != "正常"
                && !state.GlmState.Contains("正常", StringComparison.Ordinal))
                parts.Add("缓存");
            return new MonitorLine(string.Join(" · ", parts), MonitorLayout.GlmTone(state));
        }
        return new MonitorLine(state.GlmState ?? "未启用", MonitorTone.Muted);
    }

    /// <summary>Windows MonitorWindow.UpdateFooter (Program.cs L1676-L1682).</summary>
    public static string CompactFooter(UsageSnapshot? state, bool refreshing)
    {
        if (refreshing) return "正在刷新…";
        return state?.UpdatedAt.HasValue == true
            ? "更新 " + state.UpdatedAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "等待更新";
    }

    /// <summary>Windows DetailsWindow codex value (Program.cs L2125-L2128).</summary>
    public static string CodexDetailValue(UsageSnapshot state)
    {
        var parts = new List<string>();
        if (state.FiveHourRemaining.HasValue)
            parts.Add("5h " + Math.Round(state.FiveHourRemaining.Value).ToString("0", CultureInfo.InvariantCulture) + "%");
        if (state.WeeklyRemaining.HasValue)
            parts.Add("周 " + Math.Round(state.WeeklyRemaining.Value).ToString("0", CultureInfo.InvariantCulture) + "%");
        return parts.Count > 0 ? string.Join("   ", parts) : state.CodexState ?? string.Empty;
    }

    /// <summary>Windows DetailsWindow codex meta (Program.cs L2129-L2136).</summary>
    public static string CodexDetailMeta(UsageSnapshot state)
    {
        var details = new List<string>();
        if (!string.IsNullOrEmpty(state.PlanType)) details.Add("计划 " + state.PlanType);
        if (state.FiveHourReset.HasValue) details.Add("5h 重置 " + state.FiveHourReset.Value.ToString("HH:mm", CultureInfo.InvariantCulture));
        if (state.WeeklyReset.HasValue) details.Add("周重置 " + state.WeeklyReset.Value.ToString("M/d HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(state.CodexCredits)) details.Add("Credits " + state.CodexCredits);
        if (!string.IsNullOrEmpty(state.CodexState) && state.CodexState != "正常") details.Add(state.CodexState);
        if (state.CodexUpdatedAt.HasValue) details.Add("额度更新 " + state.CodexUpdatedAt.Value.ToString("M/d HH:mm", CultureInfo.InvariantCulture));
        return details.Count > 0 ? string.Join("  ·  ", details) : "等待官方额度数据";
    }

    /// <summary>Windows DetailsWindow deepseek value/meta (Program.cs L2138-L2140).</summary>
    public static string DeepSeekDetailValue(UsageSnapshot state) =>
        !string.IsNullOrEmpty(state.DeepSeekBalance) ? state.DeepSeekBalance : state.DeepSeekState ?? string.Empty;

    public static string DeepSeekDetailMeta(UsageSnapshot state) =>
        !string.IsNullOrEmpty(state.DeepSeekBreakdown) ? state.DeepSeekBreakdown : "读取 DEEPSEEK_API_KEY";

    /// <summary>Windows DetailsWindow glm value (Program.cs L2141-L2147).</summary>
    public static string GlmDetailValue(UsageSnapshot state)
    {
        var parts = new List<string>();
        var currency = state.GlmCurrencySymbol ?? "¥";
        if (state.GlmBalance.HasValue)
            parts.Add("余额 " + currency + state.GlmBalance.Value.ToString("0.##", CultureInfo.InvariantCulture));
        if (state.GlmFiveHourRemaining.HasValue)
            parts.Add("5h " + Math.Round(state.GlmFiveHourRemaining.Value).ToString("0", CultureInfo.InvariantCulture) + "%");
        if (state.GlmWeeklyRemaining.HasValue)
            parts.Add("周 " + Math.Round(state.GlmWeeklyRemaining.Value).ToString("0", CultureInfo.InvariantCulture) + "%");
        if (state.GlmMonthlyRemaining.HasValue)
            parts.Add("MCP 月 " + Math.Round(state.GlmMonthlyRemaining.Value).ToString("0", CultureInfo.InvariantCulture) + "%");
        return parts.Count > 0 ? string.Join("   ", parts) : state.GlmState ?? "未启用";
    }

    /// <summary>Windows DetailsWindow glm meta (Program.cs L2148-L2160).</summary>
    public static string GlmDetailMeta(UsageSnapshot state)
    {
        var glmHasPlan = state.GlmFiveHourRemaining.HasValue || state.GlmWeeklyRemaining.HasValue
            || state.GlmMonthlyRemaining.HasValue;
        var currency = state.GlmCurrencySymbol ?? "¥";
        var details = new List<string>();
        if (state.GlmTotalSpend.HasValue)
            details.Add("累计消费 " + currency + state.GlmTotalSpend.Value.ToString("0.##", CultureInfo.InvariantCulture));
        if (state.GlmWalletTotal.HasValue && (!state.GlmBalance.HasValue
            || Math.Abs(state.GlmWalletTotal.Value - state.GlmBalance.Value) > 0.000001))
            details.Add("账户总额 " + currency + state.GlmWalletTotal.Value.ToString("0.##", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(state.GlmPlanType)) details.Add("套餐 " + state.GlmPlanType);
        if (!string.IsNullOrEmpty(state.GlmQuotaBreakdown)) details.Add(state.GlmQuotaBreakdown);
        if (state.GlmFiveHourReset.HasValue) details.Add("5h 重置 " + state.GlmFiveHourReset.Value.ToString("HH:mm", CultureInfo.InvariantCulture));
        if (state.GlmWeeklyReset.HasValue) details.Add("周重置 " + state.GlmWeeklyReset.Value.ToString("M/d HH:mm", CultureInfo.InvariantCulture));
        if (state.GlmMonthlyReset.HasValue) details.Add("月重置 " + state.GlmMonthlyReset.Value.ToString("M/d HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(state.GlmState) && state.GlmState != "正常")
            details.Add(state.GlmState + (glmHasPlan && !state.GlmState.Contains("正常", StringComparison.Ordinal)
                ? " · 显示上次数据" : string.Empty));
        if (state.GlmUpdatedAt.HasValue) details.Add("额度更新 " + state.GlmUpdatedAt.Value.ToString("M/d HH:mm", CultureInfo.InvariantCulture));
        return details.Count > 0 ? string.Join("  ·  ", details) : "读取 GLM 账户余额与 Coding Plan 套餐配额";
    }

    /// <summary>Windows DetailsWindow.FormatTodaySpend (Program.cs L2172-L2177).</summary>
    public static string TodaySpend(double? spend, DateTime? trackingStart, string currency, DateTime today)
    {
        if (!spend.HasValue) return "今日 --";
        var scope = trackingStart.HasValue && trackingStart.Value <= today.Date.AddMinutes(1)
            ? "今日约 " : "监控后 ";
        return scope + currency + spend.Value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>Windows DetailsWindow.UpdateTrend title (Program.cs L2215-L2217).</summary>
    public static string TrendTitle(bool glm, bool partial, MonitorTrendRange range)
    {
        var label = range switch
        {
            MonitorTrendRange.Week => "本周",
            MonitorTrendRange.Month => "本月",
            _ => "今日"
        };
        var scope = partial ? (range == MonitorTrendRange.Day ? "监控后" : label + "监控后") : label;
        return (glm ? "GLM · " : "DeepSeek · ") + scope + "消费趋势（估算）";
    }

    /// <summary>Windows DetailsWindow.UpdateTrend axis labels (Program.cs L2222-L2226).</summary>
    public static string ChartMaximum(string currency, double maximum) =>
        currency + maximum.ToString("0.####", CultureInfo.InvariantCulture);

    public static string ChartMinimum(string currency) => currency + "0";

    public static string ChartStart(bool dayRange, DateTime visibleStart) =>
        dayRange ? visibleStart.ToString("HH:mm", CultureInfo.InvariantCulture) : visibleStart.ToString("M/d", CultureInfo.InvariantCulture);

    public static string ChartEnd(bool dayRange, DateTime now) =>
        dayRange ? now.ToString("HH:mm", CultureInfo.InvariantCulture) : now.ToString("M/d", CultureInfo.InvariantCulture);

    /// <summary>Windows DetailsWindow footer (Program.cs L2163).</summary>
    public static string DetailsUpdated(UsageSnapshot state) =>
        state.UpdatedAt.HasValue
            ? "更新 " + state.UpdatedAt.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "尚未更新";

    /// <summary>Windows compact row labels (Program.cs L1497-L1499).</summary>
    public const string CodexLabel = "CODEX";
    public const string DeepSeekLabel = "DEEPSEEK";
    public const string GlmLabel = "GLM";
}
