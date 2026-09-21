using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace LittleTools.Assistant.Stock;

/// <summary>
/// Serialization for the stock settings file. Windows used
/// <c>JavaScriptSerializer</c>, which has two quirks the shared file depends on:
/// it writes raw UTF-8 (no \uXXXX escapes) and it writes non finite doubles as
/// bare <c>NaN</c>/<c>Infinity</c> literals, which are not valid JSON. Reading
/// therefore masks those literals and writing reproduces them.
/// </summary>
internal static class StockJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        // Windows files round trip through "NaN" strings as well, so accept both.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new NonFiniteDoubleConverter() }
    };

    public static string Serialize(StockSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static StockSettings? Deserialize(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<StockSettings>(MaskNonFiniteLiterals(json), Options);

    /// <summary>
    /// Replaces the bare <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> values
    /// JavaScriptSerializer emits with <c>null</c>, which System.Text.Json accepts
    /// for a double that is then repaired by the store's normalization.
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

        // Only a value position counts: the token must end at a delimiter.
        var next = index + length;
        if (next < json.Length && (char.IsLetterOrDigit(json[next]) || json[next] == '_')) return false;
        return true;
    }
}

/// <summary>Writes doubles the way JavaScriptSerializer does, including NaN and Infinity.</summary>
internal sealed class NonFiniteDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.Null => double.NaN,
            JsonTokenType.String when double.TryParse(reader.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
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
