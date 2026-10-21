namespace ProspectionCrm.Api.Entities;

// An authorization snapshot, never evidence that an email was sent.
public sealed class EmailSendPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid EmailAccountId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ContactId { get; set; }
    public string StatusCode { get; set; } = "draft";
    public required string FromAddressSnapshot { get; set; }
    public required string ToAddressesJson { get; set; }
    public required string CcAddressesJson { get; set; }
    public string? Subject { get; set; }
    public required string BodyText { get; set; }
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
