using ProspectionCrm.Api.Dtos.CampaignOpportunities;

namespace ProspectionCrm.Api.Dtos.Campaigns;

public class CampaignDto
{
    public Guid Id { get; set; }
    public Guid? PipelineId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string StatusCode { get; set; } = string.Empty;
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public IReadOnlyList<CampaignOpportunityDto> Opportunities { get; set; } = [];
}
