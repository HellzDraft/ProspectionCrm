namespace ProspectionCrm.Api.Entities;

public class OpportunitySource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OpportunityId { get; set; }
    public Guid? SourceConfigurationId { get; set; }
    public Guid? SavedSearchId { get; set; }
    public Guid? SourceExecutionId { get; set; }
    public required string SourceLabel { get; set; }
    public string? SourceUrl { get; set; }
    public string? ExternalId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }

    public Opportunity Opportunity { get; set; } = null!;
    public SourceConfiguration? SourceConfiguration { get; set; }
    public SavedSearch? SavedSearch { get; set; }
    public SourceExecution? SourceExecution { get; set; }
}
