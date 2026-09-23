using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Blazor.Models;

public class ContactApiFormModel
{
    public Guid? CompanyId { get; set; }

    [Required(ErrorMessage = "Le champ prénom est obligatoire.")]
    [MaxLength(100, ErrorMessage = "Le champ prénom ne doit pas dépasser 100 caractères.")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Le champ nom est obligatoire.")]
    [MaxLength(100, ErrorMessage = "Le champ nom ne doit pas dépasser 100 caractères.")]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(254, ErrorMessage = "Le champ email ne doit pas dépasser 254 caractères.")]
    public string? Email { get; set; }

    [MaxLength(50, ErrorMessage = "Le champ téléphone ne doit pas dépasser 50 caractères.")]
    public string? Phone { get; set; }

    [MaxLength(200, ErrorMessage = "Le champ fonction ne doit pas dépasser 200 caractères.")]
    public string? JobTitle { get; set; }

    [MaxLength(2048, ErrorMessage = "Le champ linkedin ne doit pas dépasser 2048 caractères.")]
    public string? LinkedInUrl { get; set; }
}
