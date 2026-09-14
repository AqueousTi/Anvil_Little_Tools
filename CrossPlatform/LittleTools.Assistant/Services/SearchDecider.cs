namespace LittleTools.Assistant.Services;

internal static class SearchDecider
{
    private static readonly string[] Signals =
    [
        "最新", "现在", "当前", "今天", "本周", "新闻", "价格", "版本", "发布", "官网", "官方文档",
        "查一下", "搜索", "联网", "recent", "latest", "current", "today", "news", "price", "release",
        "documentation", "ubuntu 2", "安装源", "软件源", "apt repository"
    ];

    public static bool ShouldSearch(SearchPolicy policy, string input)
    {
        if (policy == SearchPolicy.On) return true;
        if (policy == SearchPolicy.Off) return false;
        return Signals.Any(signal => input.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }
}
