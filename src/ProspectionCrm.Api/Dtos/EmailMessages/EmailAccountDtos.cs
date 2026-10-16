using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.EmailMessages;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateEmailAccountRequest(Guid ConnectionId, string ProviderCode, string ExternalAccountId, string EmailAddress, string? DisplayName = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEmailAccountRequest(string EmailAddress, string? DisplayName, string StatusCode,
    [property: JsonRequired] bool Archived, [property: JsonRequired] long Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnqueueEmailSyncRequest(int MaxItems = 100);
public sealed record EmailAccountDto(Guid Id, Guid ConnectionId, string ProviderCode, string ExternalAccountId, string EmailAddress,
    string? DisplayName, string StatusCode, long Revision, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? ArchivedAt);
public sealed record EmailSyncJobDto(Guid Id, Guid EmailAccountId, Guid IntegrationOperationId, string TriggerTypeCode, string StatusCode,
    DateTimeOffset EnqueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? ErrorCode,
    string? ConflictingExternalMessageId, int CreatedCount, int ExistingCount);
