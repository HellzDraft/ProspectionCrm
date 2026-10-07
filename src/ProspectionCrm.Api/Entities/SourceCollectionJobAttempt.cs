namespace ProspectionCrm.Api.Entities;

// Processing metadata only. Business inputs, observations and counters remain in SourceExecution.
public sealed class SourceCollectionJobAttempt
{
    public Guid JobId { get; set; }
    public Guid WorkspaceId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string StatusCode { get; set; } = "running";
    public string? ErrorCode { get; set; }
    public int? UpstreamStatusCode { get; set; }
    public Guid? SourceExecutionId { get; set; }
    public SourceCollectionJob Job { get; set; } = null!;
    public SourceExecution? SourceExecution { get; set; }
}
