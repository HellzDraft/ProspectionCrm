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
    public ICollection<EmailMessage> EmailMessages { get; set; } = new List<EmailMessage>();
    public ICollection<CalendarEvent> CalendarEvents { get; set; } = new List<CalendarEvent>();
    public ICollection<Document> Documents { get; set; } = new List<Document>();
    public ICollection<CandidateProfile> CandidateProfiles { get; set; } = new List<CandidateProfile>();
    public ICollection<Experience> Experiences { get; set; } = new List<Experience>();
    public ICollection<Education> Educations { get; set; } = new List<Education>();
    public ICollection<Project> Projects { get; set; } = new List<Project>();
    public ICollection<Skill> Skills { get; set; } = new List<Skill>();
    public ICollection<ScoringRule> ScoringRules { get; set; } = [];
    public ICollection<AutomationRule> AutomationRules { get; set; } = [];
    public ICollection<AutomationExecution> AutomationExecutions { get; set; } = [];
    public ICollection<AiModelConfiguration> AiModelConfigurations { get; set; } = [];
    public ICollection<AiPromptTemplate> AiPromptTemplates { get; set; } = [];
    public ICollection<ActivityEntry> ActivityEntries { get; set; } = [];
}
