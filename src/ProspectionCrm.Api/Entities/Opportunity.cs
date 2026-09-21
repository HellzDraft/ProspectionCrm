namespace ProspectionCrm.Api.Entities;

public class Opportunity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ContactId { get; set; }
    public string? Status { get; set; }
    public string? Location { get; set; }
    public string? SourceName { get; set; }
    public string? SourceUrl { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? FollowUpDueAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public Company? Company { get; set; }
    public Contact? Contact { get; set; }
    public ICollection<CrmTask> CrmTasks { get; set; } = new List<CrmTask>();
}
