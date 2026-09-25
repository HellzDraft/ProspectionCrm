using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.AutomationRules;

public class UpdateAutomationRuleRequest
{
    public Guid? PipelineId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [Required]
    [MaxLength(50)]
    public string TriggerTypeCode { get; set; } = string.Empty;

    public string? ConditionJson { get; set; }

    [Required]
    [MaxLength(50)]
    public string ActionTypeCode { get; set; } = string.Empty;

    public string? ActionConfigurationJson { get; set; }

    public bool Enabled { get; set; } = true;
}
