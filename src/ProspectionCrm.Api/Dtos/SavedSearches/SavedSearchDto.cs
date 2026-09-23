namespace ProspectionCrm.Api.Dtos.SavedSearches;

public class SavedSearchDto
{
    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }
    public Guid SourceConfigurationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SearchUrl { get; set; }
    public string CriteriaJson { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
