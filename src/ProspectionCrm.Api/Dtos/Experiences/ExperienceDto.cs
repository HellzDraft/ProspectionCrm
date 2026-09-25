namespace ProspectionCrm.Api.Dtos.Experiences;

public class ExperienceDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OrganizationName { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
