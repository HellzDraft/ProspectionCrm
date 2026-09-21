namespace ProspectionCrm.Blazor.Models;

public class DocumentReferenceDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public int? OpportunityId { get; set; }

    public string OpportunityTitle { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;
}