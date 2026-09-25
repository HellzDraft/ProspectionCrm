namespace ProspectionCrm.Api.Entities;

public class Opportunity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid PipelineStageId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? ContactId { get; set; }
    public required string Title { get; set; }
    public required string PriorityCode { get; set; }
    public decimal? Score { get; set; }
    public DateTimeOffset? ScoredAt { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public PipelineStage PipelineStage { get; set; } = null!;
    public Company? Company { get; set; }
    public Contact? Contact { get; set; }
    public ICollection<CrmTask> CrmTasks { get; set; } = new List<CrmTask>();
    public ICollection<OpportunitySource> OpportunitySources { get; set; } = new List<OpportunitySource>();
    public ICollection<CampaignOpportunity> CampaignOpportunities { get; set; } = new List<CampaignOpportunity>();
    public ICollection<Application> Applications { get; set; } = new List<Application>();
    public ICollection<Proposal> Proposals { get; set; } = new List<Proposal>();
    public ICollection<EmailMessage> EmailMessages { get; set; } = new List<EmailMessage>();
    public ICollection<CalendarEvent> CalendarEvents { get; set; } = new List<CalendarEvent>();
    public ICollection<ActivityEntry> ActivityEntries { get; set; } = [];
}
