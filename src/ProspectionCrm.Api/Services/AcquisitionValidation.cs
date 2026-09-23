using System.Text.Json;

namespace ProspectionCrm.Api.Services;

internal static class AcquisitionValidation
{
    internal static string? ValidateJsonObject(string? value, string field, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
            return required ? $"{field} must contain a JSON object." : null;

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? null
                : $"{field} must contain a JSON object.";
        }
        catch (JsonException)
        {
            return $"{field} must contain valid JSON.";
        }
    }
}
