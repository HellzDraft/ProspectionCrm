namespace ProspectionCrm.Api.Entities;

// Immutable dispatch context. A job has at most one logical command across transport retries.
public sealed class RemoteCollectionIntent
{
    public Guid JobId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string OperationType { get; set; }
    public Guid SourceConfigurationId { get; set; }
    public required string Fingerprint { get; set; }
    public required string ContextSnapshotJson { get; set; }
    public required string CommandJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public SourceCollectionJob Job { get; set; } = null!;
}
