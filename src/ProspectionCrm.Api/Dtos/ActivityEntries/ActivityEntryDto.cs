namespace ProspectionCrm.Api.Dtos.ActivityEntries;

public class ActivityEntryDto
{
    public Guid Id { get; set; }
    public Guid? RelatedOpportunityId { get; set; }
    public string EntityTypeCode { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string EventTypeCode { get; set; } = string.Empty;
    public string ActorTypeCode { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public string? Summary { get; set; }
    public string? MetadataJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
