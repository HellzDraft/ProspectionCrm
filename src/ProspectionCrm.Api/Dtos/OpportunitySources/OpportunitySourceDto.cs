namespace ProspectionCrm.Api.Dtos.OpportunitySources;

public class OpportunitySourceDto
{
    public Guid Id { get; set; }
    public Guid OpportunityId { get; set; }
    public Guid? SourceConfigurationId { get; set; }
    public Guid? SavedSearchId { get; set; }
    public Guid? SourceExecutionId { get; set; }
    public string SourceLabel { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string? ExternalId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}
