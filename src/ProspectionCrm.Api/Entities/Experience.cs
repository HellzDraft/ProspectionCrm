namespace ProspectionCrm.Api.Entities;

public class Experience
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Title { get; set; }
    public string? OrganizationName { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
}
