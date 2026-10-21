namespace ProspectionCrm.Api.Entities;

public class CalendarEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid? CalendarId { get; set; }
    public long? ExternalRevision { get; set; }
    public string? ExternalContentSha256 { get; set; }
    public string? StatusCode { get; set; }
    public string? SeriesId { get; set; }
    public string? OccurrenceId { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? ContactId { get; set; }
    public required string ProviderCode { get; set; }
    public string? ExternalEventId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public bool IsAllDay { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Opportunity? Opportunity { get; set; }
    public Contact? Contact { get; set; }
}
