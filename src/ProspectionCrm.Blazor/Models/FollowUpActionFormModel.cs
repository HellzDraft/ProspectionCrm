using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Blazor.Models;

public class FollowUpActionFormModel
{
    [Required(ErrorMessage = "Le titre est obligatoire.")]
    [StringLength(150, ErrorMessage = "Le titre ne peut pas dépasser 150 caractères.")]
    public string Title { get; set; } = string.Empty;

    public DateTime DueDate { get; set; } = DateTime.Today;

    public string Notes { get; set; } = string.Empty;

	public int? OpportunityId { get; set; }
}