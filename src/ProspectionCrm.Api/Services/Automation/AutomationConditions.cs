using System.Text.Json;

namespace ProspectionCrm.Api.Services.Automation;

public sealed record AutomationCondition(string Type, string? Property = null, JsonElement Value = default)
{
    public bool Matches(JsonElement payload)
    {
        if (Type == AutomationConditions.Always) return true;
        if (Type != AutomationConditions.ContextEquals || payload.ValueKind != JsonValueKind.Object
            || Property is null || !payload.TryGetProperty(Property, out var actual) || actual.ValueKind != Value.ValueKind) return false;
        return Value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False => true,
            JsonValueKind.String => string.Equals(actual.GetString(), Value.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => AutomationJson.TryNumber(actual, out var a) && AutomationJson.TryNumber(Value, out var b) && a == b,
            _ => false
        };
    }
}

public static class AutomationConditions
{
    public const string Always = "always", ContextEquals = "context-equals";
    public static bool TryParse(string? json, out AutomationCondition condition)
    {
        condition = new(Always);
        if (json is null) return true;
        if (!AutomationJson.TryObject(json, AutomationDefinitionLimits.RuleJsonBytes, out var root)) return false;
        if (!root.EnumerateObject().Any()) return true;
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
        if (type.GetString() == Always) return AutomationJson.HasOnly(root, "type");
        if (type.GetString() != ContextEquals || !AutomationJson.HasOnly(root, "type", "property", "value")
            || !root.TryGetProperty("property", out var property) || property.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("value", out var value)
            || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) return false;
        var name = property.GetString()!;
        if (string.IsNullOrWhiteSpace(name) || name.Length > AutomationDefinitionLimits.ConditionPropertyLength
            || name.Any(char.IsControl) || name.IndexOfAny(['.', '[', ']']) >= 0) return false;
        condition = new(ContextEquals, name, value.Clone()); return true;
    }
}
