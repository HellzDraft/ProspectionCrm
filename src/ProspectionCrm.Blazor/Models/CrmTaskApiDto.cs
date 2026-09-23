namespace ProspectionCrm.Blazor.Models;

public class CrmTaskApiDto
{
    public Guid Id { get; set; }
    public Guid OpportunityId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
