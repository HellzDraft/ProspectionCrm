using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Blazor.Models;

public class CompanyApiFormModel
{
    [Required(ErrorMessage = "Le champ nom est obligatoire.")]
    [MaxLength(200, ErrorMessage = "Le champ nom ne doit pas dépasser 200 caractères.")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2048, ErrorMessage = "Le champ site web ne doit pas dépasser 2048 caractères.")]
    public string? Website { get; set; }

    [MaxLength(200, ErrorMessage = "Le champ localisation ne doit pas dépasser 200 caractères.")]
    public string? Location { get; set; }
}
