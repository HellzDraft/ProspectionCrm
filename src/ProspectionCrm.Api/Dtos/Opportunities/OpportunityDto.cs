namespace ProspectionCrm.Api.Dtos.Opportunities;

public class OpportunityDto
{
    public Guid Id { get; init; }
    public required string Title { get; init; }
    public Guid? CompanyId { get; init; }
    public Guid? ContactId { get; init; }
    public required string PipelineCode { get; init; }
    public required string StatusCode { get; init; }
    public required string PriorityCode { get; init; }
    public string? Location { get; init; }
    public string? SourceName { get; init; }
    public string? SourceUrl { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? FollowUpDueAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
