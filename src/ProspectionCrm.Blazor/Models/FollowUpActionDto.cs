namespace ProspectionCrm.Blazor.Models;

public class FollowUpActionDto
{
	public int Id { get; set; }

	public string Title { get; set; } = string.Empty;

	public DateTime DueDate { get; set; }

	public bool IsCompleted { get; set; }

	public int? OpportunityId { get; set; }

	public string OpportunityTitle { get; set; } = string.Empty;

	public string Notes { get; set; } = string.Empty;
}
