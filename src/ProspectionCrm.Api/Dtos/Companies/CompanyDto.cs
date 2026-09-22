namespace ProspectionCrm.Api.Dtos.Companies;

public class CompanyDto
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Website { get; init; }
    public string? Location { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
