namespace ProspectionCrm.Api.Entities;

public class PipelineStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PipelineId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public required string CategoryCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public Pipeline Pipeline { get; set; } = null!;
    public ICollection<Opportunity> Opportunities { get; set; } = new List<Opportunity>();
}
