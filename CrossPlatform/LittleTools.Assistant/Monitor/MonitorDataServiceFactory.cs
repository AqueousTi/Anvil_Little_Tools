namespace LittleTools.Assistant.Monitor;

/// <summary>
/// Chooses between the live monitor sources and the recorded replay. The replay
/// exists for deterministic offline verification (the headless render smoke and the
/// X11 interaction runs) and is only used when <c>LITTLETOOLS_MONITOR_FIXTURES</c> is
/// set, so a normal run always talks to the real hosts - the same arrangement as
/// <c>StockDataServiceFactory</c> with LITTLETOOLS_STOCK_FIXTURES.
/// </summary>
internal static class MonitorDataServiceFactory
{
    public const string ReplayVariable = "LITTLETOOLS_MONITOR_FIXTURES";

    public static bool IsFixtureReplayEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(ReplayVariable);
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value is not ("0" or "false" or "FALSE" or "False" or "no");
        }
    }

    public static MonitorDataService Create() =>
        IsFixtureReplayEnabled
            ? new MonitorDataService(new MonitorFixtureHandler(MonitorFixtureScenario.Success), FixtureCodexAppServer.LoggedIn())
            {
                IsFixtureReplay = true
            }
            : new MonitorDataService();
}
