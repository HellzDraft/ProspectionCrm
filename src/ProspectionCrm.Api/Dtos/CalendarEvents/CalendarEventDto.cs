namespace ProspectionCrm.Api.Dtos.CalendarEvents;

public class CalendarEventDto
{
    public Guid Id { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? ContactId { get; set; }
    public string ProviderCode { get; set; } = string.Empty;
    public string? ExternalEventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public bool IsAllDay { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
