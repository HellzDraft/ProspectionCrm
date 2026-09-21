using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Helpers;

public static class OpportunityDisplayHelper
{
	public static string GetStatusLabel(OpportunityStatus status)
	{
		return status switch
		{
			OpportunityStatus.ToReview => "À étudier",
			OpportunityStatus.ToApply => "À candidater",
			OpportunityStatus.ApplicationSent => "Candidature envoyée",
			OpportunityStatus.ResponseReceived => "Réponse reçue",
			OpportunityStatus.Interview => "Entretien",
			OpportunityStatus.TestOrInterview => "Test / Entretien",
			OpportunityStatus.Offer => "Offre",
			OpportunityStatus.Accepted => "Acceptée",

			OpportunityStatus.Prospect => "Prospect",
			OpportunityStatus.ToContact => "À contacter",
			OpportunityStatus.Contacted => "Contacté",
			OpportunityStatus.Discussion => "Échange",
			OpportunityStatus.Proposal => "Proposition",
			OpportunityStatus.Negotiation => "Négociation",
			OpportunityStatus.Mission => "Mission",
			OpportunityStatus.Completed => "Terminée",

			OpportunityStatus.TargetIdentified => "Cible identifiée",
			OpportunityStatus.InterestConfirmed => "Intérêt confirmé",
			OpportunityStatus.Collaboration => "Collaboration",

			OpportunityStatus.Rejected => "Refusée",
			OpportunityStatus.NoResponse => "Sans réponse",
			OpportunityStatus.Abandoned => "Abandonnée",

			_ => "Inconnu"
		};
	}

	public static string GetPriorityLabel(OpportunityPriority priority)
	{
		return priority switch
		{
			OpportunityPriority.Low => "Faible",
			OpportunityPriority.Normal => "Normale",
			OpportunityPriority.High => "Haute",
			_ => "Inconnue"
		};
	}

	public static string GetPipelineLabel(PipelineType pipeline)
	{
		return pipeline switch
		{
			PipelineType.DotNet => ".NET",
			PipelineType.MaltFreelance => "Malt / Freelance",
			PipelineType.GameJob => "Emploi jeu vidéo",
			PipelineType.GameBusiness => "Business jeu vidéo",
			_ => "Inconnu"
		};
	}
}