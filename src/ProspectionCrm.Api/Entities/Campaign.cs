namespace ProspectionCrm.Api.Entities;

public class Campaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid? PipelineId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string StatusCode { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Pipeline? Pipeline { get; set; }
    public ICollection<CampaignOpportunity> CampaignOpportunities { get; set; } = new List<CampaignOpportunity>();
}
