using ProspectionCrm.Api.Services.Automation;

namespace ProspectionCrm.Api.Entities;

public sealed class AutomationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid? AutomationRuleId { get; set; }
    public required string TriggerTypeCode { get; set; }
    public string? TriggerKey { get; set; }
    public required string ActionCategoryCode { get; set; }
    public string StatusCode { get; set; } = AutomationJobStatuses.Pending;
    public int Priority { get; set; } = AutomationJobLimits.DefaultPriority;
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int AttemptCount { get; set; }
    public string? ContextJson { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public AutomationRule? AutomationRule { get; set; }
}
