using System.Text.Json;
using ProspectionCrm.Api.Entities;

namespace ProspectionCrm.Api.Services.Automation;

public sealed record AutomationEvaluationResult(bool IsValid, bool IsMatched, bool? ConditionMatched,
    string ReasonCode, string TriggerTypeCode, string ActionTypeCode, string? ActionCategoryCode,
    AutomationSafetyDecision? SafetyDecision = null, CreateCrmTaskPlan? ActionPlan = null);

public interface IAutomationRuleEvaluator
{
    AutomationEvaluationResult Evaluate(AutomationJob job, AutomationRule rule, AutomationRuntimeSettings settings);
}

// Pure diagnostic: no database, clock, lease handling, history or business mutation.
public sealed class AutomationRuleEvaluator(IAutomationSafetyPolicy safetyPolicy) : IAutomationRuleEvaluator
{
    public AutomationEvaluationResult Evaluate(AutomationJob job, AutomationRule rule, AutomationRuntimeSettings settings)
    {
        var action = AutomationActionCatalog.Find(rule.ActionTypeCode);
        AutomationEvaluationResult Reject(string reason, bool valid = false, bool? condition = null) =>
            new(valid, false, condition, reason, job.TriggerTypeCode, rule.ActionTypeCode, action?.CategoryCode);
        if (job.WorkspaceId != rule.WorkspaceId || job.WorkspaceId != settings.WorkspaceId)
            return Reject(AutomationEvaluationReasons.WorkspaceMismatch);
        if (job.AutomationRuleId is null) return Reject(AutomationEvaluationReasons.RuleRequired);
        if (job.AutomationRuleId != rule.Id) return Reject(AutomationEvaluationReasons.RuleMismatch);
        if (!AutomationJobTriggers.IsValid(rule.TriggerTypeCode)) return Reject(AutomationEvaluationReasons.UnsupportedTrigger);
        if (job.TriggerTypeCode != rule.TriggerTypeCode) return Reject(AutomationEvaluationReasons.TriggerMismatch);
        if (action is null) return Reject(AutomationEvaluationReasons.UnsupportedAction);
        if (job.ActionCategoryCode != action.CategoryCode) return Reject(AutomationEvaluationReasons.CategoryMismatch);
        if (!AutomationEventContext.TryParse(job.ContextJson, out var context)) return Reject(AutomationEvaluationReasons.InvalidContext);
        var config = action.ParseConfiguration(rule.ActionConfigurationJson);
        if (config is null) return Reject(AutomationEvaluationReasons.InvalidConfiguration);
        if (!AutomationConditions.TryParse(rule.ConditionJson, out var condition)) return Reject(AutomationEvaluationReasons.InvalidCondition);
        if (rule.ArchivedAt is not null) return Reject(AutomationEvaluationReasons.RuleArchived, true);
        if (!rule.Enabled) return Reject(AutomationEvaluationReasons.RuleDisabled, true);
        if (rule.PipelineId is not null && rule.PipelineId != context.PipelineId) return Reject(AutomationEvaluationReasons.PipelineMismatch, true);
        if (!condition.Matches(context.Payload)) return Reject(AutomationEvaluationReasons.ConditionNotMatched, true, false);
        if (!context.Payload.TryGetProperty("opportunityId", out var target) || target.ValueKind != JsonValueKind.String
            || !target.TryGetGuid(out var opportunityId) || opportunityId == Guid.Empty)
            return Reject(AutomationEvaluationReasons.InvalidOpportunity, condition: true);

        var safety = safetyPolicy.Evaluate(settings, action.CategoryCode);
        if (safety.DecisionCode == AutomationDecisions.Blocked)
            return new(true, true, true, safety.ReasonCode, job.TriggerTypeCode, action.TypeCode, action.CategoryCode, safety);
        var reason = safety.DecisionCode switch
        {
            AutomationDecisions.Manual => AutomationEvaluationReasons.EligibleManual,
            AutomationDecisions.ApprovalRequired => AutomationEvaluationReasons.EligibleApproval,
            _ => AutomationEvaluationReasons.EligibleAutomatic
        };
        return new(true, true, true, reason, job.TriggerTypeCode, action.TypeCode, action.CategoryCode, safety,
            new(opportunityId, config.Title, config.Description, config.DueInDays));
    }
}
