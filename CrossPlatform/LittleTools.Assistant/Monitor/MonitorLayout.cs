using System.Globalization;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// The colour a value is drawn in. Windows returned a WPF <c>Brush</c> from
/// <c>StatusBrush</c>/<c>BalanceStatusBrush</c>/<c>MutedBrush</c> (AIUsageMonitor
/// Program.cs L1693-L1707); the port keeps the decision here as a tone so both the
/// windows and the unit tests can assert it, and <see cref="MonitorTheme"/> maps
/// the tone to the brush.
/// </summary>
internal enum MonitorTone
{
    /// <summary>Brushes.White.</summary>
    Primary,

    /// <summary>ARGB(180, 210, 214, 224).</summary>
    Muted,

    /// <summary>RGB(255, 198, 92).</summary>
    Warning,

    /// <summary>RGB(255, 102, 119).</summary>
    Danger
}

/// <summary>
/// Pure geometry, clamping and number formatting, ported from the Windows
/// <c>MonitorWindow</c>/<c>DetailsWindow</c> layout and brush helpers so the
/// formulas are asserted by unit tests instead of only being visible in a render.
/// </summary>
internal static class MonitorLayout
{
    /// <summary>Windows MonitorWindow ctor: 316 x 92, radius 15, padding 14,10,14,9.</summary>
    public const double CompactWidth = 316;
    public const double CompactHeight = 92;
    public const double CompactCornerRadius = 15;

    /// <summary>Windows DetailsWindow ctor: 380 x 505, radius 17, padding 19,16,19,16.</summary>
    public const double DetailsWidth = 380;
    public const double DetailsHeight = 505;
    public const double DetailsCornerRadius = 17;

    /// <summary>Windows compact rows: 20 + 20 + 20 + 12 (Program.cs L1493-L1496).</summary>
    public const double BaseRowHeight = 20;
    public const double BaseFooterHeight = 12;

    /// <summary>
    /// The dot column of a compact row (Windows Program.cs L1554).
    /// </summary>
    public const double CompactDotColumn = 12;

    /// <summary>
    /// The label column of a compact row. Windows uses 79 for "DEEPSEEK" in Segoe UI
    /// Semibold 10.5; the Linux stack falls back to Inter and measures the same word
    /// at about 89, so the column is widened instead of letting the label run into
    /// the value cell. Then 316 - 14 - 14 - 1 - 1 border - 12 - 92 leaves 182 for the
    /// value, which still fits the codex and deepseek rows; the longer GLM row is
    /// ellipsised exactly like the Windows HUD cuts it.
    /// </summary>
    public const double CompactLabelColumn = 92;

    /// <summary>Windows ProviderSettingsWindow: 430 x 470 (Program.cs L532).</summary>
    public const double SettingsWidth = 430;
    public const double SettingsHeight = 470;

    /// <summary>
    /// Windows SetCompactProviderVisibility (Program.cs L1655-L1668): one provider
    /// gets a 48 tall row, two get 27, three keep 20; disabled providers collapse
    /// to zero instead of leaving a placeholder row.
    /// </summary>
    public static double CompactRowHeight(int enabledCount) =>
        enabledCount <= 1 ? 48 : enabledCount == 2 ? 27 : BaseRowHeight;

    /// <summary>
    /// Windows keeps the footer at least 12 tall and lets it absorb the space the
    /// hidden rows gave back: <c>Math.Max(12, 72 - rowHeight * Math.Max(1, count))</c>.
    /// </summary>
    public static double CompactFooterHeight(int enabledCount) =>
        Math.Max(BaseFooterHeight, 72 - CompactRowHeight(enabledCount) * Math.Max(1, enabledCount));

    /// <summary>Windows DetailsWindow.UpdateView (Program.cs L2118-L2119).</summary>
    public static double DetailsWindowHeight(bool codexEnabled, bool deepSeekEnabled, bool glmEnabled)
    {
        var trendEnabled = deepSeekEnabled || glmEnabled;
        return Math.Max(205, 505
            - (codexEnabled ? 0 : 72)
            - (deepSeekEnabled ? 0 : 72)
            - (glmEnabled ? 0 : 70)
            - (trendEnabled ? 0 : 148));
    }

    /// <summary>Windows Remaining/ApplyRateWindow (Program.cs L435, L486-L489).</summary>
    public static double Remaining(double usedPercent) => Math.Max(0, Math.Min(100, 100 - usedPercent));

    /// <summary>
    /// Windows DetailsWindow.UpdateView section mapping (Program.cs L2113-L2117):
    /// the codex block, the deepseek block, the glm block and the trend block each
    /// collapse as a whole, and the trend block needs at least one of DS/GLM.
    /// </summary>
    public static bool[] DetailsSections(bool codexEnabled, bool deepSeekEnabled, bool glmEnabled) =>
        [codexEnabled, deepSeekEnabled, glmEnabled, deepSeekEnabled || glmEnabled];

    public static double? Remaining(RateWindow? window) =>
        window is null ? null : Remaining(window.UsedPercent);

    /// <summary>Windows MonitorWindow.Minimum (Program.cs L1684-L1689).</summary>
    public static double Minimum(double? first, double? second)
    {
        if (!first.HasValue) return second ?? 100;
        if (!second.HasValue) return first.Value;
        return Math.Min(first.Value, second.Value);
    }

    /// <summary>Windows Percent (Program.cs L1691): Math.Round (banker's) plus "%".</summary>
    public static string Percent(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Windows FormatCompactNumber (Program.cs L1692).</summary>
    public static string CompactNumber(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Windows StatusBrush (Program.cs L1693-L1698): 10 and 25 percent thresholds.</summary>
    public static MonitorTone StatusTone(double remaining) =>
        remaining <= 10 ? MonitorTone.Danger : remaining <= 25 ? MonitorTone.Warning : MonitorTone.Primary;

    /// <summary>Windows BalanceStatusBrush (Program.cs L1699-L1704): 0 and 10 unit thresholds.</summary>
    public static MonitorTone BalanceTone(double balance) =>
        balance <= 0 ? MonitorTone.Danger : balance < 10 ? MonitorTone.Warning : MonitorTone.Primary;

    /// <summary>Windows codex tone: the smaller of the two windows, 100 when neither is known.</summary>
    public static MonitorTone CodexTone(UsageSnapshot state) =>
        StatusTone(Minimum(state.FiveHourRemaining, state.WeeklyRemaining));

    /// <summary>Windows glm tone (Program.cs L1641-L1643).</summary>
    public static MonitorTone GlmTone(UsageSnapshot state)
    {
        if (state.GlmFiveHourRemaining.HasValue || state.GlmWeeklyRemaining.HasValue
            || state.GlmMonthlyRemaining.HasValue)
            return StatusTone(Minimum(Minimum(state.GlmFiveHourRemaining, state.GlmWeeklyRemaining),
                state.GlmMonthlyRemaining));
        return state.GlmBalance.HasValue ? BalanceTone(state.GlmBalance.Value) : MonitorTone.Primary;
    }

    /// <summary>Windows MonitorController.FriendlyError (Program.cs L399-L415).</summary>
    public static string FriendlyError(Exception exception, string fallback)
    {
        var message = exception.Message ?? string.Empty;
        if (message.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
            return "需要 Codex 登录";
        if (message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            return "API Key 无效";
        if (message.Contains("no coding plan quota", StringComparison.OrdinalIgnoreCase))
            return "无 Coding Plan 配额";
        if (message.Contains("API key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("environment", StringComparison.OrdinalIgnoreCase))
            return "未配置 API Key";
        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "连接超时";
        return fallback;
    }

    /// <summary>Windows MonitorController.ApplyRateWindow (Program.cs L432-L446).</summary>
    public static void ApplyRateWindow(UsageSnapshot state, RateWindow? window)
    {
        if (window is null) return;
        var remaining = Remaining(window.UsedPercent);
        if (window.WindowDurationMins <= 360)
        {
            state.FiveHourRemaining = remaining;
            state.FiveHourReset = window.ResetsAt;
        }
        else
        {
            state.WeeklyRemaining = remaining;
            state.WeeklyReset = window.ResetsAt;
        }
    }
}
