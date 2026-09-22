using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Blazor.Models;

public class OpportunityApiFormModel
{
    [Required(ErrorMessage = "Le titre est obligatoire.")]
    [MaxLength(200, ErrorMessage = "Le titre ne doit pas dépasser 200 caractères.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Le pipeline est obligatoire.")]
    [MaxLength(50, ErrorMessage = "Le pipeline ne doit pas dépasser 50 caractères.")]
    public string PipelineCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Le statut est obligatoire.")]
    [MaxLength(50, ErrorMessage = "Le statut ne doit pas dépasser 50 caractères.")]
    public string StatusCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "La priorité est obligatoire.")]
    [MaxLength(50, ErrorMessage = "La priorité ne doit pas dépasser 50 caractères.")]
    public string PriorityCode { get; set; } = "normal";

    [MaxLength(200, ErrorMessage = "La localisation ne doit pas dépasser 200 caractères.")]
    public string? Location { get; set; }
}
