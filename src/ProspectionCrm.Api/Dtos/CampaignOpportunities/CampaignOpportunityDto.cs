namespace ProspectionCrm.Api.Dtos.CampaignOpportunities;

public class CampaignOpportunityDto
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Guid OpportunityId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}
