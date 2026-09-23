using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace LittleTools.Assistant.Monitor;

/// <summary>
/// Serialization for every file the module shares with Windows
/// (<c>providers.json</c>, <c>snapshot.json</c>, <c>usage-history.json</c>,
/// <c>glm-usage-history.json</c>).
///
/// Windows wrote them with <c>System.Web.Script.Serialization.JavaScriptSerializer</c>
/// (AIUsageMonitor/Program.cs L122, L130, L188, L205, L1253, L1262, L1381, L1388),
/// which differs from <c>System.Text.Json</c> in three ways that matter for a file
/// both platforms read and write:
///   * a <c>DateTime</c> is written as <c>"\/Date(unix-milliseconds)\/"</c>, not ISO 8601;
///   * non ASCII text is written raw, never as <c>\uXXXX</c>;
///   * a non finite double is written as a bare <c>NaN</c>/<c>Infinity</c> literal.
/// All three are reproduced here, and reading accepts the Windows form as well as
/// plain ISO 8601 (so a hand edited file still loads).
/// </summary>
internal static class MonitorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JavaScriptSerializerDateConverter(), new MonitorNonFiniteDoubleConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(MaskNonFiniteLiterals(json), Options);

    /// <summary>
    /// Replaces the bare <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> values
    /// JavaScriptSerializer emits with <c>null</c>, which System.Text.Json accepts.
    /// </summary>
    internal static string MaskNonFiniteLiterals(string json)
    {
        var builder = new StringBuilder(json.Length);
        var inString = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                builder.Append(character);
                if (character == '\\' && index + 1 < json.Length)
                {
                    builder.Append(json[index + 1]);
                    index++;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '"')
            {
                inString = true;
                builder.Append(character);
                continue;
            }

            if (TryLiteralLength(json, index, out var length))
            {
                builder.Append("null");
                index += length - 1;
                continue;
            }

            builder.Append(character);
        }
        return builder.ToString();
    }

    private static bool TryLiteralLength(string json, int index, out int length)
    {
        length = 0;
        var remaining = json.AsSpan(index);
        if (remaining.StartsWith("-Infinity")) length = 9;
        else if (remaining.StartsWith("Infinity")) length = 8;
        else if (remaining.StartsWith("NaN")) length = 3;
        else return false;

        var next = index + length;
        if (next < json.Length && (char.IsLetterOrDigit(json[next]) || json[next] == '_')) return false;
        return true;
    }
}

/// <summary>
/// The <c>JavaScriptSerializer</c> DateTime format. Reading accepts the escaped
/// form Windows writes (<c>\/Date(ms)\/</c>), the unescaped form, a bare epoch
/// millisecond number and ISO 8601; writing always emits the Windows form so a
/// Windows installation keeps reading our files.
/// </summary>
internal sealed class JavaScriptSerializerDateConverter : JsonConverter<DateTime>
{
    private const string DatePrefix = "Date(";
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return FromUnixMilliseconds(reader.GetInt64());
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Unsupported DateTime token: " + reader.TokenType);

        var text = (reader.GetString() ?? string.Empty).Trim();
        if (TryParseMsDate(text, out var value)) return value;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            return FromUnixMilliseconds(milliseconds);
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
            return value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        throw new JsonException("Unsupported DateTime text: " + text);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        // WriteRawValue keeps the JSON escape "\/" JavaScriptSerializer emits;
        // WriteStringValue would escape the backslash instead.
        var milliseconds = ToUnixMilliseconds(value);
        writer.WriteRawValue("\"\\/Date(" + milliseconds.ToString(CultureInfo.InvariantCulture) + ")\\/\"",
            skipInputValidation: true);
    }

    /// <summary>Milliseconds since the epoch, treating an unspecified kind as local.</summary>
    internal static long ToUnixMilliseconds(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
        };
        return (long)(utc - UnixEpoch).TotalMilliseconds;
    }

    internal static DateTime FromUnixMilliseconds(long milliseconds) =>
        UnixEpoch.AddMilliseconds(milliseconds).ToLocalTime();

    /// <summary>Parses <c>/Date(ms)/</c>, optionally with the JSON escaped slashes.</summary>
    internal static bool TryParseMsDate(string text, out DateTime value)
    {
        value = default;
        var trimmed = text.Replace("\\/", "/", StringComparison.Ordinal).Trim();
        if (!trimmed.StartsWith('/') || !trimmed.EndsWith('/')) return false;
        trimmed = trimmed[1..^1];
        if (!trimmed.StartsWith(DatePrefix, StringComparison.Ordinal) || !trimmed.EndsWith(')')) return false;
        var number = trimmed[DatePrefix.Length..^1];
        // Some serializers append a timezone offset, e.g. Date(1234+0200).
        var plus = number.IndexOfAny(['+', '-'], 1);
        if (plus > 0) number = number[..plus];
        if (!long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)) return false;
        value = FromUnixMilliseconds(milliseconds);
        return true;
    }
}

/// <summary>Writes doubles the way JavaScriptSerializer does, including NaN and Infinity.</summary>
internal sealed class MonitorNonFiniteDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.Null => double.NaN,
            JsonTokenType.String when double.TryParse(reader.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new JsonException("Unsupported double token: " + reader.TokenType)
        };

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsNaN(value)) writer.WriteRawValue("NaN", skipInputValidation: true);
        else if (double.IsPositiveInfinity(value)) writer.WriteRawValue("Infinity", skipInputValidation: true);
        else if (double.IsNegativeInfinity(value)) writer.WriteRawValue("-Infinity", skipInputValidation: true);
        else writer.WriteNumberValue(value);
    }
}
