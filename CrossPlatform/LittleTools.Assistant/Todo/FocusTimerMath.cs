using System.Globalization;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Focus countdown rules and the dial geometry, ported from the Windows module.
/// The countdown is wall-clock based, so it survives a restart exactly like the
/// Windows version does.
/// </summary>
internal static class FocusTimerMath
{
    /// <summary>The ring is normalised to a full 100 minute run, matching Windows.</summary>
    public const double FillReferenceSeconds = 100.0 * 60.0;

    private const double DialDeadZoneRadius = 35.0;
    private const double DetentTolerance = 0.7;
    private const int DetentStep = 5;

    public static FocusTimerData Create(string itemId, string itemText, int minutes, ITodoClock clock) =>
        new()
        {
            ItemId = itemId,
            ItemText = itemText,
            DurationMinutes = Math.Clamp(minutes, FocusTimerLimits.MinimumMinutes, FocusTimerLimits.MaximumMinutes),
            EndsAtUtc = clock.UtcNow.AddMinutes(minutes).ToString("o", CultureInfo.InvariantCulture)
        };

    public static bool TryGetEnd(string? endsAtUtc, out DateTime endsAt)
    {
        endsAt = default;
        if (string.IsNullOrEmpty(endsAtUtc)) return false;
        return DateTime.TryParse(endsAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out endsAt);
    }

    /// <summary>Prefers the timer when it is still valid for today and still open.</summary>
    public static bool IsActiveFor(FocusTimerData? timer, string? itemId) =>
        timer is not null && !string.IsNullOrEmpty(itemId) && timer.ItemId == itemId;

    public static double RemainingSeconds(FocusTimerData? timer, DateTime utcNow)
    {
        if (timer is null || !TryGetEnd(timer.EndsAtUtc, out var endsAt)) return 0;
        return Math.Max(0, (endsAt.ToUniversalTime() - utcNow).TotalSeconds);
    }

    public static double Fill(FocusTimerData? timer, DateTime utcNow) =>
        timer is null ? 0 : Math.Max(0, Math.Min(1, RemainingSeconds(timer, utcNow) / FillReferenceSeconds));

    public static bool IsUrgent(FocusTimerData? timer, DateTime utcNow) =>
        timer is not null && RemainingSeconds(timer, utcNow) <= 60;

    /// <summary>Drops a timer that is malformed or already finished; expired timers never re-notify.</summary>
    public static FocusTimerData? Normalize(FocusTimerData? timer, DateTime utcNow)
    {
        if (timer is null) return null;
        if (timer.DurationMinutes <= FocusTimerLimits.MinimumMinutes
            || timer.DurationMinutes > FocusTimerLimits.MaximumMinutes) return null;
        if (string.IsNullOrEmpty(timer.ItemId)) return null;
        if (!TryGetEnd(timer.EndsAtUtc, out var endsAt)) return null;
        if (endsAt.ToUniversalTime() <= utcNow) return null;
        return timer;
    }

    public static string FormatRemaining(double seconds)
    {
        var total = (int)Math.Ceiling(Math.Max(0, seconds));
        return (total / 60).ToString("00", CultureInfo.InvariantCulture)
               + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts a pointer position on the dial into a minute value, applying the
    /// dead zone, the 5 minute detents and the wrap-around handling of the
    /// Windows dial.
    /// </summary>
    public static int AngleToMinutes(double x, double y, double width, double height, int currentMinutes)
    {
        var dx = x - width / 2;
        var dy = y - height / 2;
        if (Math.Sqrt(dx * dx + dy * dy) < DialDeadZoneRadius) return currentMinutes;

        var angle = Math.Atan2(dx, -dy);
        if (angle < 0) angle += Math.PI * 2;
        var raw = angle / (Math.PI * 2) * FocusTimerLimits.MaximumMinutes;

        if (currentMinutes >= 75 && raw < 10) raw = FocusTimerLimits.MaximumMinutes;
        else if (currentMinutes <= 25 && raw > 90) raw = FocusTimerLimits.MinimumMinutes;

        var detent = Math.Round(raw / DetentStep) * DetentStep;
        var next = (int)Math.Round(Math.Abs(raw - detent) <= DetentTolerance ? detent : raw);
        return Math.Clamp(next, FocusTimerLimits.MinimumMinutes, FocusTimerLimits.MaximumMinutes);
    }
}
