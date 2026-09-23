using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Blazor.Models;

public class OpportunityApiFormModel
{
    [Required(ErrorMessage = "Le titre est obligatoire.")]
    [MaxLength(200, ErrorMessage = "Le titre ne doit pas dépasser 200 caractères.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Le pipeline est obligatoire.")]
    public Guid? PipelineId { get; set; }

    [Required(ErrorMessage = "L'étape est obligatoire.")]
    public Guid? PipelineStageId { get; set; }

    [Required(ErrorMessage = "La priorité est obligatoire.")]
    [MaxLength(50, ErrorMessage = "La priorité ne doit pas dépasser 50 caractères.")]
    [RegularExpression("^(low|normal|high)$", ErrorMessage = "La priorité doit être faible, normale ou haute.")]
    public string PriorityCode { get; set; } = "normal";

    [MaxLength(200, ErrorMessage = "La localisation ne doit pas dépasser 200 caractères.")]
    public string? Location { get; set; }
}
