using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.CalendarEvents;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCalendarAccountRequest(Guid ConnectionId, string ProviderCode, string ExternalAccountId, string? DisplayName);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateCalendarAccountRequest([property: JsonRequired] long Revision, string? DisplayName, string StatusCode, [property: JsonRequired] bool Archived);
public sealed record CalendarAccountDto(Guid Id, Guid ConnectionId, string ProviderCode, string ExternalAccountId,
    string? DisplayName, string StatusCode, long Revision, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? ArchivedAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateCalendarRequest(string ExternalCalendarId, string? DisplayName, string? TimeZoneId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateCalendarRequest([property: JsonRequired] long Revision, string? DisplayName, string? TimeZoneId, string StatusCode, [property: JsonRequired] bool Archived);
public sealed record CalendarDto(Guid Id, Guid CalendarAccountId, string ExternalCalendarId, string? DisplayName, string? TimeZoneId,
    string StatusCode, long Revision, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? ArchivedAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnqueueCalendarSyncRequest(int MaxItems = 100);
public sealed record CalendarSyncJobDto(Guid Id, Guid CalendarId, Guid IntegrationOperationId, string TriggerTypeCode, string StatusCode,
    DateTimeOffset EnqueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? ErrorCode,
    string? ConflictingExternalEventId, int CreatedCount, int ExistingCount, int UpdatedCount);
