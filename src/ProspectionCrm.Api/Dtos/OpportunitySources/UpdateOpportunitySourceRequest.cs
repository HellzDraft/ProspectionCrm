using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.OpportunitySources;

public class UpdateOpportunitySourceRequest
{
    public Guid? SourceConfigurationId { get; set; }

    public Guid? SavedSearchId { get; set; }

    public Guid? SourceExecutionId { get; set; }

    [Required]
    [MaxLength(200)]
    public string SourceLabel { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? SourceUrl { get; set; }

    [MaxLength(500)]
    public string? ExternalId { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
}
