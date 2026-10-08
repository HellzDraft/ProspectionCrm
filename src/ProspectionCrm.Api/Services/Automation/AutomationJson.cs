using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ProspectionCrm.Api.Services.Automation;

// Shared strict parsing for definitions and event envelopes, independent of EF and HTTP.
internal static class AutomationJson
{
    internal static bool TryObject(string? json, int maxBytes, out JsonElement root)
    {
        root = default;
        if (json is null || !SafeText(json) || Encoding.UTF8.GetByteCount(json) > maxBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !SafeElement(document.RootElement)) return false;
            root = document.RootElement.Clone(); return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException) { return false; }
    }

    internal static bool HasOnly(JsonElement root, params string[] names)
        => root.EnumerateObject().All(p => names.Contains(p.Name, StringComparer.Ordinal));

    internal static bool SafeText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\0') return false;
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    private static bool SafeElement(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            return value.EnumerateObject().All(p => names.Add(p.Name) && SafeText(p.Name) && SafeElement(p.Value));
        }
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().All(SafeElement);
        if (value.ValueKind == JsonValueKind.String) return SafeText(value.GetString()!);
        return value.ValueKind != JsonValueKind.Number || TryNumber(value, out _);
    }

    internal readonly record struct Number(string Digits, int Exponent, bool Negative)
    {
        internal string Plain()
        {
            var sign = Negative ? "-" : "";
            if (Exponent >= 0) return sign + Digits + new string('0', Exponent);
            var point = Digits.Length + Exponent;
            return point > 0 ? sign + Digits[..point] + "." + Digits[point..]
                : sign + "0." + new string('0', -point) + Digits;
        }
    }

    // Exact decimal value, not double/decimal rounding. Bound expansion before allocating it;
    // the bound also keeps accepted numbers within PostgreSQL jsonb's numeric range.
    internal static bool TryNumber(JsonElement value, out Number number)
    {
        number = default;
        var raw = value.GetRawText(); var negative = raw[0] == '-';
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        if (negative) mantissa = mantissa[1..];
        var point = mantissa.IndexOf('.'); var fraction = point < 0 ? 0 : mantissa.Length - point - 1;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) { number = new("0", 0, false); return true; }
        var trimmed = digits.TrimEnd('0');
        var exponent = (exponentIndex < 0 ? BigInteger.Zero : BigInteger.Parse(raw[(exponentIndex + 1)..], CultureInfo.InvariantCulture))
            - fraction + digits.Length - trimmed.Length;
        var limit = AutomationJobLimits.ContextBytes;
        if (BigInteger.Abs(exponent) > limit) return false;
        var exp = (int)exponent;
        var plainLength = (negative ? 1 : 0) + (exp >= 0 ? trimmed.Length + exp : Math.Max(1, trimmed.Length + exp) + 1 - exp);
        if (plainLength > limit) return false;
        number = new(trimmed, exp, negative); return true;
    }

    internal static string Serialize(Action<Utf8JsonWriter> write, int maxBytes)
    {
        using var stream = new MemoryStream();
        // This is JSON storage, never an HTML/script fragment. HTTP output still uses MVC's encoder.
        using var writer = new Utf8JsonWriter(stream, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        write(writer); CheckSize(writer, maxBytes); writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void Write(Utf8JsonWriter writer, JsonElement value, int maxBytes)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(writer, property.Value, maxBytes); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item, maxBytes);
                writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                if (!TryNumber(value, out var number)) throw new JsonException("Number exceeds the supported size.");
                writer.WriteRawValue(number.Plain()); break;
            default: value.WriteTo(writer); break;
        }
        CheckSize(writer, maxBytes);
    }

    private static void CheckSize(Utf8JsonWriter writer, int maxBytes)
    {
        if (writer.BytesCommitted + writer.BytesPending > maxBytes) throw new JsonException("JSON exceeds the supported size.");
    }

    internal static string? NormalizeDefinition(string? json)
    {
        if (json is null) return null;
        if (!TryObject(json, AutomationDefinitionLimits.RuleJsonBytes, out var root)) throw new InvalidOperationException("Validate the definition before normalization.");
        return Serialize(w => Write(w, root, AutomationDefinitionLimits.RuleJsonBytes), AutomationDefinitionLimits.RuleJsonBytes);
    }
}
