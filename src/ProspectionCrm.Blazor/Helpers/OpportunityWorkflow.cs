using ProspectionCrm.Blazor.Models;

namespace ProspectionCrm.Blazor.Helpers;

public static class OpportunityWorkflow
{
	public static IReadOnlyList<OpportunityStatus> GetAllowedStatuses(PipelineType pipeline)
	{
		return pipeline switch
		{
			PipelineType.DotNet =>
			[
				OpportunityStatus.ToReview,
				OpportunityStatus.ToApply,
				OpportunityStatus.ApplicationSent,
				OpportunityStatus.ResponseReceived,
				OpportunityStatus.Interview,
				OpportunityStatus.Offer,
				OpportunityStatus.Accepted,
				OpportunityStatus.Rejected,
				OpportunityStatus.Abandoned
			],

			PipelineType.MaltFreelance =>
			[
				OpportunityStatus.Prospect,
				OpportunityStatus.ToContact,
				OpportunityStatus.Contacted,
				OpportunityStatus.Discussion,
				OpportunityStatus.Proposal,
				OpportunityStatus.Negotiation,
				OpportunityStatus.Mission,
				OpportunityStatus.Completed,
				OpportunityStatus.Rejected,
				OpportunityStatus.Abandoned
			],

			PipelineType.GameJob =>
			[
				OpportunityStatus.ToReview,
				OpportunityStatus.ToApply,
				OpportunityStatus.ApplicationSent,
				OpportunityStatus.ResponseReceived,
				OpportunityStatus.TestOrInterview,
				OpportunityStatus.Offer,
				OpportunityStatus.Accepted,
				OpportunityStatus.Rejected,
				OpportunityStatus.Abandoned
			],

			PipelineType.GameBusiness =>
			[
				OpportunityStatus.TargetIdentified,
				OpportunityStatus.ToContact,
				OpportunityStatus.Contacted,
				OpportunityStatus.Discussion,
				OpportunityStatus.InterestConfirmed,
				OpportunityStatus.Proposal,
				OpportunityStatus.Negotiation,
				OpportunityStatus.Collaboration,
				OpportunityStatus.Rejected,
				OpportunityStatus.NoResponse,
				OpportunityStatus.Abandoned
			],

			_ => []
		};
	}
}