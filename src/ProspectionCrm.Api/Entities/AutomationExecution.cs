namespace ProspectionCrm.Api.Entities;

public class AutomationExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid AutomationRuleId { get; set; }
    public required string StatusCode { get; set; }
    public DateTimeOffset TriggeredAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ContextJson { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public AutomationRule AutomationRule { get; set; } = null!;
}
