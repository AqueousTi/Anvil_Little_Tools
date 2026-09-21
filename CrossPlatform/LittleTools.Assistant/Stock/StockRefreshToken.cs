namespace LittleTools.Assistant.Stock;

/// <summary>
/// Identifies what a market data request was made for, so a late answer can be
/// dropped when it no longer describes what the user is looking at.
///
/// The port used to guard every refresh with a bare version counter. That stopped
/// an older response from overwriting a newer one, but it did not tie the answer to
/// the selection: switching the code or the period did not invalidate a request
/// that was already in flight (the counter only moved when the *next* refresh
/// started). Two user visible failures came out of that:
///
///   * a quote that arrived after the user switched was stored under the new code
///     and rendered next to it;
///   * a request that lost the counter race returned without applying anything, so
///     opening the detail window could leave the chart empty with nothing left to
///     retry it - the "K line is gone again" report.
///
/// A token carries the query (code, period, span) plus the counter. A response is
/// applied only while <see cref="Matches"/> still holds, which makes every applied
/// answer describe the live selection; the counter is kept for diagnostics.
/// </summary>
internal readonly record struct StockRefreshToken(string Code, string Period, int Years, int Version)
{
    /// <summary>True while the token still describes the live selection.</summary>
    public bool Matches(string code, string period, int years) =>
        string.Equals(Code, code, StringComparison.Ordinal)
        && string.Equals(Period, period, StringComparison.Ordinal)
        && Years == years;

    public override string ToString() =>
        Code + "/" + Period + "/" + Years.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "#" + Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
