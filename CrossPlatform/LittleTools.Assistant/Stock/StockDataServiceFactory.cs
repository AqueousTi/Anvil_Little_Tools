namespace LittleTools.Assistant.Stock;

/// <summary>
/// Chooses between the live market data service and the recorded replay. The
/// replay exists for deterministic offline verification (the headless render
/// smoke and X11 interaction runs) and is only used when
/// <c>LITTLETOOLS_STOCK_FIXTURES</c> is set, so a normal run always talks to the
/// real sources.
/// </summary>
internal static class StockDataServiceFactory
{
    public const string ReplayVariable = "LITTLETOOLS_STOCK_FIXTURES";

    public static bool IsFixtureReplayEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(ReplayVariable);
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value is not ("0" or "false" or "FALSE" or "False" or "no");
        }
    }

    public static StockDataService Create() =>
        IsFixtureReplayEnabled ? StockFixtures.CreateService() : new StockDataService();
}
