namespace ProspectionCrm.Api.Entities;

public class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Website { get; set; }
    public string? Location { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public ICollection<Contact> Contacts { get; set; } = new List<Contact>();
    public ICollection<Opportunity> Opportunities { get; set; } = new List<Opportunity>();
}
