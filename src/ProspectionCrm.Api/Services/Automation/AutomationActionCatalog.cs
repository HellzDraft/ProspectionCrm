using System.Text.Json;

namespace ProspectionCrm.Api.Services.Automation;

public sealed record CreateCrmTaskConfiguration(string Title, string? Description, int? DueInDays);
public sealed record CreateCrmTaskPlan(Guid OpportunityId, string Title, string? Description, int? DueInDays)
{
    public string ActionTypeCode => AutomationActionCatalog.CreateCrmTask;
    public string ActionCategoryCode => AutomationCategories.General;
}

// V1 deliberately has a single typed configuration and plan, without a generic execution framework.
public sealed record AutomationActionDefinition(string TypeCode, string CategoryCode, Type PlanType,
    Func<string?, CreateCrmTaskConfiguration?> ParseConfiguration);

public static class AutomationActionCatalog
{
    public const string CreateCrmTask = "create-crm-task";
    private static readonly AutomationActionDefinition TaskAction = new(CreateCrmTask, AutomationCategories.General,
        typeof(CreateCrmTaskPlan), ParseTaskConfiguration);
    public static AutomationActionDefinition? Find(string? code) => code == CreateCrmTask ? TaskAction : null;

    private static CreateCrmTaskConfiguration? ParseTaskConfiguration(string? json)
    {
        if (!AutomationJson.TryObject(json, AutomationDefinitionLimits.RuleJsonBytes, out var root)
            || !AutomationJson.HasOnly(root, "title", "description", "dueInDays")
            || !root.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String) return null;
        var text = title.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > AutomationDefinitionLimits.TaskTitleLength || HasTemplate(text)) return null;
        string? description = null; int? dueInDays = null;
        if (root.TryGetProperty("description", out var d))
        {
            if (d.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) return null;
            description = d.GetString();
            if (description is not null && (description.Length > AutomationDefinitionLimits.TaskDescriptionLength || HasTemplate(description))) return null;
        }
        if (root.TryGetProperty("dueInDays", out var days))
        {
            if (days.ValueKind != JsonValueKind.Number || !days.TryGetInt32(out var count) || count is < 0 or > 365) return null;
            dueInDays = count;
        }
        return new(text, description, dueInDays);
    }
    private static bool HasTemplate(string text) => text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal);
}

public static class AutomationRuleValidation
{
    public static string? Validate(string? trigger, string? action, string? conditionJson, string? configurationJson)
    {
        if (!AutomationJobTriggers.IsValid(trigger)) return AutomationEvaluationReasons.UnsupportedTrigger;
        var definition = AutomationActionCatalog.Find(action);
        if (definition is null) return AutomationEvaluationReasons.UnsupportedAction;
        if (!AutomationConditions.TryParse(conditionJson, out _)) return AutomationEvaluationReasons.InvalidCondition;
        if (definition.ParseConfiguration(configurationJson) is null) return AutomationEvaluationReasons.InvalidConfiguration;
        return null;
    }
}
