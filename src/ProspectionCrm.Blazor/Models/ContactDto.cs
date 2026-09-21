namespace ProspectionCrm.Blazor.Models;

public class ContactDto
{
	public int Id { get; set; }

	public string FirstName { get; set; } = string.Empty;

	public string LastName { get; set; } = string.Empty;

	public string Role { get; set; } = string.Empty;

	public int CompanyId { get; set; }

	public string CompanyName { get; set; } = string.Empty;

	public string Email { get; set; } = string.Empty;

	public string Phone { get; set; } = string.Empty;

	public string LinkedInUrl { get; set; } = string.Empty;

	public string Notes { get; set; } = string.Empty;

}
