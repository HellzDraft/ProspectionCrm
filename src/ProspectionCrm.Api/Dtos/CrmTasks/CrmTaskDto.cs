namespace ProspectionCrm.Api.Dtos.CrmTasks;

public class CrmTaskDto
{
    public Guid Id { get; init; }
    public Guid OpportunityId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public bool IsCompleted { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
