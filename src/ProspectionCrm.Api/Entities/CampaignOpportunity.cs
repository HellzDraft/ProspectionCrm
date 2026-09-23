namespace ProspectionCrm.Api.Entities;

public class CampaignOpportunity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid OpportunityId { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public Campaign Campaign { get; set; } = null!;
    public Opportunity Opportunity { get; set; } = null!;
}
