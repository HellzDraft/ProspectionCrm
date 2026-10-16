namespace ProspectionCrm.Api.Entities;

public sealed class EmailSyncState
{
    public Guid EmailAccountId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string? Cursor { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; }
}
