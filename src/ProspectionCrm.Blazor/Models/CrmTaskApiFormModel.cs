using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Blazor.Models;

public class CrmTaskApiFormModel
{
    [Required(ErrorMessage = "L'opportunité est obligatoire.")]
    public Guid? OpportunityId { get; set; }

    [Required(ErrorMessage = "Le titre est obligatoire.")]
    [MaxLength(200, ErrorMessage = "Le titre ne doit pas dépasser 200 caractères.")]
    public string Title { get; set; } = string.Empty;

    [MaxLength(10000, ErrorMessage = "La description ne doit pas dépasser 10000 caractères.")]
    public string? Description { get; set; }

    public DateTime? DueAtLocal { get; set; }
}
