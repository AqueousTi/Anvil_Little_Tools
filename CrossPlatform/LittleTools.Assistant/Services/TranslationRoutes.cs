namespace LittleTools.Assistant.Services;

internal sealed record TranslationRoute(string Id, string Label, string Source, string Target, bool SmartChineseEnglish = false)
{
    public string InstructionFor(string input)
    {
        if (SmartChineseEnglish)
            return TranslationRoutes.ContainsChinese(input)
                ? "The source is Chinese. Translate it into natural English."
                : "The source is not Chinese. Translate it into Simplified Chinese.";

        var source = Source == "auto" ? "Auto-detect the source language" : $"The source language is {TranslationRoutes.LanguageName(Source)}";
        return $"{source}. Translate it into {TranslationRoutes.LanguageName(Target)}.";
    }
}

internal static class TranslationRoutes
{
    public const string DefaultId = "smart-zh-en";

    public static readonly IReadOnlyList<TranslationRoute> All =
    [
        new(DefaultId, "中英互译", "auto", "auto", true),
        new("auto-zh", "自动检测 → 中文", "auto", "zh"),
        new("auto-en", "自动检测 → 英语", "auto", "en"),
        new("zh-en", "中文 → 英语", "zh", "en"),
        new("en-zh", "英语 → 中文", "en", "zh"),
        new("zh-ja", "中文 → 日语", "zh", "ja"),
        new("zh-ko", "中文 → 韩语", "zh", "ko"),
        new("ja-zh", "日语 → 中文", "ja", "zh"),
        new("ko-zh", "韩语 → 中文", "ko", "zh"),
        new("en-ja", "英语 → 日语", "en", "ja"),
        new("en-ko", "英语 → 韩语", "en", "ko"),
        new("fr-zh", "法语 → 中文", "fr", "zh"),
        new("de-zh", "德语 → 中文", "de", "zh"),
        new("ru-zh", "俄语 → 中文", "ru", "zh")
    ];

    public static TranslationRoute Default => All[0];

    public static TranslationRoute Find(string? id) =>
        All.FirstOrDefault(route => string.Equals(route.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    internal static bool ContainsChinese(string text) =>
        text.Any(character => character is >= '\u3400' and <= '\u9FFF');

    internal static string LanguageName(string code) => code switch
    {
        "zh" => "Simplified Chinese",
        "en" => "English",
        "ja" => "Japanese",
        "ko" => "Korean",
        "fr" => "French",
        "de" => "German",
        "ru" => "Russian",
        _ => code
    };
}
