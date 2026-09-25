namespace ProspectionCrm.Api.Dtos.Educations;

public class EducationDto
{
    public Guid Id { get; set; }
    public string InstitutionName { get; set; } = string.Empty;
    public string? Degree { get; set; }
    public string? FieldOfStudy { get; set; }
    public string? Description { get; set; }
    public DateOnly? StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
