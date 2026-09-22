using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Api.Dtos.Companies;

public class CreateCompanyRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2048)]
    public string? Website { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }
}
