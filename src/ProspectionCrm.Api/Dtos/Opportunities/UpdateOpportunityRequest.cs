using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Opportunities;

public class UpdateOpportunityRequest
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public Guid? CompanyId { get; set; }
    public Guid? ContactId { get; set; }

    [Required]
    public Guid? PipelineStageId { get; set; }

    [Required]
    [MaxLength(50)]
    [RegularExpression("^(low|normal|high)$")]
    public string PriorityCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(10000)]
    public string? Notes { get; set; }
}
