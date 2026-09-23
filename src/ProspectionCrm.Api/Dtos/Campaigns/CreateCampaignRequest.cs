using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Campaigns;

public class CreateCampaignRequest
{
    public Guid? PipelineId { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    [Required]
    [MaxLength(50)]
    [RegularExpression("^(draft|active|paused|completed|cancelled)$")]
    public string StatusCode { get; set; } = string.Empty;

    public DateTimeOffset? StartsAt { get; set; }

    public DateTimeOffset? EndsAt { get; set; }
}
