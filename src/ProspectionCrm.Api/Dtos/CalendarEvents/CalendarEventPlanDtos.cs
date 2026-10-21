using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.CalendarEvents;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalendarEventContent(Guid CalendarId, string OperationCode, Guid? TargetCalendarEventId,
    string Title, string? Description, string? Location, string StartsAt, string EndsAt,
    [property: JsonRequired] bool IsAllDay, string? TimeZoneId,
    Guid? OpportunityId = null, Guid? CompanyId = null, Guid? ContactId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateCalendarEventPlanRequest([property: JsonRequired] long Revision, CalendarEventContent Content);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalendarEventPlanRevision([property: JsonRequired] long Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalendarEventTarget(Guid Id, string ProviderCode, string ExternalEventId, long Revision,
    string ContentHash, string Title, string? Description, string? Location, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, bool IsAllDay, string? TimeZoneId, string StatusCode,
    string? SeriesId, string? OccurrenceId, Guid? OpportunityId, Guid? ContactId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CalendarEventSnapshot(int Version, Guid PlanId, Guid WorkspaceId, Guid CalendarAccountId,
    Guid ConnectionId, long Revision, CalendarEventContent Content, CalendarEventTarget? Target);
public sealed record CalendarEventPlanDto(Guid Id, string StatusCode, long Revision, long? SubmittedRevision,
    Guid? AutomationActionRequestId, string ContentHash, CalendarEventSnapshot Content,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt, DateTimeOffset? RejectedAt, DateTimeOffset? CancelledAt);
