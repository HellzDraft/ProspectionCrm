using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.ScoringRules;

public class CreateScoringRuleRequest
{
    public Guid? PipelineId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [Required]
    [MaxLength(50)]
    public string RuleTypeCode { get; set; } = string.Empty;

    [Range(typeof(decimal), "-100", "100")]
    public decimal Weight { get; set; }

    [Required]
    public string ConfigurationJson { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
