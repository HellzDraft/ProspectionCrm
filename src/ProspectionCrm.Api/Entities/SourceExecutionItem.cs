namespace ProspectionCrm.Api.Entities;

public class SourceExecutionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid SourceExecutionId { get; set; }
    public int ItemIndex { get; set; }
    public Guid? OpportunityId { get; set; }
    public Guid? OpportunityIdSnapshot { get; set; }
    public required string OutcomeCode { get; set; }
    public required string DecisionCode { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public required string Title { get; set; }
    public required string NormalizedTitle { get; set; }
    public string? CompanyName { get; set; }
    public string? NormalizedCompanyName { get; set; }
    public string? ExternalId { get; set; }
    public string? SourceUrl { get; set; }
    public string? NormalizedSourceUrl { get; set; }
    public string? PayloadSnapshotJson { get; set; }
    public string? DecisionDetailsJson { get; set; }

    public SourceExecution SourceExecution { get; set; } = null!;
    public Opportunity? Opportunity { get; set; }
    public ICollection<SourceExecutionItemSource> Sources { get; set; } = [];
}
