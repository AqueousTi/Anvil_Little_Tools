using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Serialization that stays byte-compatible with the Windows module. Windows used
/// <c>JavaScriptSerializer</c>, which writes <c>DateTime</c> as the legacy
/// <c>\/Date(ms since epoch UTC)\/</c> form. Reading accepts both that form and
/// ISO-8601, writing always emits the legacy form so a file can travel back to
/// Windows unchanged.
/// </summary>
internal static class TodoJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // JavaScriptSerializer writes raw UTF-8. Keeping CJK unescaped makes the
        // file readable and byte-comparable with what Windows produces.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new LegacyDateTimeConverter() }
    };

    public static string Serialize(DailyTodoData data) => JsonSerializer.Serialize(data, Options);

    public static DailyTodoData? Deserialize(string json) =>
        JsonSerializer.Deserialize<DailyTodoData>(json, Options);
}

internal sealed class LegacyDateTimeConverter : JsonConverter<DateTime>
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return FromUnixMilliseconds(reader.GetInt64());

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Unsupported DateTime token: " + reader.TokenType);

        var text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text)) return default;

        var legacy = TryParseLegacy(text);
        if (legacy.HasValue) return legacy.Value;

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return parsed;

        throw new JsonException("Unsupported DateTime value: " + text);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToLegacy(value));

    /// <summary>Formats a timestamp exactly like JavaScriptSerializer does.</summary>
    internal static string ToLegacy(DateTime value)
    {
        // JavaScriptSerializer serializes the UTC instant for Local values and
        // treats Unspecified as local, which is what the Windows module produced.
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var milliseconds = (long)Math.Floor((utc - Epoch).TotalMilliseconds);
        return "/Date(" + milliseconds.ToString(CultureInfo.InvariantCulture) + ")/";
    }

    /// <summary>
    /// Parses <c>/Date(ms)/</c> and the offset variant <c>/Date(ms+0800)/</c>.
    /// The result is the original local wall clock, so a local round trip through
    /// this converter preserves the value the user saw.
    /// </summary>
    internal static DateTime? TryParseLegacy(string text)
    {
        if (!text.StartsWith("/Date(", StringComparison.Ordinal) || !text.EndsWith(")/", StringComparison.Ordinal))
            return null;

        var inner = text[6..^2];
        var signIndex = inner.IndexOfAny(['+', '-'], 1);
        if (signIndex > 0)
        {
            var offsetText = inner[signIndex..];
            inner = inner[..signIndex];
            if (long.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offsetMilliseconds)
                && TryParseOffset(offsetText, out var offset))
            {
                var instant = DateTimeOffset.FromUnixTimeMilliseconds(offsetMilliseconds).ToOffset(offset);
                return DateTime.SpecifyKind(instant.DateTime, DateTimeKind.Local);
            }
            return null;
        }

        return long.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
            ? FromUnixMilliseconds(milliseconds)
            : null;
    }

    private static bool TryParseOffset(string text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (text.Length != 5) return false;
        var sign = text[0] == '-' ? -1 : 1;
        if (text[0] is not ('+' or '-')) return false;
        if (!int.TryParse(text.AsSpan(1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)) return false;
        if (!int.TryParse(text.AsSpan(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)) return false;
        offset = TimeSpan.FromMinutes(sign * (hours * 60 + minutes));
        return true;
    }

    private static DateTime FromUnixMilliseconds(long milliseconds)
    {
        var utc = Epoch.AddMilliseconds(milliseconds);
        // Match the wall clock the writer saw when the file was produced locally.
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
        return DateTime.SpecifyKind(local, DateTimeKind.Local);
    }
}
