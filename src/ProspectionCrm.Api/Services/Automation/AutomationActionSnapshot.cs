using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

public static class AutomationActionSnapshot
{
    public static string? Fingerprint(AutomationRule rule)
    {
        try
        {
            var definition = JsonSerializer.Serialize(new { rule.TriggerTypeCode, rule.PipelineId,
                Condition = AutomationJson.NormalizeDefinition(rule.ConditionJson), rule.ActionTypeCode,
                Configuration = AutomationJson.NormalizeDefinition(rule.ActionConfigurationJson) });
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(definition)));
        }
        catch (Exception error) when (error is InvalidOperationException or JsonException) { return null; }
    }

    public static string Serialize(CreateCrmTaskPlan plan) => AutomationJson.Serialize(w =>
    {
        w.WriteStartObject(); w.WriteString("opportunityId", plan.OpportunityId); w.WriteString("title", plan.Title);
        w.WriteString("description", plan.Description);
        if (plan.DueInDays is { } days) w.WriteNumber("dueInDays", days); else w.WriteNull("dueInDays");
        w.WriteEndObject();
    }, ActionRequestCodes.PlanBytes);

    public static CreateCrmTaskPlan? Parse(string? json)
    {
        if (!AutomationJson.TryObject(json, ActionRequestCodes.PlanBytes, out var root)
            || !AutomationJson.HasOnly(root, "opportunityId", "title", "description", "dueInDays")
            || !root.TryGetProperty("opportunityId", out var id) || id.ValueKind != JsonValueKind.String
            || !id.TryGetGuid(out var opportunity) || opportunity == Guid.Empty) return null;
        // Reuse the action's literal-text validation, omitting the snapshot's nullable dueInDays.
        var config = AutomationJson.Serialize(w =>
        {
            w.WriteStartObject();
            foreach (var p in root.EnumerateObject())
                if (p.Name != "opportunityId" && !(p.Name == "dueInDays" && p.Value.ValueKind == JsonValueKind.Null)) p.WriteTo(w);
            w.WriteEndObject();
        }, ActionRequestCodes.PlanBytes);
        var parsed = AutomationActionCatalog.Find(AutomationActionCatalog.CreateCrmTask)!.ParseConfiguration(config);
        return parsed is null ? null : new(opportunity, parsed.Title, parsed.Description, parsed.DueInDays);
    }

    public static bool IsStale(AutomationActionRequest request, AutomationRule? rule) =>
        DefinitionChanged(request, rule) || Parse(request.ActionPlanJson) is null;

    private static bool DefinitionChanged(AutomationActionRequest request, AutomationRule? rule) => rule is null
        || rule.WorkspaceId != request.WorkspaceId || rule.Id != request.AutomationRuleId
        || Fingerprint(rule) is not { } fingerprint || fingerprint != request.RuleFingerprint;

    public static string? Validate(AutomationActionRequest request, AutomationJob job, AutomationRule? rule,
        out CreateCrmTaskPlan? plan)
    {
        plan = null;
        if (request.StatusCode != ActionRequestStatuses.Approved) return ActionRequestCodes.Unavailable;
        if (request.WorkspaceId != job.WorkspaceId || request.AutomationJobId != job.Id
            || request.AutomationRuleId != job.AutomationRuleId) return ActionRequestCodes.Mismatch;
        // Keep the existing execution reason for an invalid approved plan; reads also flag it stale.
        if (DefinitionChanged(request, rule)) return ActionRequestCodes.Stale;
        if (rule!.ArchivedAt is not null) return AutomationEvaluationReasons.RuleArchived;
        if (!rule.Enabled) return AutomationEvaluationReasons.RuleDisabled;
        if (request.ActionTypeCode != AutomationActionCatalog.CreateCrmTask || request.ActionTypeCode != rule.ActionTypeCode
            || request.ActionCategoryCode != AutomationCategories.General || request.ActionCategoryCode != job.ActionCategoryCode
            || !ActionRequestCodes.RequiresDecision(request.DecisionRequirementCode)) return ActionRequestCodes.Mismatch;
        plan = Parse(request.ActionPlanJson);
        return plan is null ? ActionRequestCodes.InvalidPlan : null;
    }
}
