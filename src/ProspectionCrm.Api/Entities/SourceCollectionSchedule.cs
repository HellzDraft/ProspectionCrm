namespace ProspectionCrm.Api.Entities;

public sealed class SourceCollectionSchedule
{
    public Guid SavedSearchId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PipelineId { get; set; }
    public Guid PipelineStageId { get; set; }
    public bool Enabled { get; set; }
    public int DailyUtcMinute { get; set; }
    public DateTimeOffset? NextCollectionAt { get; set; }
    public SavedSearch SavedSearch { get; set; } = null!;
}
