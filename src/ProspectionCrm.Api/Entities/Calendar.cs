namespace ProspectionCrm.Api.Entities;
public sealed class Calendar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid CalendarAccountId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ProviderCode { get; set; }
    public required string ExternalCalendarId { get; set; }
    public string? DisplayName { get; set; }
    public string? TimeZoneId { get; set; }
    public string StatusCode { get; set; } = "active";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
