using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Experiences;

public class UpdateExperienceRequest
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? OrganizationName { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(10000)]
    public string? Description { get; set; }

    public DateOnly? StartedOn { get; set; }

    public DateOnly? EndedOn { get; set; }
}
