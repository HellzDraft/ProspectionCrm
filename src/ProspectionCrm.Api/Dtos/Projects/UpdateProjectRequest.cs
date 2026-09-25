using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Projects;

public class UpdateProjectRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(10000)]
    public string? Description { get; set; }

    [MaxLength(200)]
    public string? Role { get; set; }

    [MaxLength(2048)]
    public string? RepositoryUrl { get; set; }

    [MaxLength(2048)]
    public string? WebsiteUrl { get; set; }

    public DateOnly? StartedOn { get; set; }

    public DateOnly? EndedOn { get; set; }
}
