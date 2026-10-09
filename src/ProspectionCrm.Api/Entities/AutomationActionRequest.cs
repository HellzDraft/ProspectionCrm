using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Entities;

public sealed class AutomationActionRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid AutomationJobId { get; set; }
    public Guid AutomationRuleId { get; set; }
    public required string DecisionRequirementCode { get; set; }
    public string StatusCode { get; set; } = ActionRequestStatuses.Pending;
    public required string ActionTypeCode { get; set; }
    public required string ActionCategoryCode { get; set; }
    public required string ActionPlanJson { get; set; }
    public required string RuleFingerprint { get; set; }
    public required string RequestedReasonCode { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public string? DecisionNote { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public AutomationJob AutomationJob { get; set; } = null!;
    public AutomationRule AutomationRule { get; set; } = null!;
    public UserAccount? DecidedByUser { get; set; }
}
