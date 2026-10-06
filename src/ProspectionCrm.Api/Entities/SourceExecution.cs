namespace ProspectionCrm.Api.Entities;

public class SourceExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid SourceConfigurationId { get; set; }
    public Guid? SavedSearchId { get; set; }
    public required string TriggerTypeCode { get; set; }
    public required string StatusCode { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int ItemsFound { get; set; }
    public int ItemsCreated { get; set; }
    public int ItemsUpdated { get; set; }
    public int ItemsIgnored { get; set; }
    public string? ErrorMessage { get; set; }
    public int? HistoryVersion { get; set; }
    public int? ContractVersion { get; set; }
    public int? NormalizationVersion { get; set; }
    public Guid? TargetPipelineId { get; set; }
    public Guid? TargetPipelineStageId { get; set; }
    public string? ContextSnapshotJson { get; set; }
    public int ItemsRejected { get; set; }
    public int ItemsRolledBack { get; set; }
    public int ItemsNotProcessed { get; set; }
    public int ItemsCancelled { get; set; }
    public ICollection<SourceExecutionItem> Items { get; set; } = [];

    public Workspace Workspace { get; set; } = null!;
    public SourceConfiguration SourceConfiguration { get; set; } = null!;
    public SavedSearch? SavedSearch { get; set; }
    public ICollection<OpportunitySource> OpportunitySources { get; set; } = new List<OpportunitySource>();
}
