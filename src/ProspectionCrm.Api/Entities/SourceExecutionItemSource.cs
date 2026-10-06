namespace ProspectionCrm.Api.Entities;

public class SourceExecutionItemSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid SourceExecutionItemId { get; set; }
    public Guid? OpportunitySourceId { get; set; }
    public Guid OpportunitySourceIdSnapshot { get; set; }
    public required string RoleCode { get; set; }

    public SourceExecutionItem SourceExecutionItem { get; set; } = null!;
    public OpportunitySource? OpportunitySource { get; set; }
}
