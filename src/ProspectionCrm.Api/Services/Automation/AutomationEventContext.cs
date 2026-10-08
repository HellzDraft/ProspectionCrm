using System.Text.Json;

namespace ProspectionCrm.Api.Services.Automation;

public sealed record AutomationEventContext(string EventKey, Guid? PipelineId, JsonElement Payload)
{
    public static bool ValidKey(string? key) => !string.IsNullOrWhiteSpace(key)
        && key.Length <= AutomationDefinitionLimits.EventKeyLength && !key.Any(char.IsControl) && AutomationJson.SafeText(key);

    public static string? Create(string? eventKey, Guid? pipelineId, string? payloadJson, out string? error)
    {
        error = null;
        if (!ValidKey(eventKey)) { error = AutomationEvaluationReasons.InvalidEventKey; return null; }
        if (pipelineId == Guid.Empty) { error = AutomationEvaluationReasons.InvalidPipeline; return null; }
        if (!AutomationJson.TryObject(payloadJson ?? "{}", AutomationJobLimits.ContextBytes, out var payload))
        { error = AutomationEvaluationReasons.InvalidPayload; return null; }
        try
        {
            return AutomationJson.Serialize(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("eventKey", eventKey);
                if (pipelineId is { } id) writer.WriteString("pipelineId", id); else writer.WriteNull("pipelineId");
                writer.WritePropertyName("payload");
                AutomationJson.Write(writer, payload, AutomationJobLimits.ContextBytes);
                writer.WriteEndObject();
            }, AutomationJobLimits.ContextBytes);
        }
        catch (JsonException) { error = AutomationEvaluationReasons.InvalidContext; return null; }
    }

    public static bool TryParse(string? json, out AutomationEventContext context)
    {
        context = null!;
        if (!AutomationJson.TryObject(json, AutomationDefinitionLimits.RuleJsonBytes, out var root)
            || !AutomationJson.HasOnly(root, "eventKey", "pipelineId", "payload")
            || !root.TryGetProperty("eventKey", out var key) || key.ValueKind != JsonValueKind.String || !ValidKey(key.GetString())
            || !root.TryGetProperty("pipelineId", out var pipeline)
            || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return false;
        Guid? pipelineId = null;
        if (pipeline.ValueKind != JsonValueKind.Null)
        {
            if (pipeline.ValueKind != JsonValueKind.String || !pipeline.TryGetGuid(out var id) || id == Guid.Empty) return false;
            pipelineId = id;
        }
        context = new(key.GetString()!, pipelineId, payload.Clone());
        return true;
    }

    public static string TriggerKey(string eventKey, Guid ruleId) => $"event:{eventKey}:{ruleId:N}";
}
