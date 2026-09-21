using System.Net.NetworkInformation;

namespace ProspectionCrm.Blazor.Models;

public class OpportunityDto
{
	public int Id { get; set; }

	public string Title { get; set; } = string.Empty;

	public string CompanyName { get; set; } = string.Empty;

	public PipelineType Pipeline { get; set; }

	public OpportunityStatus Status { get; set; }

	public OpportunityPriority Priority { get; set; }

	public string Location { get; set; } = string.Empty;

	public int CompanyId { get; set; }
}