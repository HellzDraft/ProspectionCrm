namespace ProspectionCrm.Api.Entities;

public class AutomationExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid AutomationRuleId { get; set; }
    public Guid? AutomationJobId { get; set; }
    public int? AttemptNumber { get; set; }
    public string? ActionTypeCode { get; set; }
    public string? ReasonCode { get; set; }
    public bool IsAutomaticAttempt { get; set; }
    public bool IsHumanApprovedAttempt { get; set; }
    public Guid? AutomationActionRequestId { get; set; }
    public AutomationActionRequest? AutomationActionRequest { get; set; }
    public bool IsDeferred { get; set; }
    public bool EffectApplied { get; set; }
    // Assigned under the workspace automation lock; timestamps alone cannot order concurrent outcomes.
    public long? OutcomeSequence { get; set; }
    public required string StatusCode { get; set; }
    public DateTimeOffset TriggeredAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ContextJson { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public AutomationRule AutomationRule { get; set; } = null!;
    public AutomationJob? AutomationJob { get; set; }
}
