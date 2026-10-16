namespace ProspectionCrm.Api.Entities;

public sealed class EmailSyncJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid EmailAccountId { get; set; }
    public Guid ConnectionId { get; set; }
    public string TriggerTypeCode { get; set; } = "manual";
    public string StatusCode { get; set; } = "queued";
    public string? PreviousCursor { get; set; }
    public string? NextCursor { get; set; }
    public long StateRevision { get; set; }
    public long AccountRevision { get; set; }
    public int MaxItems { get; set; } = 100;
    public Guid IntegrationOperationId { get; set; }
    public string OperationType { get; set; } = "email.sync";
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ConflictingExternalMessageId { get; set; }
    public int CreatedCount { get; set; }
    public int ExistingCount { get; set; }
}
