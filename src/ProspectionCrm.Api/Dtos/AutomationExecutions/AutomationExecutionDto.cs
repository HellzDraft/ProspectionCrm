namespace ProspectionCrm.Api.Dtos.AutomationExecutions;

public class AutomationExecutionDto
{
    public Guid Id { get; set; }
    public Guid AutomationRuleId { get; set; }
    public Guid? AutomationJobId { get; set; }
    public Guid? AutomationActionRequestId { get; set; }
    public bool IsAutomaticAttempt { get; set; }
    public bool IsHumanApprovedAttempt { get; set; }
    public int? AttemptNumber { get; set; }
    public string? ActionTypeCode { get; set; }
    public string? ReasonCode { get; set; }
    public bool EffectApplied { get; set; }
    public bool IsDeferred { get; set; }
    public string StatusCode { get; set; } = string.Empty;
    public DateTimeOffset TriggeredAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ContextJson { get; set; }
}
