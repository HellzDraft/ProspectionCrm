namespace ProspectionCrm.Api.Dtos.Projects;

public class ProjectDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Role { get; set; }
    public string? RepositoryUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public IReadOnlyList<Guid> SkillIds { get; set; } = [];
}
