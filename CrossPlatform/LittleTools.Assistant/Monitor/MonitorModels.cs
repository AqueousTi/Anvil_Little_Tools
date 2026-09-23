namespace LittleTools.Assistant.Monitor;

/// <summary>
/// The data the HUD renders. Ported field for field from the Windows
/// <c>UsageSnapshot</c> (AIUsageMonitor/Program.cs L47-L99) so the shared
/// <c>snapshot.json</c> keeps the same public PascalCase field names and either
/// platform can read the other's file.
/// </summary>
internal sealed class UsageSnapshot
{
    public bool CodexEnabled { get; set; } = true;
    public bool DeepSeekEnabled { get; set; } = true;
    public bool GlmEnabled { get; set; }
    public string? CodexState { get; set; }
    public double? FiveHourRemaining { get; set; }
    public double? WeeklyRemaining { get; set; }
    public DateTime? FiveHourReset { get; set; }
    public DateTime? WeeklyReset { get; set; }
    public string? PlanType { get; set; }
    public string? CodexCredits { get; set; }
    public DateTime? CodexUpdatedAt { get; set; }
    public string? DeepSeekState { get; set; }
    public string? DeepSeekBalance { get; set; }
    public string? DeepSeekBreakdown { get; set; }
    public double? DeepSeekCurrentBalance { get; set; }
    public string? DeepSeekCurrencySymbol { get; set; }
    public string? GlmState { get; set; }
    public double? GlmBalance { get; set; }
    public double? GlmWalletTotal { get; set; }
    public double? GlmTotalSpend { get; set; }
    public string? GlmCurrencySymbol { get; set; }
    public double? GlmFiveHourRemaining { get; set; }
    public double? GlmWeeklyRemaining { get; set; }
    public double? GlmMonthlyRemaining { get; set; }
    public DateTime? GlmFiveHourReset { get; set; }
    public DateTime? GlmWeeklyReset { get; set; }
    public DateTime? GlmMonthlyReset { get; set; }
    public string? GlmPlanType { get; set; }
    public string? GlmQuotaBreakdown { get; set; }
    public DateTime? GlmUpdatedAt { get; set; }
    public DateTime? GlmWalletUpdatedAt { get; set; }
    public double? TodayGlmSpend { get; set; }
    public double? WeeklyGlmSpend { get; set; }
    public double? MonthlyGlmSpend { get; set; }
    public DateTime? GlmTrackingStart { get; set; }
    public DateTime? GlmWeekTrackingStart { get; set; }
    public DateTime? GlmMonthTrackingStart { get; set; }
    public List<UsagePoint> GlmTodayPoints { get; set; } = [];
    public List<UsagePoint> GlmWeekPoints { get; set; } = [];
    public List<UsagePoint> GlmMonthPoints { get; set; } = [];
    public double? TodayDeepSeekSpend { get; set; }
    public double? WeeklyDeepSeekSpend { get; set; }
    public double? MonthlyDeepSeekSpend { get; set; }
    public DateTime? DeepSeekTrackingStart { get; set; }
    public DateTime? DeepSeekWeekTrackingStart { get; set; }
    public DateTime? DeepSeekMonthTrackingStart { get; set; }
    public List<UsagePoint> DeepSeekTodayPoints { get; set; } = [];
    public List<UsagePoint> DeepSeekWeekPoints { get; set; } = [];
    public List<UsagePoint> DeepSeekMonthPoints { get; set; } = [];
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Windows <c>ProviderSettings</c> (Program.cs L101-L112).</summary>
internal sealed class ProviderSettings
{
    public bool CodexEnabled { get; set; } = true;
    public bool DeepSeekEnabled { get; set; } = true;
    public bool GlmEnabled { get; set; }
    public string DeepSeekSource { get; set; } = "Environment";
    public string DeepSeekEnvironment { get; set; } = "DEEPSEEK_API_KEY";
    public string? DeepSeekProtectedKey { get; set; }
    public string GlmSource { get; set; } = "Environment";
    public string GlmEnvironment { get; set; } = "ZHIPUAI_API_KEY";
    public string? GlmProtectedKey { get; set; }

    public ProviderSettings Clone() => new()
    {
        CodexEnabled = CodexEnabled,
        DeepSeekEnabled = DeepSeekEnabled,
        GlmEnabled = GlmEnabled,
        DeepSeekSource = DeepSeekSource,
        DeepSeekEnvironment = DeepSeekEnvironment,
        DeepSeekProtectedKey = DeepSeekProtectedKey,
        GlmSource = GlmSource,
        GlmEnvironment = GlmEnvironment,
        GlmProtectedKey = GlmProtectedKey
    };
}

/// <summary>Windows <c>BalanceSample</c> (Program.cs L1150-L1155).</summary>
internal sealed class BalanceSample
{
    public DateTime Timestamp { get; set; }
    public double Balance { get; set; }
    public string? Currency { get; set; }
}

/// <summary>Windows <c>GlmSpendSample</c> (Program.cs L1268-L1274).</summary>
internal sealed class GlmSpendSample
{
    public DateTime Timestamp { get; set; }
    public double? Balance { get; set; }
    public double? TotalSpend { get; set; }
    public string? Currency { get; set; }
}

/// <summary>Windows <c>UsagePoint</c> (Program.cs L170-L174).</summary>
internal sealed class UsagePoint
{
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
}

/// <summary>Windows <c>RateWindow</c> (Program.cs L211-L216).</summary>
internal sealed class RateWindow
{
    public double UsedPercent { get; set; }
    public int WindowDurationMins { get; set; }
    public DateTime? ResetsAt { get; set; }
}

/// <summary>Windows <c>CodexUsage</c> (Program.cs L218-L224).</summary>
internal sealed class CodexUsage
{
    public RateWindow? Primary { get; set; }
    public RateWindow? Secondary { get; set; }
    public string? PlanType { get; set; }
    public string? Credits { get; set; }
}

/// <summary>Windows <c>DeepSeekUsage</c> (Program.cs L758-L765).</summary>
internal sealed class DeepSeekUsage
{
    public bool Available { get; set; }
    public string? TotalDisplay { get; set; }
    public string? Breakdown { get; set; }
    public double? PrimaryBalance { get; set; }
    public string? CurrencySymbol { get; set; }
}

/// <summary>Windows <c>GlmUsage</c> (Program.cs L842-L855).</summary>
internal sealed class GlmUsage
{
    public bool QuotaRead { get; set; }
    public bool WalletRead { get; set; }
    public RateWindow? FiveHour { get; set; }
    public RateWindow? Weekly { get; set; }
    public RateWindow? Monthly { get; set; }
    public string? Level { get; set; }
    public string? Breakdown { get; set; }
    public double? Balance { get; set; }
    public double? WalletTotal { get; set; }
    public double? TotalSpend { get; set; }
    public string? CurrencySymbol { get; set; }
}

/// <summary>Windows <c>GlmWallet</c> (Program.cs L857-L863).</summary>
internal sealed class GlmWallet
{
    public double? Balance { get; set; }
    public double? WalletTotal { get; set; }
    public double? TotalSpend { get; set; }
    public string? CurrencySymbol { get; set; }
}

/// <summary>Which provider row a trend chart is drawn for (Windows <c>selectedProvider</c>).</summary>
internal enum MonitorTrendProvider
{
    DeepSeek = 0,
    Glm = 1
}

/// <summary>The three way time range switch of the trend chart (Windows <c>selectedRange</c>).</summary>
internal enum MonitorTrendRange
{
    Day = 0,
    Week = 1,
    Month = 2
}
