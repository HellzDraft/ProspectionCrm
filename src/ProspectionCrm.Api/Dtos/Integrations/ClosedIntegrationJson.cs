using System.Text;
using System.Text.Json;

namespace ProspectionCrm.Api.Dtos.Integrations;

internal static class ClosedIntegrationJson
{
    internal static T Read<T>(string json, int max, Func<JsonElement, T> parse)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > max) throw new IntegrationContractException();
        try { using var doc = JsonDocument.Parse(json, new() { MaxDepth = 6 }); return parse(doc.RootElement); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or ArgumentException)
        { throw new IntegrationContractException(); }
    }
    internal static void Shape(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new IntegrationContractException();
        var actual = value.EnumerateObject().Select(x => x.Name).ToArray(); var names = actual.ToHashSet(StringComparer.Ordinal);
        if (names.Count != actual.Length || !required.All(names.Contains) || actual.Any(x => !required.Contains(x) && !(optional ?? []).Contains(x)))
            throw new IntegrationContractException();
    }
    internal static Guid Id(JsonElement value, string name)
    {
        if (!Guid.TryParseExact(value.GetProperty(name).GetString(), "D", out var id) || id == Guid.Empty) throw new IntegrationContractException();
        return id;
    }
    internal static string? Text(JsonElement value, string name, int max, bool nullable = false, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
        { if (nullable) return null; throw new IntegrationContractException(); }
        if (p.ValueKind != JsonValueKind.String) throw new IntegrationContractException();
        var text = p.GetString()!;
        if (text.Length > max || text.Contains('\0') || !allowEmpty && (string.IsNullOrWhiteSpace(text) || text != text.Trim()))
            throw new IntegrationContractException();
        return text;
    }
}
