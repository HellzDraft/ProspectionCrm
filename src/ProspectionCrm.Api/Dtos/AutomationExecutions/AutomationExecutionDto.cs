namespace ProspectionCrm.Api.Dtos.AutomationExecutions;

public class AutomationExecutionDto
{
    public Guid Id { get; set; }
    public Guid AutomationRuleId { get; set; }
    public string StatusCode { get; set; } = string.Empty;
    public DateTimeOffset TriggeredAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ContextJson { get; set; }
}
