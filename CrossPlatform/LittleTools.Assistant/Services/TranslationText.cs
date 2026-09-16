using System.Text.RegularExpressions;

namespace LittleTools.Assistant.Services;

internal sealed class TranslationText
{
    private readonly List<string> _protected = [];
    public string Text { get; }
    public bool IsEntirelyProtected { get; }

    private static readonly Regex Protected = new(
        @"```[\s\S]*?```|`[^`\r\n]+`|https?://[^\s<>]+|(?<!\w)(?:[A-Za-z]:\\|/)[\w./\\-]+|(?<!\w)--[a-zA-Z][\w-]*",
        RegexOptions.CultureInvariant);
    private static readonly Regex Command = new(
        @"^(?:\$\s*)?(?:sudo\s+\S+|(?:git|apt|apt-get|npm|pnpm|yarn|dotnet|docker|kubectl)\s+(?:install|update|upgrade|add|remove|run|build|test|publish|restore|status|diff|log|commit|push|pull|clone|checkout|switch|fetch|ps|exec|start|stop|compose|get|apply|delete)\b|(?:cd|ls|pwd|mkdir|rm|cat|chmod|chown|echo)\b)",
        RegexOptions.CultureInvariant);

    public TranslationText(string text)
    {
        IsEntirelyProtected = IsProtectedOnly(text);
        Text = Protected.Replace(text, match => Keep(match.Value));
        Text = string.Join("\n", Text.Split('\n').Select(line => Command.IsMatch(line.Trim()) ? Keep(line) : line));
    }

    private string Keep(string value)
    {
        var token = $"__LT_KEEP_{_protected.Count}__";
        _protected.Add(value);
        return token;
    }

    public string Restore(string translated)
    {
        // Restore outer command placeholders first, then any inline placeholders.
        for (var index = _protected.Count - 1; index >= 0; index--)
        {
            var token = $"__LT_KEEP_{index}__";
            if (!translated.Contains(token, StringComparison.Ordinal))
                throw new InvalidDataException("翻译服务改动了代码或路径，请将代码与说明分开翻译。");
            translated = translated.Replace(token, _protected[index], StringComparison.Ordinal);
        }
        return translated;
    }

    internal static bool IsProtectedOnly(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return true;
        return Protected.Replace(trimmed, "").Split('\n')
            .All(line => string.IsNullOrWhiteSpace(line) || Command.IsMatch(line.Trim()));
    }
}
