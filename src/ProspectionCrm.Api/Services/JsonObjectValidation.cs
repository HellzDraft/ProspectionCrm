using System.Text.Json;

namespace ProspectionCrm.Api.Services;

internal static class JsonObjectValidation
{
    internal static string? Validate(string? value, string field, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return required ? $"{field} must contain a JSON object." : null;

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? null : $"{field} must contain a JSON object.";
        }
        catch (JsonException)
        {
            return $"{field} must contain valid JSON.";
        }
    }

    internal static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
