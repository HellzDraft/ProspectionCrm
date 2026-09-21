using System.ComponentModel.DataAnnotations;

namespace ProspectionCrm.Blazor.Models;

public class OpportunityFormModel
{
	[Required(ErrorMessage = "Le titre est obligatoire.")]
	public string Title { get; set; } = string.Empty;

	[Required(ErrorMessage = "L'entreprise est obligatoire.")]
	public string CompanyName { get; set; } = string.Empty;

	[Required(ErrorMessage = "Le pipeline est obligatoire.")]
	public PipelineType? Pipeline { get; set; }

	[Required(ErrorMessage = "Le statut est obligatoire.")]
	public OpportunityStatus? Status { get; set; }

	public OpportunityPriority Priority { get; set; } = OpportunityPriority.Normal;

	public string Location { get; set; } = string.Empty;
}