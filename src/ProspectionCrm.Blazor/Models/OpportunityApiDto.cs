namespace ProspectionCrm.Blazor.Models;

public class OpportunityApiDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public Guid? ContactId { get; set; }
    public string PipelineCode { get; set; } = string.Empty;
    public string StatusCode { get; set; } = string.Empty;
    public string PriorityCode { get; set; } = string.Empty;
    public string? Location { get; set; }
    public string? SourceName { get; set; }
    public string? SourceUrl { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? FollowUpDueAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
