namespace ProspectionCrm.Api.Dtos.Contacts;

public class ContactDto
{
    public Guid Id { get; init; }
    public Guid? CompanyId { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? JobTitle { get; init; }
    public string? LinkedInUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
