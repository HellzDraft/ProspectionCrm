using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Skills;

public class UpdateSkillRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? CategoryCode { get; set; }
}
