namespace ProspectionCrm.Api.Entities;

// A manual authorization snapshot; decisions never mutate calendar events.
public sealed class CalendarEventPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid CalendarAccountId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ContactId { get; set; }
    public string StatusCode { get; set; } = "draft";
    public Guid CalendarId { get; set; }
    public required string OperationCode { get; set; }
    public Guid? TargetCalendarEventId { get; set; }
    public string? TargetExternalEventId { get; set; }
    public long? TargetExternalRevision { get; set; }
    public string? TargetSnapshotJson { get; set; }
    public required string ContentJson { get; set; }
    public required string ContentHash { get; set; }
    public long Revision { get; set; }
    public long? SubmittedRevision { get; set; }
    public Guid? AutomationActionRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}
