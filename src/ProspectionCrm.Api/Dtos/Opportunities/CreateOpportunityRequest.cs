using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Opportunities;

public class CreateOpportunityRequest
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public Guid? CompanyId { get; set; }

    public Guid? ContactId { get; set; }

    [Required]
    [MaxLength(50)]
    public string PipelineCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string StatusCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string PriorityCode { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(200)]
    public string? SourceName { get; set; }

    [MaxLength(2048)]
    public string? SourceUrl { get; set; }

    [MaxLength(10000)]
    public string? Notes { get; set; }

    public DateTimeOffset? FollowUpDueAt { get; set; }
}
