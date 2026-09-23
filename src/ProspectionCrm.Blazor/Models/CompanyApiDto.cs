namespace ProspectionCrm.Blazor.Models;

public class CompanyApiDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Website { get; set; }

    public string? Location { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
