namespace ProspectionCrm.Api.Dtos.ScoringRules;

public class ScoringRuleDto
{
    public Guid Id { get; set; }
    public Guid? PipelineId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string RuleTypeCode { get; set; } = string.Empty;
    public decimal Weight { get; set; }
    public string ConfigurationJson { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
