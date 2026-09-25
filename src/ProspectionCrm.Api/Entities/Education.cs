namespace ProspectionCrm.Api.Entities;

public class Education
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string InstitutionName { get; set; }
    public string? Degree { get; set; }
    public string? FieldOfStudy { get; set; }
    public string? Description { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
}
