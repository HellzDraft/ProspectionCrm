namespace ProspectionCrm.Api.Entities;

public class AutomationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid? PipelineId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string TriggerTypeCode { get; set; }
    public string? ConditionJson { get; set; }
    public required string ActionTypeCode { get; set; }
    public string? ActionConfigurationJson { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Pipeline? Pipeline { get; set; }
    public ICollection<AutomationExecution> Executions { get; set; } = [];
}
