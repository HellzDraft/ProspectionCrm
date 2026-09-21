namespace ProspectionCrm.Blazor.Models;

public enum OpportunityStatus
{
	ToReview,
	ToApply,
	ApplicationSent,
	ResponseReceived,
	Interview,
	TestOrInterview,
	Offer,
	Accepted,

	Prospect,
	ToContact,
	Contacted,
	Discussion,
	Proposal,
	Negotiation,
	Mission,
	Completed,

	TargetIdentified,
	InterestConfirmed,
	Collaboration,

	Rejected,
	NoResponse,
	Abandoned
}