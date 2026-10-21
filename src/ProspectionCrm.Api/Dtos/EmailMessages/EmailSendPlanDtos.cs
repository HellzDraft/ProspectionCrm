using System.Text.Json.Serialization;

namespace ProspectionCrm.Api.Dtos.EmailMessages;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EmailSendContent(Guid EmailAccountId, string[] ToAddresses, string[]? CcAddresses,
    string? Subject, string BodyText, Guid? OpportunityId = null, Guid? CompanyId = null, Guid? ContactId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateEmailSendPlanRequest([property: JsonRequired] long Revision, EmailSendContent Content);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EmailSendPlanRevision([property: JsonRequired] long Revision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EmailSendSnapshot(int Version, Guid PlanId, Guid WorkspaceId, Guid EmailAccountId,
    Guid ConnectionId, long Revision, string FromAddress, string[] ToAddresses, string[] CcAddresses,
    string? Subject, string BodyText, Guid? OpportunityId, Guid? CompanyId, Guid? ContactId);

public sealed record EmailSendPlanDto(Guid Id, string StatusCode, long Revision, long? SubmittedRevision,
    Guid? AutomationActionRequestId, string ContentHash, EmailSendSnapshot Content,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt, DateTimeOffset? RejectedAt, DateTimeOffset? CancelledAt);
