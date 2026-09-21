namespace ProspectionCrm.Api.Entities;

public class CrmTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OpportunityId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Opportunity Opportunity { get; set; } = null!;
}
