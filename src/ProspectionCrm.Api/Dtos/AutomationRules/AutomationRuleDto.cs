namespace ProspectionCrm.Api.Dtos.AutomationRules;

public class AutomationRuleDto
{
    public Guid Id { get; set; }
    public Guid? PipelineId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TriggerTypeCode { get; set; } = string.Empty;
    public string? ConditionJson { get; set; }
    public string ActionTypeCode { get; set; } = string.Empty;
    public string? ActionConfigurationJson { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
