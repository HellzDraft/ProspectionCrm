using System.Text.Json;

namespace ProspectionCrm.Api.Services;

internal static class HistoricalJsonReader
{
    internal static JsonElement? Read(string? json)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
