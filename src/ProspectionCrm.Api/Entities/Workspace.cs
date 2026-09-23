namespace ProspectionCrm.Api.Entities;

public class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerUserId { get; set; }
    public required string Name { get; set; }
    public required string TimeZoneId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public UserAccount OwnerUser { get; set; } = null!;
    public ICollection<Company> Companies { get; set; } = new List<Company>();
    public ICollection<Contact> Contacts { get; set; } = new List<Contact>();
    public ICollection<Pipeline> Pipelines { get; set; } = new List<Pipeline>();
    public ICollection<Opportunity> Opportunities { get; set; } = new List<Opportunity>();
    public ICollection<SourceConfiguration> SourceConfigurations { get; set; } = new List<SourceConfiguration>();
    public ICollection<SavedSearch> SavedSearches { get; set; } = new List<SavedSearch>();
    public ICollection<SourceExecution> SourceExecutions { get; set; } = new List<SourceExecution>();
    public ICollection<Campaign> Campaigns { get; set; } = new List<Campaign>();
}
