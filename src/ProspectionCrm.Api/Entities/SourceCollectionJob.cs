namespace ProspectionCrm.Api.Entities;

public class SourceCollectionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid SavedSearchId { get; set; }
    public Guid PipelineId { get; set; }
    public Guid PipelineStageId { get; set; }
    public required string TriggerTypeCode { get; set; }
    public required string StatusCode { get; set; }
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int AttemptCount { get; set; }
    public Guid? SourceExecutionId { get; set; }
    public string? ErrorCode { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public SavedSearch SavedSearch { get; set; } = null!;
    public Pipeline Pipeline { get; set; } = null!;
    public PipelineStage PipelineStage { get; set; } = null!;
    public SourceExecution? SourceExecution { get; set; }
}
