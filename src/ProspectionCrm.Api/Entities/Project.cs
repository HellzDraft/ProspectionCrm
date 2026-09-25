namespace ProspectionCrm.Api.Entities;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Role { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public ICollection<ProjectSkill> Skills { get; set; } = new List<ProjectSkill>();
}
