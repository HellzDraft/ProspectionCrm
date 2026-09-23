namespace ProspectionCrm.Api.Entities;

public class SavedSearch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid PipelineId { get; set; }
    public Guid SourceConfigurationId { get; set; }
    public required string Name { get; set; }
    public string? SearchUrl { get; set; }
    public string CriteriaJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Pipeline Pipeline { get; set; } = null!;
    public SourceConfiguration SourceConfiguration { get; set; } = null!;
    public ICollection<SourceExecution> Executions { get; set; } = new List<SourceExecution>();
    public ICollection<OpportunitySource> OpportunitySources { get; set; } = new List<OpportunitySource>();
}
