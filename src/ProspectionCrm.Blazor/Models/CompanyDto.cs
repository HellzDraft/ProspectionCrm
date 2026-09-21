namespace ProspectionCrm.Blazor.Models;

public class CompanyDto
{
	public int Id { get; set; }

	public string Name { get; set; } = string.Empty;

	public string Type { get; set; } = string.Empty;

	public string Industry { get; set; } = string.Empty;

	public string Location { get; set; } = string.Empty;

	public string Website { get; set; } = string.Empty;

	public string Notes { get; set; } = string.Empty;
}