namespace ProspectionCrm.Api.Services.Automation;

public static class AutomationDefinitionLimits
{
    // Matches the existing CrmTask model; no schema change is needed.
    public const int TaskTitleLength = 200, TaskDescriptionLength = 10000,
        EventKeyLength = 100, ConditionPropertyLength = 100, RuleJsonBytes = 65536;
}

public static class AutomationEvaluationReasons
{
    public const string WorkspaceMismatch = "workspace-mismatch", RuleRequired = "automation-rule-required",
        RuleMismatch = "rule-mismatch", RuleDisabled = "rule-disabled", RuleArchived = "rule-archived",
        UnsupportedTrigger = "unsupported-trigger", TriggerMismatch = "trigger-mismatch",
        UnsupportedAction = "unsupported-action", CategoryMismatch = "action-category-mismatch",
        PipelineMismatch = "pipeline-mismatch", InvalidContext = "invalid-context",
        InvalidCondition = "invalid-condition", ConditionNotMatched = "condition-not-matched",
        InvalidConfiguration = "invalid-action-configuration", InvalidOpportunity = "invalid-opportunity-id",
        EligibleManual = "eligible-manual", EligibleApproval = "eligible-approval-required", EligibleAutomatic = "eligible-automatic",
        InvalidEventKey = "invalid-event-key", InvalidPayload = "invalid-payload", InvalidPipeline = "invalid-pipeline",
        PipelineNotFound = "pipeline-not-found", DispatchKeyConflict = "dispatch-key-conflict",
        ConcurrentDispatchChange = "concurrent-dispatch-change";
}
