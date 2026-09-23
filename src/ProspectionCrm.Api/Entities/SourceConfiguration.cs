namespace ProspectionCrm.Api.Entities;

public class SourceConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string SourceTypeCode { get; set; }
    public string? BaseUrl { get; set; }
    public string? ConfigurationJson { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public ICollection<SavedSearch> SavedSearches { get; set; } = new List<SavedSearch>();
    public ICollection<SourceExecution> Executions { get; set; } = new List<SourceExecution>();
    public ICollection<OpportunitySource> OpportunitySources { get; set; } = new List<OpportunitySource>();
}
