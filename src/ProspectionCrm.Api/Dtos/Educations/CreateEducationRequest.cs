using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Educations;

public class CreateEducationRequest
{
    [Required]
    [MaxLength(200)]
    public string InstitutionName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Degree { get; set; }

    [MaxLength(200)]
    public string? FieldOfStudy { get; set; }

    [MaxLength(10000)]
    public string? Description { get; set; }

    public DateOnly? StartedOn { get; set; }

    public DateOnly? EndedOn { get; set; }
}
