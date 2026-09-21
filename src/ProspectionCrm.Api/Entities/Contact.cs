namespace ProspectionCrm.Api.Entities;

public class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? CompanyId { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }
    public string? LinkedInUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public Company? Company { get; set; }
    public ICollection<Opportunity> Opportunities { get; set; } = new List<Opportunity>();
}
