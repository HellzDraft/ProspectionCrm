namespace ProspectionCrm.Api.Entities;

public class ActivityEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid? RelatedOpportunityId { get; set; }
    public required string EntityTypeCode { get; set; }
    public Guid EntityId { get; set; }
    public required string EventTypeCode { get; set; }
    public required string ActorTypeCode { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Summary { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Opportunity? RelatedOpportunity { get; set; }
    public UserAccount? ActorUser { get; set; }
}
