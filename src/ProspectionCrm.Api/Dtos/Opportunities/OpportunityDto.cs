namespace ProspectionCrm.Api.Dtos.Opportunities;

public class OpportunityDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public Guid? ContactId { get; set; }
    public Guid PipelineStageId { get; set; }
    public string PriorityCode { get; set; } = string.Empty;
    public decimal? Score { get; set; }
    public DateTimeOffset? ScoredAt { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
